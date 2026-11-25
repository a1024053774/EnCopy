using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

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
        if (_settings.RecordingPaused || !IsEnabled)
        {
            _lastSequence = _backend.SequenceNumber;
            return;
        }

        var seq = _backend.SequenceNumber;
        if (seq == _lastSequence) return;
        _lastSequence = seq;

        var snap = _backend.TryRead();
        if (snap is null) return;
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
/// 生产剪贴板后端：通过隐藏 Message-Only 窗口挂载 AddClipboardFormatListener 接收 WM_CLIPBOARDUPDATE。
/// </summary>
public sealed class Win32ClipboardBackend : IClipboardBackend
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private HwndSource? _source;
    private bool _listening;

    public event EventHandler? Changed;

    public Win32ClipboardBackend()
    {
        var parameters = new HwndSourceParameters("EnCopyClipboardListener")
        {
            ParentWindow = HwndMessage,
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _listening = AddClipboardFormatListener(_source.Handle);
    }

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public ClipboardSnapshot? TryRead()
    {
        return RetryClipboard<ClipboardSnapshot?>(() =>
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return null;

            var formats = data.GetFormats(autoConvert: false);
            bool selfCopy = formats.Contains(ClipboardWatcher.SelfCopyFormat, StringComparer.OrdinalIgnoreCase);
            bool containsFiles = data.GetDataPresent(DataFormats.FileDrop, autoConvert: false);

            bool privacyBlocked = formats.Any(f =>
                ClipboardWatcher.PrivacyFormats.Contains(f, StringComparer.OrdinalIgnoreCase));

            if (!privacyBlocked && data.GetDataPresent(ClipboardWatcher.CanIncludeFormat, autoConvert: false))
            {
                if (IsZeroDword(data.GetData(ClipboardWatcher.CanIncludeFormat, autoConvert: false)))
                {
                    privacyBlocked = true;
                }
            }

            string? text = data.GetDataPresent(DataFormats.UnicodeText, autoConvert: true)
                ? data.GetData(DataFormats.UnicodeText, autoConvert: true) as string
                : null;

            byte[]? rtfBytes = null;
            if (data.GetDataPresent(DataFormats.Rtf, autoConvert: false))
            {
                if (data.GetData(DataFormats.Rtf, autoConvert: false) is string rtfStr && rtfStr.Length <= 5_000_000)
                {
                    rtfBytes = Encoding.UTF8.GetBytes(rtfStr);
                }
            }

            return new ClipboardSnapshot(SequenceNumber, selfCopy, containsFiles, privacyBlocked, text, rtfBytes);
        }, fallback: null);
    }

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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            // 异步派发到当前 Dispatcher，避免在 WndProc 同步输入调用内触发 COM RPC_E_CANTCALLOUT_ININPUTSYNCCALL
            _dispatcher.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            if (_listening)
            {
                RemoveClipboardFormatListener(_source.Handle);
                _listening = false;
            }
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private static bool IsZeroDword(object? raw)
    {
        return raw switch
        {
            MemoryStream ms when ms.Length >= 4 => BitConverter.ToInt32(ms.ToArray(), 0) == 0,
            byte[] bytes when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0) == 0,
            int i => i == 0,
            _ => false
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
