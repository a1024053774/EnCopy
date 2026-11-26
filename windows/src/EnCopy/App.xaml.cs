using System.IO;
using System.Windows;

namespace EnCopy;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private SettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private History? _history;
    private Line? _line;
    private Win32ClipboardBackend? _clipboardBackend;
    private ClipboardWatcher? _clipboardWatcher;
    private LinePanel? _panel;
    private PasteHint? _pasteHint;
    private Paster? _paster;
    private HotKey? _hotKey;
    private TrayIcon? _trayIcon;
    private ShortcutRecorderWindow? _recorder;

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
        _line = new Line(_history, keepOnLine: _settings.NormalizedKeepOnLine);
        var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
        var initialStrip = ScreenPlacement.TopLineStrip(cx, cy, ScreenPlacement.EnumerateMonitors(), 300.0);
        _line.SetVisibleCapacity(ScreenPlacement.ComputeVisibleCapacity(initialStrip.Width));

        _clipboardBackend = new Win32ClipboardBackend();
        _clipboardWatcher = new ClipboardWatcher(
            _clipboardBackend,
            _history,
            _settings,
            onNew: path => _line.AddNew(path),
            onAgain: (path, stampUtc) => _line.Use(path, stampUtc));
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
            onRequestChangeShortcut: OpenShortcutRecorder);

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
            onSetKeepOnLine: keep =>
            {
                _settings.KeepOnLine = keep;
                _settingsStore.Save(_settings);
                _line.SetKeepOnLine(keep);
            },
            getKeepOnLine: () => _settings.NormalizedKeepOnLine,
            onOpenShortcutRecorder: OpenShortcutRecorder,
            getActiveShortcut: () => _settings.GetActiveShortcut(),
            onQuit: () => Shutdown());

        var activeShortcut = _settings.GetActiveShortcut();
        if (activeShortcut is { } s && !ApplyHotKey(s))
        {
            _trayIcon.ShowHotKeyConflict(s);
        }
    }

    private void RefreshTrayState()
    {
        _trayIcon?.UpdateState(
            _panel?.IsSummoned ?? false,
            _settings.RecordingPaused,
            _settings.NormalizedKeepOnLine,
            _settings.GetActiveShortcut());
    }

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
        // 先释放当前热键，避免自己占用自己的旧组合键导致无法重新录回原键
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
        _recorder?.CloseSilently();
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
}
