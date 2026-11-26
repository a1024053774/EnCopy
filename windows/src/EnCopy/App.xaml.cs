using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EnCopy;

public partial class App : Application
{
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private SettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private History? _history;
    private Line? _line;
    private Win32ClipboardBackend? _clipboardBackend;
    private ClipboardWatcher? _clipboardWatcher;
    private HeldWindowManager? _heldManager;
    private WindowsShareService? _shareService;
    private LinePanel? _panel;
    private PasteHint? _pasteHint;
    private Paster? _paster;
    private HotKey? _hotKey;
    private TrayIcon? _trayIcon;
    private ShortcutRecorderWindow? _recorder;
    private DispatcherTimer? _topHoverTimer;
    private readonly TopEdgeHoverTracker _hoverTracker = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, AppIdentity.MutexName, out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        CleanupOrphanedDiscarded();

        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        _history = new History();
        _line = new Line(_history, keepOnLine: _settings.NormalizedKeepOnLine)
        {
            Theme = _settings.GetActiveTheme()
        };

        var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
        var initialStrip = ScreenPlacement.TopLineStrip(cx, cy, ScreenPlacement.EnumerateMonitors(), 300.0);
        _line.SetVisibleCapacity(ScreenPlacement.ComputeVisibleCapacity(initialStrip.Width));

        _heldManager = new HeldWindowManager();
        _shareService = new WindowsShareService();

        _clipboardBackend = new Win32ClipboardBackend();
        _clipboardWatcher = new ClipboardWatcher(
            _clipboardBackend,
            _history,
            _settings,
            onNew: HandleNewCopiedItem,
            onAgain: HandleRepeatedCopiedItem);
        if (_clipboardWatcher.IsEnabled)
        {
            _clipboardWatcher.Start();
        }

        _pasteHint = new PasteHint();
        _paster = new Paster(
            _line,
            new Win32PastePlatform(
                _clipboardBackend,
                tuckAndRestore: targetHwnd => _panel?.Dismiss(restoreForeground: true, targetOverride: targetHwnd),
                showHint: () => _pasteHint.Show()));

        _panel = new LinePanel(
            _line,
            onPasteNotes: (notes, targetHwnd, plain) => _paster.PasteNotes(notes, targetHwnd, plain),
            onCopyNotes: notes => _paster.WriteMultipleToClipboard(notes, plain: false),
            onVisibilityChanged: RefreshTrayState,
            onRequestChangeShortcut: OpenShortcutRecorder,
            heldManager: _heldManager,
            shareService: _shareService,
            getZoomOnHover: () => _settings.ZoomOnHover);

        _line.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(Line.ShownItems), StringComparison.Ordinal) ||
                string.Equals(args.PropertyName, nameof(Line.TotalCount), StringComparison.Ordinal))
            {
                _trayIcon?.Refresh();
            }
        };

        _trayIcon = new TrayIcon(
            onToggleSummon: () => _panel.SummonOrToggleFromTray(),
            isSummoned: () => _panel.IsSummoned,
            onSetPaused: paused =>
            {
                _settings.RecordingPaused = paused;
                _settingsStore.Save(_settings);
                if (_clipboardWatcher.IsEnabled) _clipboardWatcher.Start();
                else _clipboardWatcher.Stop();
            },
            isPaused: () => _settings.RecordingPaused,
            getItemCount: () => _line.LineItems.Count,
            onSetKeepOnLine: keep =>
            {
                _settings.KeepOnLine = keep;
                _settingsStore.Save(_settings);
                _line.SetKeepOnLine(keep);
            },
            getKeepOnLine: () => _settings.NormalizedKeepOnLine,
            onSetTheme: theme =>
            {
                _settings.SetActiveTheme(theme);
                _settingsStore.Save(_settings);
                _line.Theme = theme;
            },
            getTheme: () => _line.Theme,
            onSetZoomOnHover: zoom =>
            {
                _settings.ZoomOnHover = zoom;
                _settingsStore.Save(_settings);
            },
            getZoomOnHover: () => _settings.ZoomOnHover,
            onHangAllBack: () => _heldManager.HangAllBack(),
            hasHeldItems: () => _heldManager.HasOpenWindows,
            onOpenShortcutRecorder: OpenShortcutRecorder,
            getActiveShortcut: () => _settings.GetActiveShortcut(),
            onQuit: () => Shutdown());

        var activeShortcut = _settings.GetActiveShortcut();
        if (activeShortcut is { } s && !ApplyHotKey(s))
        {
            _trayIcon.ShowHotKeyConflict(s);
        }

        _topHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _topHoverTimer.Tick += (_, _) => PollTopEdgeHover();
        _topHoverTimer.Start();
    }

    private void HandleNewCopiedItem(string path)
    {
        if (_line is null) return;
        _line.AddNew(path);
        if (_panel is { IsSummoned: true })
        {
            _panel.NotifyNewCardHung(path);
            _trayIcon?.Refresh();
            return;
        }

        _trayIcon?.TriggerCopySway();

        if (_settings.PeeksAtNew && _panel is not null)
        {
            var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
            var mon = ScreenPlacement.PickMonitor(cx, cy, ScreenPlacement.EnumerateMonitors());
            var selfHwnd = new WindowInteropHelper(_panel).Handle;
            if (!FullScreenDetector.BlocksLineOnMonitor(mon, _settings.ShowOverFullScreen, selfHwnd))
            {
                _panel.PeekNewItem(_settings.NormalizedPeekSeconds, path);
            }
        }
    }

    private void HandleRepeatedCopiedItem(string path, DateTime stampUtc)
    {
        if (_line is null) return;
        _line.Use(path, stampUtc);
        if (_panel is not { IsSummoned: true })
        {
            _trayIcon?.TriggerCopySway();
        }
    }

    private void PollTopEdgeHover()
    {
        if (_panel is null) return;
        if (!_settings.OpensFromTopEdge && !_panel.IsSummoned)
        {
            _hoverTracker.Reset();
            return;
        }

        var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
        var monitors = ScreenPlacement.EnumerateMonitors();
        var mon = _panel.IsSummoned ? _panel.ActiveMonitor : ScreenPlacement.PickMonitor(cx, cy, monitors);
        bool mouseDown = ((GetAsyncKeyState(VkLButton) & 0x8000) != 0) || ((GetAsyncKeyState(VkRButton) & 0x8000) != 0);

        var action = _hoverTracker.Update(
            cx,
            cy,
            mon,
            mouseDown,
            isRevealed: _panel.IsSummoned,
            isPinnedBySummon: _panel.IsPinnedBySummon,
            keepOpenForMenuOrDrag: _panel.IsInteractionHoldActive,
            panelBottomPhysicalY: _panel.PanelBottomPhysicalY,
            nowUtc: DateTime.UtcNow);

        if (action == HoverAction.RevealByHover && _settings.OpensFromTopEdge)
        {
            var selfHwnd = new WindowInteropHelper(_panel).Handle;
            if (!FullScreenDetector.BlocksLineOnMonitor(mon, _settings.ShowOverFullScreen, selfHwnd))
            {
                _panel.RevealByHover();
            }
        }
        else if (action == HoverAction.Retract)
        {
            _panel.Dismiss(restoreForeground: false, animateTuck: true);
        }
    }

    private void RefreshTrayState() => _trayIcon?.Refresh();

    /// <summary>
    /// 全应用唯一的热键实例化与切换入口，保证全局仅占用一个快捷键。
    /// </summary>
    private bool ApplyHotKey(Shortcut? shortcut)
    {
        _hotKey?.Dispose();
        _hotKey = null;

        if (shortcut is not { } s)
        {
            _panel?.SetConflictNotice(null);
            RefreshTrayState();
            return true;
        }

        // 全局唯一热键注册处；被占用时不崩溃，提醒用户并保留托盘唤起。
        _hotKey = new HotKey(s, () => _panel?.SummonOrToggle());
        bool ok = _hotKey.IsRegistered;
        _panel?.SetConflictNotice(ok ? null : Localization.Format("Shortcut {0} is in use by another app.", s.Display));
        RefreshTrayState();
        return ok;
    }

    private void OpenShortcutRecorder()
    {
        if (_panel is not null && _panel.IsSummoned)
        {
            _panel.Dismiss(restoreForeground: false);
        }

        if (_recorder is { IsVisible: true })
        {
            _recorder.Activate();
            return;
        }

        var current = _settings.GetActiveShortcut();
        _hotKey?.Dispose();
        _hotKey = null;

        _recorder = new ShortcutRecorderWindow(
            current,
            tryApplyOnly: ApplyHotKey,
            commitShortcut: chosen =>
            {
                _settings.SetActiveShortcut(chosen);
                _settingsStore?.Save(_settings);
                RefreshTrayState();
            });
        _recorder.Closed += (_, _) => _recorder = null;
        _recorder.Show();
        _recorder.Activate();
    }

    private static void CleanupOrphanedDiscarded()
    {
        var dir = DataFolder.DiscardedFolder;
        if (!Directory.Exists(dir)) return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 启动阶段最佳努力清理上次未撤销的暂存文件
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _topHoverTimer?.Stop();
        _recorder?.CloseSilently();
        _heldManager?.Dispose();
        _shareService?.Dispose();
        _hotKey?.Dispose();
        _clipboardWatcher?.Dispose();
        _clipboardBackend?.Dispose();
        _trayIcon?.Dispose();
        _history?.Dispose();
        if (_instanceMutex is not null)
        {
            if (_ownsMutex)
            {
                _instanceMutex.ReleaseMutex();
                _ownsMutex = false;
            }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
