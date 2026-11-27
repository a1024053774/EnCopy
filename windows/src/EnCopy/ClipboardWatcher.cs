using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EnCopy;

public sealed record ClipboardSnapshot(
    uint SequenceNumber,
    bool ContainsSelfCopy,
    bool ContainsFiles,
    bool IsPrivacyBlocked,
    string? Text,
    byte[]? RtfBytes,
    IReadOnlyList<string>? CopiedPaths = null,
    byte[]? PngBytes = null,
    string? SourceAppId = null);

public interface IClipboardBackend : IDisposable
{
    event EventHandler? Changed;
    uint SequenceNumber { get; }
    void SetListening(bool enabled);
    bool TryDequeueSnapshot(out ClipboardSnapshot? snapshot);
    ClipboardSnapshot? TryRead();
    bool TryWrite(string text, byte[]? rtfBytes);
    bool TryWriteFiles(IReadOnlyList<string> paths);
    bool TryWriteImage(string imagePath);
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
    public const double HourlyLifeSeconds = 3600.0;
    public const double VerificationCodeLifeSeconds = 600.0;

    private const int MaxChars = 100_000;
    private const int MaxImageBytes = 64 * 1024 * 1024;
    private const long MaxImagePixels = 80_000_000;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly Regex DigitsOnlyCodeRegex = new(@"^[0-9]{4,8}$", RegexOptions.Compiled);
    private static readonly Regex NotACodeRegex = new(
        @"\b(status|postal|zip|area|country|error|exit|return|dead|source|promo|discount|bar|qr)\s+code\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NearCodeRegex = new(
        @"(验证码|校验码|动态码|\bcode\b)[^0-9]{0,12}(?<![0-9A-Za-z-])[0-9]{4,8}(?![0-9])|(?<![0-9A-Za-z-])[0-9]{4,8}(?![0-9])[^0-9]{0,24}(验证码|校验码|动态码|\bcode\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> MailAndSmsBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "hxoutlook", "olk", "outlook", "thunderbird", "phoneexperiencehost"
    };

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

    public bool IsEnabled =>
        !_settings.RecordingPaused &&
        (_settings.TextEnabled || _settings.LinksEnabled || _settings.ImagesEnabled || _settings.FilesEnabled);

    /// <summary>
    /// 识别 4–8 位验证码（对齐 ClipboardWatcher.swift:107-118）：
    /// 邮件/短信客户端单独复制的 4–8 位数字，或短文本（<=160 字）中紧挨「验证码 / 校验码 / 动态码 / code」的 4–8 位数字；
    /// 排除状态码、邮编、错误码与 T-1024 / v2024 等标识符。
    /// </summary>
    public static bool IsVerificationCode(string text, string? appId)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (DigitsOnlyCodeRegex.IsMatch(trimmed) && !string.IsNullOrWhiteSpace(appId))
        {
            var baseKey = SourceApp.NormalizeRuleKey(appId);
            if (MailAndSmsBaseNames.Contains(baseKey))
            {
                return true;
            }
        }
        if (trimmed.Length > 160) return false;
        if (NotACodeRegex.IsMatch(trimmed)) return false;
        return NearCodeRegex.IsMatch(trimmed);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _lastSequence = _backend.SequenceNumber;
        _backend.Changed += OnBackendChanged;
        _backend.SetListening(true);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _backend.SetListening(false);
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
        if (snap.ContainsSelfCopy || snap.IsPrivacyBlocked) return;

        var appId = snap.SourceAppId;
        var rule = SourceApp.GetRule(_settings, appId);
        if (rule == AppRule.Skip)
        {
            return;
        }
        double? baseTtl = rule == AppRule.Hourly ? HourlyLifeSeconds : null;

        if (snap.ContainsFiles)
        {
            if ((!_settings.FilesEnabled && !_settings.ImagesEnabled) || snap.CopiedPaths is not { Count: > 0 } rawPaths)
            {
                return;
            }

            var existingPaths = FileList.NormalizePaths(rawPaths)
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .ToArray();
            if (existingPaths.Length == 0) return;

            var nonMediaFiles = _settings.FilesEnabled
                ? existingPaths.Where(p => !FileList.IsMediaFile(p)).ToArray()
                : [];
            var mediaFiles = _settings.ImagesEnabled
                ? existingPaths.Where(FileList.IsMediaFile).Take(20).ToArray()
                : [];

            foreach (var mediaPath in mediaFiles)
            {
                var stamp = NextMonotonicUtc();
                bool alreadyKnown = _history.Contains(mediaPath);
                _history.RecordImage(mediaPath, ocrWords: null, pngBytesForHash: null, usedAt: stamp, appName: appId, ttlSeconds: baseTtl);
                _history.SetApp(mediaPath, appId);
                _history.RenewTtl(mediaPath, baseTtl);
                if (alreadyKnown) _onAgain(mediaPath, stamp);
                else _onNew(mediaPath);
            }

            if (nonMediaFiles.Length > 0)
            {
                var stamp = NextMonotonicUtc();
                if (_history.FindFileListHolding(nonMediaFiles) is { } existingManifest)
                {
                    _history.SetApp(existingManifest, appId);
                    _history.RenewTtl(existingManifest, baseTtl);
                    _onAgain(existingManifest, stamp);
                }
                else if (FileList.Make(nonMediaFiles) is { } newManifest)
                {
                    _history.RecordFileList(newManifest, appName: appId, ttlSeconds: baseTtl, usedAt: stamp);
                    _onNew(newManifest);
                }
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(snap.Text))
        {
            if (snap.Text.Length > MaxChars) return;
            double? textTtl = IsVerificationCode(snap.Text, appId) ? VerificationCodeLifeSeconds : baseTtl;
            var trimmed = snap.Text.Trim();
            if (Note.WebAddress(trimmed) is not null)
            {
                if (_settings.LinksEnabled)
                {
                    SaveText(trimmed, rtfBytes: null, isLink: true, appId: appId, ttlSeconds: textTtl);
                }
            }
            else if (_settings.TextEnabled)
            {
                SaveText(snap.Text, snap.RtfBytes, isLink: false, appId: appId, ttlSeconds: textTtl);
            }
            return;
        }

        if (_settings.ImagesEnabled && snap.PngBytes is { Length: > 0 and <= MaxImageBytes } pngBytes)
        {
            SaveCopiedImage(pngBytes, appId, baseTtl);
        }
    }

    private void OnBackendChanged(object? sender, EventArgs e) => CheckNow();

    private void SaveText(string text, byte[]? rtfBytes, bool isLink, string? appId, double? ttlSeconds)
    {
        if (_history.NoteHolding(text) is { } existingPath)
        {
            var stamp = NextMonotonicUtc();
            _history.SetApp(existingPath, appId);
            _history.RenewTtl(existingPath, ttlSeconds);
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
        _history.Record(path, text, monotonicUtc, appName: appId, ttlSeconds: ttlSeconds);
        _onNew(path);
    }

    private void SaveCopiedImage(byte[] pngBytes, string? appId, double? ttlSeconds)
    {
        if (!Win32ClipboardBackend.IsSanePngImage(pngBytes)) return;

        if (_history.FindPictureHolding(pngBytes) is { } existingImg)
        {
            var stamp = NextMonotonicUtc();
            _history.SetApp(existingImg, appId);
            _history.RenewTtl(existingImg, ttlSeconds);
            _onAgain(existingImg, stamp);
            return;
        }

        var folder = DataFolder.ScreenshotsFolder;
        Directory.CreateDirectory(folder);

        var stampName = DateTime.Now.ToString("yyyy-MM-dd 'at' HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"Clipboard {stampName}.png");
        int n = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"Clipboard {stampName} {n}.png");
            n++;
        }

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, pngBytes);
        File.Move(tmp, path, overwrite: false);

        var monotonicUtc = NextMonotonicUtc();
        _history.RecordImage(path, ocrWords: null, pngBytesForHash: pngBytes, usedAt: monotonicUtc, appName: appId, ttlSeconds: ttlSeconds);
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
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint CfHDrop = 15;
    private const int MaxUnicodeReadChars = 100_005;
    private const int MaxRtfBytes = 5_000_000;
    private const int MaxPngBytes = 64 * 1024 * 1024;
    private const long MaxBitmapPixels = 80_000_000;
    private const int MaxRetryAttempts = 10;
    private static readonly IntPtr HwndMessage = new(-3);

    private static readonly uint CfSelfCopy = RegisterClipboardFormat(ClipboardWatcher.SelfCopyFormat);
    private static readonly uint CfRtf = RegisterClipboardFormat("Rich Text Format");
    private static readonly uint CfPng = RegisterClipboardFormat("PNG");
    private static readonly uint CfExcludeMonitor = RegisterClipboardFormat(ClipboardWatcher.PrivacyFormats[0]);
    private static readonly uint CfViewerIgnore = RegisterClipboardFormat(ClipboardWatcher.PrivacyFormats[1]);
    private static readonly uint CfCanInclude = RegisterClipboardFormat(ClipboardWatcher.CanIncludeFormat);

    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private readonly ConcurrentQueue<ClipboardSnapshot> _captured = new();
    private readonly Thread _listenerThread;
    private Dispatcher? _listenerDispatcher;
    private IntPtr _listenerHwnd;
    private DispatcherTimer? _retryTimer;
    private uint _lastCapturedSeq;
    private int _notifyPending;
    private int _retryAttempts;
    private bool _listeningActive;
    private volatile bool _disposed;

    public event EventHandler? Changed;

    public Win32ClipboardBackend()
    {
        _lastCapturedSeq = GetClipboardSequenceNumber();
        using var ready = new ManualResetEventSlim(false);
        _listenerThread = new Thread(state => ListenerThreadMain((ManualResetEventSlim)state!))
        {
            IsBackground = true,
            Name = "EnCopyClipboardListener"
        };
        _listenerThread.SetApartmentState(ApartmentState.STA);
        _listenerThread.Start(ready);
        ready.Wait();
    }

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public void SetListening(bool enabled)
    {
        if (!enabled)
        {
            Volatile.Write(ref _listeningActive, false);
            Interlocked.Exchange(ref _notifyPending, 0);
            while (_captured.TryDequeue(out _)) { }
            Volatile.Write(ref _lastCapturedSeq, GetClipboardSequenceNumber());
            if (!_disposed && _listenerDispatcher is { HasShutdownStarted: false } dispatcher)
            {
                dispatcher.BeginInvoke(() =>
                {
                    _retryAttempts = 0;
                    _retryTimer?.Stop();
                });
            }
            return;
        }

        while (_captured.TryDequeue(out _)) { }
        Volatile.Write(ref _lastCapturedSeq, GetClipboardSequenceNumber());
        Volatile.Write(ref _listeningActive, true);
    }

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

    public bool TryWriteFiles(IReadOnlyList<string> paths)
    {
        var present = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).ToArray();
        if (present.Length == 0) return false;
        return RetryClipboard(() =>
        {
            var obj = new DataObject();
            var drop = new StringCollection();
            drop.AddRange(present);
            obj.SetFileDropList(drop);
            obj.SetData(ClipboardWatcher.SelfCopyFormat, new byte[] { 1 });
            Clipboard.SetDataObject(obj, copy: true);
            return true;
        }, fallback: false);
    }

    public bool TryWriteImage(string imagePath)
    {
        if (!File.Exists(imagePath)) return false;
        var full = Path.GetFullPath(imagePath);
        return RetryClipboard(() =>
        {
            var bytes = File.ReadAllBytes(full);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            var obj = new DataObject();
            obj.SetImage(bmp);
            if (string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase))
            {
                obj.SetData("PNG", new MemoryStream(bytes));
            }
            var drop = new StringCollection { full };
            obj.SetFileDropList(drop);
            obj.SetData(ClipboardWatcher.SelfCopyFormat, new byte[] { 1 });
            Clipboard.SetDataObject(obj, copy: true);
            return true;
        }, fallback: false);
    }

    public static bool IsSanePngImage(byte[] pngBytes)
    {
        if (pngBytes.Length < 24 || pngBytes.Length > MaxPngBytes) return false;
        try
        {
            using var ms = new MemoryStream(pngBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return false;
            var frame = decoder.Frames[0];
            return frame.PixelWidth > 0 && frame.PixelHeight > 0 &&
                   (long)frame.PixelWidth * frame.PixelHeight <= MaxBitmapPixels;
        }
        catch (Exception)
        {
            return false;
        }
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

    private void ListenerThreadMain(ManualResetEventSlim ready)
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
                CaptureIfNewer();
            };
        }
        finally
        {
            ready.Set();
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
            _retryAttempts = 0;
            CaptureIfNewer();
        }
        return IntPtr.Zero;
    }

    private void CaptureIfNewer()
    {
        if (_disposed || !Volatile.Read(ref _listeningActive))
        {
            _retryAttempts = 0;
            _retryTimer?.Stop();
            Volatile.Write(ref _lastCapturedSeq, GetClipboardSequenceNumber());
            return;
        }
        if (GetClipboardSequenceNumber() == Volatile.Read(ref _lastCapturedSeq)) return;

        if (TryReadNative(_listenerHwnd, out var snap, out bool openFailed))
        {
            _retryAttempts = 0;
            _retryTimer?.Stop();
            if (!_disposed && Volatile.Read(ref _listeningActive) &&
                snap is not null && snap.SequenceNumber != Volatile.Read(ref _lastCapturedSeq))
            {
                Volatile.Write(ref _lastCapturedSeq, snap.SequenceNumber);
                _captured.Enqueue(snap);
                ScheduleUiNotify();
            }
            if (!_disposed && Volatile.Read(ref _listeningActive) &&
                GetClipboardSequenceNumber() != Volatile.Read(ref _lastCapturedSeq))
            {
                _listenerDispatcher?.BeginInvoke(CaptureIfNewer);
            }
        }
        else if (openFailed)
        {
            if (_retryAttempts < MaxRetryAttempts)
            {
                _retryAttempts++;
                _retryTimer?.Stop();
                _retryTimer?.Start();
            }
            else
            {
                // 外部进程长期独占 OpenClipboard 或延迟渲染永久失败时停止 10ms 重试，防止后台空转
                _retryAttempts = 0;
                _retryTimer?.Stop();
                Volatile.Write(ref _lastCapturedSeq, GetClipboardSequenceNumber());
            }
        }
    }

    private void ScheduleUiNotify()
    {
        if (_disposed || !Volatile.Read(ref _listeningActive) || Changed is null) return;
        if (Interlocked.Exchange(ref _notifyPending, 1) == 0)
        {
            _uiDispatcher.BeginInvoke(DrainToUi);
        }
    }

    private void DrainToUi()
    {
        Interlocked.Exchange(ref _notifyPending, 0);
        var handler = Changed;
        if (_disposed || !Volatile.Read(ref _listeningActive) || handler is null)
        {
            while (_captured.TryDequeue(out _)) { }
            return;
        }
        if (!_captured.IsEmpty)
        {
            handler(this, EventArgs.Empty);
        }
        if (!_disposed && Volatile.Read(ref _listeningActive) && !_captured.IsEmpty && Changed is not null)
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
            string? sourceAppId = SourceApp.ResolveClipboardSourceApp();
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

            if (selfCopy || privacyBlocked)
            {
                snapshot = new ClipboardSnapshot(seq, selfCopy, containsFiles, privacyBlocked, null, null, null, null, sourceAppId);
                return true;
            }

            if (containsFiles)
            {
                var paths = ReadHDropPaths(GetClipboardData(CfHDrop));
                if (paths is null)
                {
                    openFailed = true;
                    return false;
                }
                snapshot = new ClipboardSnapshot(seq, false, true, false, null, null, paths, null, sourceAppId);
                return true;
            }

            bool hasUnicode = IsClipboardFormatAvailable(CfUnicodeText);
            string? text = hasUnicode ? ReadUnicodeFromGlobal(GetClipboardData(CfUnicodeText)) : null;
            if (hasUnicode && text is null)
            {
                // 延迟渲染格式声明存在但暂时拿不到 HGLOBAL：视为未就绪，交由重试计时器而不提前推进序号
                openFailed = true;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                byte[]? rtfBytes = CfRtf != 0 && IsClipboardFormatAvailable(CfRtf)
                    ? ReadRtfFromGlobal(GetClipboardData(CfRtf))
                    : null;

                snapshot = new ClipboardSnapshot(seq, false, false, false, text, rtfBytes, null, null, sourceAppId);
                return true;
            }

            byte[]? pngBytes = null;
            if (CfPng != 0 && IsClipboardFormatAvailable(CfPng))
            {
                pngBytes = ReadRawPngFromGlobal(GetClipboardData(CfPng));
            }
            if (pngBytes is null && IsClipboardFormatAvailable(CfDib))
            {
                pngBytes = ReadDibAsPngFromGlobal(GetClipboardData(CfDib));
            }

            snapshot = new ClipboardSnapshot(seq, false, false, false, text, null, null, pngBytes, sourceAppId);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static IReadOnlyList<string>? ReadHDropPaths(IntPtr hDrop)
    {
        if (hDrop == IntPtr.Zero) return null;
        uint count = DragQueryFileW(hDrop, 0xFFFFFFFFu, null, 0);
        if (count == 0) return [];
        var list = new List<string>((int)Math.Min(count, 2000));
        var sb = new StringBuilder(1024);
        for (uint i = 0; i < count && i < 2000; i++)
        {
            uint needed = DragQueryFileW(hDrop, i, null, 0);
            if (needed == 0) continue;
            if (sb.Capacity < (int)needed + 2)
            {
                sb.EnsureCapacity((int)needed + 2);
            }
            uint copied = DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity);
            if (copied > 0)
            {
                list.Add(sb.ToString(0, (int)copied));
            }
        }
        return list;
    }

    private static byte[]? ReadRawPngFromGlobal(IntPtr hMem)
    {
        if (hMem == IntPtr.Zero) return null;
        nuint rawSize = GlobalSize(hMem);
        if (rawSize < 24 || rawSize > (nuint)MaxPngBytes) return null;
        IntPtr ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[(int)rawSize];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return IsSanePngImage(bytes) ? bytes : null;
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    private static byte[]? ReadDibAsPngFromGlobal(IntPtr hMem)
    {
        if (hMem == IntPtr.Zero) return null;
        nuint rawSize = GlobalSize(hMem);
        if (rawSize < 40 || rawSize > (nuint)MaxPngBytes) return null;
        IntPtr ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return null;
        byte[] dibBytes;
        try
        {
            dibBytes = new byte[(int)rawSize];
            Marshal.Copy(ptr, dibBytes, 0, dibBytes.Length);
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        try
        {
            int biSize = BitConverter.ToInt32(dibBytes, 0);
            int width = Math.Abs(BitConverter.ToInt32(dibBytes, 4));
            int height = Math.Abs(BitConverter.ToInt32(dibBytes, 8));
            ushort bpp = BitConverter.ToUInt16(dibBytes, 14);
            int compression = BitConverter.ToInt32(dibBytes, 16);
            uint clrUsed = BitConverter.ToUInt32(dibBytes, 32);
            if (biSize < 40 || width <= 0 || height <= 0 || (long)width * height > MaxBitmapPixels)
            {
                return null;
            }

            uint paletteBytes = clrUsed > 0
                ? clrUsed * 4u
                : (bpp <= 8 ? (1u << bpp) * 4u : (compression == 3 && biSize == 40 ? 12u : 0u));
            uint bfOffBits = 14u + (uint)biSize + paletteBytes;
            uint fileSize = 14u + (uint)dibBytes.Length;

            using var bmpStream = new MemoryStream((int)fileSize);
            using (var bw = new BinaryWriter(bmpStream, Encoding.ASCII, leaveOpen: true))
            {
                bw.Write((byte)'B');
                bw.Write((byte)'M');
                bw.Write(fileSize);
                bw.Write(0u);
                bw.Write(bfOffBits);
                bw.Write(dibBytes);
            }
            bmpStream.Position = 0;

            var decoder = BitmapDecoder.Create(bmpStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            using var outPng = new MemoryStream();
            encoder.Save(outPng);
            var png = outPng.ToArray();
            return png.Length <= MaxPngBytes ? png : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _listeningActive, false);
        while (_captured.TryDequeue(out _)) { }
        var dispatcher = _listenerDispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        if (Thread.CurrentThread != _listenerThread)
        {
            _listenerThread.Join(1000);
        }
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);
}
