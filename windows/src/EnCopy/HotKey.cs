using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace EnCopy;

public enum ShortcutInputResult
{
    Valid,
    Cancel,
    Clear,
    ModifierOnly,
    InvalidNeedsModifier
}

public readonly record struct Shortcut(uint Modifiers, uint VirtualKey, string Display)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public const uint VkC = 0x43;

    public static readonly Shortcut Standard = new(ModAlt, VkC, "Alt+C");

    /// <summary>
    /// 解析改键窗口的键盘按键：必须含 Ctrl / Alt / Win 至少一个修饰键；
    /// 单独 Esc 取消，单独 Delete/Backspace 移除快捷键，纯修饰键与非法单键分别返回明确状态。
    /// </summary>
    public static ShortcutInputResult TryFromKeyInput(
        Key key,
        Key systemKey,
        ModifierKeys modifiers,
        out Shortcut shortcut,
        Key imeProcessedKey = Key.None)
    {
        shortcut = default;
        var actualKey = key switch
        {
            Key.System => systemKey,
            Key.ImeProcessed when imeProcessedKey != Key.None => imeProcessedKey,
            _ => key
        };
        if (actualKey == Key.None)
        {
            return ShortcutInputResult.ModifierOnly;
        }
        if (IsModifierKey(actualKey))
        {
            return ShortcutInputResult.ModifierOnly;
        }
        if (modifiers == ModifierKeys.None)
        {
            if (actualKey == Key.Escape)
            {
                return ShortcutInputResult.Cancel;
            }
            if (actualKey is Key.Back or Key.Delete)
            {
                return ShortcutInputResult.Clear;
            }
            return ShortcutInputResult.InvalidNeedsModifier;
        }

        // 仅按 Shift+字母 仍会干扰日常英文大小写输入，要求至少包含 Ctrl / Alt / Win。
        bool hasCommandModifier = (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
        if (!hasCommandModifier)
        {
            return ShortcutInputResult.InvalidNeedsModifier;
        }

        int vk = KeyInterop.VirtualKeyFromKey(actualKey);
        if (vk <= 0)
        {
            return ShortcutInputResult.InvalidNeedsModifier;
        }

        uint winMods = 0;
        if ((modifiers & ModifierKeys.Control) != 0) winMods |= ModControl;
        if ((modifiers & ModifierKeys.Alt) != 0) winMods |= ModAlt;
        if ((modifiers & ModifierKeys.Shift) != 0) winMods |= ModShift;
        if ((modifiers & ModifierKeys.Windows) != 0) winMods |= ModWin;

        shortcut = new Shortcut(winMods, (uint)vk, FormatDisplay(modifiers, actualKey));
        return ShortcutInputResult.Valid;
    }

    public static string FormatDisplay(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(FormatKeyName(key));
        return string.Join('+', parts);
    }

    private static string FormatKeyName(Key key)
    {
        if (key == Key.Space) return Localization.L("Space");
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "Num" + (key - Key.NumPad0);
        return key switch
        {
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemTilde => "`",
            _ => key.ToString()
        };
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;
}

public interface IHotKeyBackend : IDisposable
{
    bool Register(Shortcut shortcut, Action onPressed);
    void Unregister();
}

/// <summary>
/// 全局唯一系统热键注册包装（默认 Alt+C）。除本文件外，任何源文件均不得调用 Win32 热键 API。
/// </summary>
public sealed class HotKey : IDisposable
{
    private readonly IHotKeyBackend _backend;

    public bool IsRegistered { get; }
    public Shortcut Shortcut { get; }

    public HotKey(Shortcut shortcut, Action onPressed, IHotKeyBackend? backend = null)
    {
        Shortcut = shortcut;
        _backend = backend ?? new Win32HotKeyBackend();
        IsRegistered = _backend.Register(shortcut, onPressed);
    }

    public void Dispose() => _backend.Dispose();

    private sealed class Win32HotKeyBackend : IHotKeyBackend
    {
        private const int WmHotKey = 0x0312;
        private const int HotKeyId = 0x454E;
        private static readonly IntPtr HwndMessage = new(-3);

        private HwndSource? _source;
        private Action? _onPressed;
        private bool _registered;

        public bool Register(Shortcut shortcut, Action onPressed)
        {
            Unregister();
            _onPressed = onPressed;
            var parameters = new HwndSourceParameters("EnCopyHotKeyWindow")
            {
                ParentWindow = HwndMessage,
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            _registered = RegisterHotKey(_source.Handle, HotKeyId, shortcut.Modifiers | Shortcut.ModNoRepeat, shortcut.VirtualKey);
            if (!_registered)
            {
                DisposeWindow();
            }
            return _registered;
        }

        public void Unregister()
        {
            if (_registered && _source is not null)
            {
                UnregisterHotKey(_source.Handle, HotKeyId);
                _registered = false;
            }
            DisposeWindow();
            _onPressed = null;
        }

        public void Dispose() => Unregister();

        private void DisposeWindow()
        {
            if (_source is not null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotKey && wParam.ToInt32() == HotKeyId)
            {
                handled = true;
                _onPressed?.Invoke();
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
