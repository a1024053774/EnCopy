using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EnCopy;

public readonly record struct Shortcut(uint Modifiers, uint VirtualKey, string Display)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public const uint VkC = 0x43;

    public static readonly Shortcut Standard = new(ModAlt, VkC, "Alt+C");
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
