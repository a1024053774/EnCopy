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
    private TracerPanel? _panel;
    private PasteHint? _pasteHint;
    private Paster? _paster;
    private HotKey? _hotKey;
    private TrayIcon? _trayIcon;

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

        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        _history = new History();
        _line = new Line(_history);

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

        _panel = new TracerPanel(
            _line,
            onPaste: (note, targetHwnd, plain) => _paster.Paste(note, targetHwnd, plain),
            onVisibilityChanged: () => _trayIcon?.UpdateState(_panel?.IsSummoned ?? false, _settings.RecordingPaused));

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
            onQuit: () => Shutdown());

        // 全局唯一热键注册处（Alt+C）；被占用时不崩溃，提醒用户并保留托盘唤起。
        _hotKey = new HotKey(Shortcut.Standard, () => _panel.SummonOrToggle());
        if (!_hotKey.IsRegistered)
        {
            _panel.SetConflictNotice(Localization.Format("Shortcut {0} is in use by another app.", Shortcut.Standard.Display));
            _trayIcon.ShowHotKeyConflict(Shortcut.Standard);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
