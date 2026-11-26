using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EnCopy;

public sealed record ClipboardSnapshot(
    uint SequenceNumber,
    bool ContainsSelfCopy,
    bool ContainsFiles,
    bool IsPrivacyBlocked,
    string? Text,
    byte[]? RtfBytes);

public interface IClipboardBackend : IDisposable
{
    event EventHandler? Changed;
    uint SequenceNumber { get; }
    bool TryDequeueSnapshot(out ClipboardSnapshot? snapshot);
    ClipboardSnapshot? TryRead();
    bool TryWrite(string text, byte[]? rtfBytes);
}

/// <summary>
/// 监听系统剪贴板文本与链接并立即写入 DataFolder\Notes 与 History.sqlite；
/// 过滤自身写入（app.encopy.SelfCopy）与密码管理器/系统隐私标记，200ms 内连续复制两次均独立入库。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    public const string SelfCopyFormat = "app.encopy.SelfCopy";
    public static readonly string[] PrivacyFormats =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "Clipboard Viewer Ignore"
    ];
    public const string CanIncludeFormat = "CanIncludeInClipboardHistory";

    private const int MaxChars = 100_000;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly IClipboardBackend _backend;
    private readonly History _history;
    private readonly AppSettings _settings;
    private readonly Action<string> _onNew;
    private readonly Action<string, DateTime> _onAgain;

    private uint _lastSequence;
    private bool _started;
    private DateTime _lastStampUtc = DateTime.MinValue;

    public ClipboardWatcher(
        IClipboardBackend backend,
        History history,
        AppSettings settings,
        Action<string> onNew,
        Action<string, DateTime> onAgain)
    {
        _backend = backend;
        _history = history;
        _settings = settings;
        _onNew = onNew;
        _onAgain = onAgain;
        _lastSequence = backend.SequenceNumber;
    }

    public bool IsEnabled => !_settings.RecordingPaused && (_settings.TextEnabled || _settings.LinksEnabled);

    public void Start()
    {
        if (_started) return;
        _started = true;
        while (_backend.TryDequeueSnapshot(out _)) { }
        _lastSequence = _backend.SequenceNumber;
        _backend.Changed += OnBackendChanged;
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _backend.Changed -= OnBackendChanged;
    }

    public void Dispose() => Stop();

    public void CheckNow()
    {
        bool processedAnyQueued = false;
        while (_backend.TryDequeueSnapshot(out var queued))
        {
            processedAnyQueued = true;
            if (queued is null) continue;
            if (queued.SequenceNumber == _lastSequence) continue;
            _lastSequence = queued.SequenceNumber;
            if (!_settings.RecordingPaused && IsEnabled)
            {
                ProcessSnapshot(queued);
            }
        }
        if (processedAnyQueued)
        {
            return;
        }

        if (_settings.RecordingPaused || !IsEnabled)
        {
            _lastSequence = _backend.SequenceNumber;
            return;
        }

        var seq = _backend.SequenceNumber;
        if (seq == _lastSequence) return;

        var snap = _backend.TryRead();
        if (snap is null) return;
        // 仅在真正读到快照后才推进序号，避免短暂读失败把该次复制永久吞掉
        _lastSequence = snap.SequenceNumber != 0 ? snap.SequenceNumber : seq;
        ProcessSnapshot(snap);
    }

    private void ProcessSnapshot(ClipboardSnapshot snap)
    {
        if (snap.ContainsSelfCopy || snap.IsPrivacyBlocked || snap.ContainsFiles) return;
        if (string.IsNullOrWhiteSpace(snap.Text) || snap.Text.Length > MaxChars) return;

        var trimmed = snap.Text.Trim();
        if (Note.WebAddress(trimmed) is not null)
        {
            if (_settings.LinksEnabled)
            {
                Save(trimmed, rtfBytes: null, isLink: true);
            }
        }
        else if (_settings.TextEnabled)
        {
            Save(snap.Text, snap.RtfBytes, isLink: false);
        }
    }

    private void OnBackendChanged(object? sender, EventArgs e) => CheckNow();

    private void Save(string text, byte[]? rtfBytes, bool isLink)
    {
        if (_history.NoteHolding(text) is { } existingPath)
        {
            var stamp = NextMonotonicUtc();
            _onAgain(existingPath, stamp);
            return;
        }

        var folder = NoteFolder.Folder;
        Directory.CreateDirectory(folder);

        var nowLocal = DateTime.Now;
        var ext = rtfBytes is null ? "txt" : "rtf";
        var prefix = isLink ? "Link" : "Text";
        var stampName = nowLocal.ToString("yyyy-MM-dd 'at' HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);

        var path = Path.Combine(folder, $"{prefix} {stampName}.{ext}");
        int n = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{prefix} {stampName} {n}.{ext}");
            n++;
        }

        var tmp = path + ".tmp";
        if (rtfBytes is not null)
        {
            File.WriteAllBytes(tmp, rtfBytes);
        }
        else
        {
            File.WriteAllText(tmp, text, Utf8NoBom);
        }
        File.Move(tmp, path, overwrite: false);

        var monotonicUtc = NextMonotonicUtc();
        _history.Record(path, text, monotonicUtc);
        _onNew(path);
    }

    private DateTime NextMonotonicUtc()
    {
        var now = DateTime.UtcNow;
        if (now <= _lastStampUtc)
        {
            now = _lastStampUtc.AddMilliseconds(1);
        }
        _lastStampUtc = now;
        return now;
    }
}

/// <summary>
/// 生产剪贴板后端：在独立后台 STA 线程挂载 Message-Only 窗口接收 WM_CLIPBOARDUPDATE，
/// 第一时间通过纯 Win32 OpenClipboard/GlobalLock 把快照入队再异步通知 UI，避免 200ms 内连续复制被覆盖丢失。
/// </summary>
public sealed class Win32ClipboardBackend : IClipboardBackend
{
    private const int WmClipboardUpdate = 0x031D;
    private const uint CfUnicodeText = 13;
    private const uint CfHDrop = 15;
    private const int MaxUnicodeReadChars = 100_005;
    private const int MaxRtfBytes = 5_000_000;
    private static readonly IntPtr HwndMessage = new(-3);

    private static readonly uint CfSelfCopy = RegisterClipboardFormat(ClipboardWatcher.SelfCopyFormat);
    private static readonly uint CfRtf = RegisterClipboardFormat("Rich Text Format");
    private static readonly uint CfExcludeMonitor = RegisterClipboardFormat(ClipboardWatcher.PrivacyFormats[0]);
    private static readonly uint CfViewerIgnore = RegisterClipboardFormat(ClipboardWatcher.PrivacyFormats[1]);
    private static readonly uint CfCanInclude = RegisterClipboardFormat(ClipboardWatcher.CanIncludeFormat);

    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private readonly ConcurrentQueue<ClipboardSnapshot> _captured = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _listenerThread;
    private Dispatcher? _listenerDispatcher;
    private IntPtr _listenerHwnd;
    private DispatcherTimer? _retryTimer;
    private uint _lastCapturedSeq;
    private int _notifyPending;
    private volatile bool _disposed;

    public event EventHandler? Changed;

    public Win32ClipboardBackend()
    {
        _lastCapturedSeq = GetClipboardSequenceNumber();
        _listenerThread = new Thread(ListenerThreadMain)
        {
            IsBackground = true,
            Name = "EnCopyClipboardListener"
        };
        _listenerThread.SetApartmentState(ApartmentState.STA);
        _listenerThread.Start();
        _ready.Wait();
    }

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public bool TryDequeueSnapshot(out ClipboardSnapshot? snapshot) => _captured.TryDequeue(out snapshot);

    public ClipboardSnapshot? TryRead() =>
        TryReadNative(IntPtr.Zero, out var snap, out _) ? snap : null;

    public bool TryWrite(string text, byte[]? rtfBytes)
    {
        return RetryClipboard(() =>
        {
            var obj = new DataObject();
            obj.SetData(DataFormats.UnicodeText, text, autoConvert: true);
            if (rtfBytes is not null)
            {
                obj.SetData(DataFormats.Rtf, Encoding.UTF8.GetString(rtfBytes));
            }
            obj.SetData(ClipboardWatcher.SelfCopyFormat, new byte[] { 1 });
            Clipboard.SetDataObject(obj, copy: true);
            return true;
        }, fallback: false);
    }

    /// <summary>
    /// Win32 剪贴板短暂被占用（如 CLIPBRD_E_CANT_OPEN）时最多重试 5 次；全部失败后返回 fallback，不让 COMException 崩掉主消息循环。
    /// </summary>
    public static T RetryClipboard<T>(Func<T> action, T fallback, int maxAttempts = 5, Action? onRetryWait = null)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return action();
            }
            catch (COMException) when (attempt < maxAttempts - 1)
            {
                if (onRetryWait is not null) onRetryWait();
                else Thread.Sleep(15);
            }
            catch (COMException)
            {
                // 第 maxAttempts 次仍被其他进程占用剪贴板：按契约放弃本次读/写并返回 fallback。
                return fallback;
            }
        }
        return fallback;
    }

    public static string? ReadUnicodeFromGlobal(IntPtr hMem)
    {
        if (hMem == IntPtr.Zero) return null;
        nuint rawSize = GlobalSize(hMem);
        if (rawSize < 2) return string.Empty;
        int maxChars = (int)Math.Min(rawSize / 2, (nuint)MaxUnicodeReadChars);
        IntPtr ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var raw = Marshal.PtrToStringUni(ptr, maxChars);
            if (raw is null) return null;
            int nul = raw.IndexOf('\0');
            return nul >= 0 ? raw[..nul] : raw;
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    public static byte[]? ReadRtfFromGlobal(IntPtr hMem)
    {
        if (hMem == IntPtr.Zero) return null;
        nuint rawSize = GlobalSize(hMem);
        if (rawSize == 0 || rawSize > (nuint)(MaxRtfBytes + 64)) return null;
        IntPtr ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            int len = (int)rawSize;
            var bytes = new byte[len];
            Marshal.Copy(ptr, bytes, 0, len);
            int nul = Array.IndexOf(bytes, (byte)0);
            if (nul >= 0)
            {
                len = nul;
            }
            if (len == 0 || len > MaxRtfBytes) return null;
            return len == bytes.Length ? bytes : bytes[..len];
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    public static bool IsZeroDwordGlobal(IntPtr hMem)
    {
        if (hMem == IntPtr.Zero) return false;
        nuint rawSize = GlobalSize(hMem);
        if (rawSize < 4) return false;
        IntPtr ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return false;
        try
        {
            return Marshal.ReadInt32(ptr) == 0;
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    private void ListenerThreadMain()
    {
        HwndSource? source = null;
        bool listening = false;
        try
        {
            _listenerDispatcher = Dispatcher.CurrentDispatcher;
            var parameters = new HwndSourceParameters("EnCopyClipboardListener")
            {
                ParentWindow = HwndMessage,
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            source = new HwndSource(parameters);
            _listenerHwnd = source.Handle;
            source.AddHook(WndProc);
            listening = AddClipboardFormatListener(_listenerHwnd);

            _retryTimer = new DispatcherTimer(DispatcherPriority.Normal, _listenerDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            _retryTimer.Tick += (_, _) =>
            {
                _retryTimer?.Stop();
                CaptureIfNewer(allowSettleDelay: false);
            };
        }
        finally
        {
            _ready.Set();
        }

        if (source is null) return;
        try
        {
            Dispatcher.Run();
        }
        finally
        {
            _retryTimer?.Stop();
            _retryTimer = null;
            if (listening && _listenerHwnd != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(_listenerHwnd);
            }
            source.RemoveHook(WndProc);
            source.Dispose();
            _listenerHwnd = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            CaptureIfNewer(allowSettleDelay: true);
        }
        return IntPtr.Zero;
    }

    private void CaptureIfNewer(bool allowSettleDelay)
    {
        if (_disposed) return;
        if (GetClipboardSequenceNumber() == _lastCapturedSeq) return;

        if (allowSettleDelay)
        {
            // 等待写入方的 OleSetClipboard -> OleFlushClipboard (<1ms) 无竞争落地为内核 HGLOBAL，
            // 同时远小于 50ms 连按间隔，防止 Writer 触发 100ms CLIPBRD_E_CANT_OPEN 重试。
            Thread.Sleep(2);
        }
        if (_disposed) return;
        if (GetClipboardSequenceNumber() == _lastCapturedSeq) return;

        if (TryReadNative(_listenerHwnd, out var snap, out bool openFailed))
        {
            _retryTimer?.Stop();
            if (snap is not null && snap.SequenceNumber != _lastCapturedSeq)
            {
                _lastCapturedSeq = snap.SequenceNumber;
                _captured.Enqueue(snap);
                ScheduleUiNotify();
            }
            if (!_disposed && GetClipboardSequenceNumber() != _lastCapturedSeq)
            {
                _listenerDispatcher?.BeginInvoke(() => CaptureIfNewer(allowSettleDelay: false));
            }
        }
        else if (openFailed)
        {
            _retryTimer?.Stop();
            _retryTimer?.Start();
        }
    }

    private void ScheduleUiNotify()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _notifyPending, 1) == 0)
        {
            _uiDispatcher.BeginInvoke(DrainToUi);
        }
    }

    private void DrainToUi()
    {
        Interlocked.Exchange(ref _notifyPending, 0);
        if (_disposed) return;
        Changed?.Invoke(this, EventArgs.Empty);
        if (!_disposed && !_captured.IsEmpty)
        {
            ScheduleUiNotify();
        }
    }

    private static bool TryReadNative(IntPtr ownerHwnd, out ClipboardSnapshot? snapshot, out bool openFailed)
    {
        snapshot = null;
        openFailed = false;

        bool opened = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (OpenClipboard(ownerHwnd))
            {
                opened = true;
                break;
            }
            if (attempt < 5)
            {
                Thread.Sleep(1);
            }
        }

        if (!opened)
        {
            openFailed = true;
            return false;
        }

        try
        {
            uint seq = GetClipboardSequenceNumber();
            bool selfCopy = CfSelfCopy != 0 && IsClipboardFormatAvailable(CfSelfCopy);
            bool containsFiles = IsClipboardFormatAvailable(CfHDrop);
            bool privacyBlocked =
                (CfExcludeMonitor != 0 && IsClipboardFormatAvailable(CfExcludeMonitor)) ||
                (CfViewerIgnore != 0 && IsClipboardFormatAvailable(CfViewerIgnore));

            if (!privacyBlocked && CfCanInclude != 0 && IsClipboardFormatAvailable(CfCanInclude))
            {
                if (IsZeroDwordGlobal(GetClipboardData(CfCanInclude)))
                {
                    privacyBlocked = true;
                }
            }

            if (selfCopy || containsFiles || privacyBlocked)
            {
                snapshot = new ClipboardSnapshot(seq, selfCopy, containsFiles, privacyBlocked, null, null);
                return true;
            }

            string? text = IsClipboardFormatAvailable(CfUnicodeText)
                ? ReadUnicodeFromGlobal(GetClipboardData(CfUnicodeText))
                : null;
            byte[]? rtfBytes = CfRtf != 0 && IsClipboardFormatAvailable(CfRtf)
                ? ReadRtfFromGlobal(GetClipboardData(CfRtf))
                : null;

            snapshot = new ClipboardSnapshot(seq, false, false, false, text, rtfBytes);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var dispatcher = _listenerDispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        if (Thread.CurrentThread != _listenerThread)
        {
            _listenerThread.Join(1000);
        }
        _ready.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr hMem);
}
