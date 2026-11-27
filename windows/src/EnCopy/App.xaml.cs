using System.Diagnostics;
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
    private SearchIndex? _searchIndex;
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
    private SettingsWindow? _settingsWindow;
    private CleanupWindow? _cleanupWindow;
    private OnboardingWindow? _onboardingWindow;
    private DispatcherTimer? _topHoverTimer;
    private DispatcherTimer? _expiryTimer;
    private readonly TopEdgeHoverTracker _hoverTracker = new();
    private readonly ForegroundAppTracker _foregroundTracker = new();
    private WinEventDelegate? _winEventDelegate;
    private IntPtr _foregroundHook;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

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
            Theme = _settings.GetActiveTheme(),
            ShowsSourceApp = _settings.ShowsSourceApp
        };

        var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
        var initialStrip = ScreenPlacement.TopLineStrip(cx, cy, ScreenPlacement.EnumerateMonitors(), 300.0);
        _line.SetVisibleCapacity(ScreenPlacement.ComputeVisibleCapacity(initialStrip.Width));

        _line.ClearExpired();
        RunRetentionIfDue();

        _searchIndex = new SearchIndex(
            _history,
            onImageIndexed: _ => Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => _line?.RefreshAfterIndexUpdate())));
        _searchIndex.ScanUnindexedImages();

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
            onRequestChangeShortcut: () => OpenShortcutRecorder(),
            heldManager: _heldManager,
            shareService: _shareService,
            getZoomOnHover: () => _settings.ZoomOnHover,
            onRequestOpenCleanup: OpenCleanupWindow);

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
                _settingsWindow?.Rebuild();
            },
            getTheme: () => _line.Theme,
            onSetZoomOnHover: zoom =>
            {
                _settings.ZoomOnHover = zoom;
                _settingsStore.Save(_settings);
                _settingsWindow?.Rebuild();
            },
            getZoomOnHover: () => _settings.ZoomOnHover,
            onHangAllBack: () => _heldManager.HangAllBack(),
            hasHeldItems: () => _heldManager.HasOpenWindows,
            onOpenShortcutRecorder: () => OpenShortcutRecorder(),
            getActiveShortcut: () => _settings.GetActiveShortcut(),
            onQuit: () => Shutdown(),
            onOpenCleanup: OpenCleanupWindow,
            onOpenSettings: OpenSettingsWindow);

        var activeShortcut = _settings.GetActiveShortcut();
        if (activeShortcut is { } s && !ApplyHotKey(s))
        {
            _trayIcon.ShowHotKeyConflict(s);
        }

        _topHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _topHoverTimer.Tick += (_, _) => PollTopEdgeHover();
        _topHoverTimer.Start();

        _foregroundTracker.ObserveLiveForeground();
        _winEventDelegate = (_, _, hwnd, _, _, _, _) => _foregroundTracker.ObserveWindow(hwnd);
        _foregroundHook = SetWinEventHook(0x0003u, 0x0003u, IntPtr.Zero, _winEventDelegate, 0, 0, 0x0000u | 0x0002u);

        _expiryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _expiryTimer.Tick += (_, _) =>
        {
            // 用户正在面板中挑选时暂不抽走脚下的卡片（对齐 AppDelegate.swift:100）
            if (_panel is { IsSummoned: true }) return;
            _line?.ClearExpired();
            RunRetentionIfDue();
        };
        _expiryTimer.Start();

        if (!_settings.RecordingExplained && !IsInChecksRunner())
        {
            var welcomeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            welcomeTimer.Tick += (_, _) =>
            {
                welcomeTimer.Stop();
                ShowFirstLaunchOnboarding();
            };
            welcomeTimer.Start();
        }
    }

    private void HandleNewCopiedItem(string path)
    {
        if (_line is null) return;
        _searchIndex?.Enqueue(path);
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
        _foregroundTracker.ObserveLiveForeground();
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

        if (_panel.IsPeeking && mon.Bounds.Contains(cx, cy) && cy <= _panel.PanelBottomPhysicalY)
        {
            _panel.PromotePeekToHover();
        }

        var action = _hoverTracker.Update(
            cx,
            cy,
            mon,
            mouseDown,
            isRevealed: _panel.IsSummoned,
            isPinnedBySummon: _panel.IsPinnedBySummon,
            keepOpenForMenuOrDrag: _panel.IsInteractionHoldActive || _panel.IsPeeking,
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

    private void OpenShortcutRecorder(Action? onFinished = null)
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
                _settingsWindow?.Rebuild();
            });
        _recorder.Closed += (_, _) =>
        {
            _recorder = null;
            onFinished?.Invoke();
        };
        _recorder.Show();
        _recorder.Activate();
    }

    private void SaveAndApplySettings()
    {
        _settingsStore?.Save(_settings);
        if (_clipboardWatcher is not null)
        {
            if (_clipboardWatcher.IsEnabled) _clipboardWatcher.Start();
            else _clipboardWatcher.Stop();
        }
        RefreshTrayState();
    }

    private void RunRetentionIfDue()
    {
        if (_line is null || _settingsStore is null) return;
        double nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (nowSec - _settings.RetentionLastRunUnix < 86400.0) return;
        _settings.RetentionLastRunUnix = nowSec;
        _settingsStore.Save(_settings);
        _line.RunRetention(DateTime.UtcNow, _settings.NormalizedOriginalsCapBytes);
    }

    private void OpenSettingsWindow()
    {
        var frontApp = _foregroundTracker.ObserveLiveForeground();
        if (_panel is { IsSummoned: true })
        {
            _panel.Dismiss(restoreForeground: false);
        }

        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.RefreshWithFrontApp(frontApp);
            _settingsWindow.Show();
            _settingsWindow.Activate();
            return;
        }

        if (_line is null) return;
        _settingsWindow = new SettingsWindow(
            _settings,
            _line,
            new SettingsCallbacks(
                SaveAndApplySettings: SaveAndApplySettings,
                ChangeShortcut: after => OpenShortcutRecorder(after),
                OpenCleanup: OpenCleanupWindow,
                OpenScreenshotsFolder: () => OpenFolderInExplorer(DataFolder.ScreenshotsFolder),
                OpenDataFolder: () => OpenFolderInExplorer(DataFolder.Root)),
            frontApp: frontApp,
            ocrStatus: _searchIndex?.LanguageStatus);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenCleanupWindow()
    {
        if (_panel is { IsSummoned: true })
        {
            _panel.Dismiss(restoreForeground: false);
        }

        if (_cleanupWindow is { IsLoaded: true })
        {
            _cleanupWindow.RefreshPreviewAndBatches();
            _cleanupWindow.Show();
            _cleanupWindow.Activate();
            return;
        }

        if (_line is null) return;
        _cleanupWindow = new CleanupWindow(_line);
        _cleanupWindow.Closed += (_, _) => _cleanupWindow = null;
        _cleanupWindow.Show();
        _cleanupWindow.Activate();
    }

    private void ShowFirstLaunchOnboarding()
    {
        if (_onboardingWindow is { IsLoaded: true } || _line is null) return;
        _onboardingWindow = new OnboardingWindow(_line.Theme, (paused, openAtLogin) =>
        {
            _settings.RecordingExplained = true;
            _settings.RecordingPaused = paused;
            LaunchAtLogin.SetEnabled(openAtLogin);
            SaveAndApplySettings();
        });
        _onboardingWindow.Closed += (_, _) => _onboardingWindow = null;
        _onboardingWindow.Show();
        _onboardingWindow.Activate();
    }

    private static void OpenFolderInExplorer(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static bool IsInChecksRunner() =>
        string.Equals(Environment.GetEnvironmentVariable(DataFolder.ChecksRunnerEnv), "1", StringComparison.Ordinal);

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
        _expiryTimer?.Stop();
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        _winEventDelegate = null;
        _recorder?.CloseSilently();
        _onboardingWindow?.Close();
        _cleanupWindow?.Close();
        _settingsWindow?.Close();
        _heldManager?.Dispose();
        _shareService?.Dispose();
        _hotKey?.Dispose();
        _clipboardWatcher?.Dispose();
        _clipboardBackend?.Dispose();
        _searchIndex?.Dispose();
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

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
