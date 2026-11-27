using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EnCopy;

public readonly record struct KeyStroke(ushort VirtualKey, bool KeyDown, bool ModifiersClearedAfter)
{
    public const ushort VkLControl = 0xA2;
    public const ushort VkV = 0x56;
    public const ushort VkLShift = 0xA0;
    public const ushort VkRShift = 0xA1;
    public const ushort VkLMenu = 0xA4;
    public const ushort VkRMenu = 0xA5;
    public const ushort VkRControl = 0xA3;
    public const ushort VkLWin = 0x5B;
    public const ushort VkRWin = 0x5C;
}

/// <summary>
/// 生成标准的 Ctrl+V 合成键序列：按下时带 Ctrl，抬起依次释放 V 和 Ctrl 确保零卡键。
/// </summary>
public static class PasteKey
{
    public static IReadOnlyList<KeyStroke> CtrlV =>
    [
        new KeyStroke(KeyStroke.VkLControl, KeyDown: true, ModifiersClearedAfter: false),
        new KeyStroke(KeyStroke.VkV, KeyDown: true, ModifiersClearedAfter: false),
        new KeyStroke(KeyStroke.VkV, KeyDown: false, ModifiersClearedAfter: false),
        new KeyStroke(KeyStroke.VkLControl, KeyDown: false, ModifiersClearedAfter: true)
    ];

    public static bool AnyModifierPhysicallyDown()
    {
        return IsDown(KeyStroke.VkLControl) || IsDown(KeyStroke.VkRControl) ||
               IsDown(KeyStroke.VkLShift) || IsDown(KeyStroke.VkRShift) ||
               IsDown(KeyStroke.VkLMenu) || IsDown(KeyStroke.VkRMenu) ||
               IsDown(KeyStroke.VkLWin) || IsDown(KeyStroke.VkRWin);
    }

    public static void WaitForModifiersRelease(int maxWaitMs = 250)
    {
        var deadline = Environment.TickCount64 + maxWaitMs;
        while (AnyModifierPhysicallyDown() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
        }
    }

    public static bool PostCtrlV()
    {
        WaitForModifiersRelease();
        // 给刚恢复前台焦点的目标应用留出激活编辑控件的时间窗口，避免 Ctrl+V 抢先到达
        Thread.Sleep(35);
        var strokes = CtrlV;
        var inputs = new INPUT[strokes.Count];
        for (int i = 0; i < strokes.Count; i++)
        {
            inputs[i] = new INPUT
            {
                type = 1,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = strokes[i].VirtualKey,
                        wScan = (ushort)MapVirtualKey(strokes[i].VirtualKey, 0),
                        dwFlags = strokes[i].KeyDown ? 0u : 0x0002u,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static bool IsDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}

public interface IPastePlatform
{
    bool WriteClipboardText(string text, byte[]? rtfBytes);
    bool WriteClipboardFiles(IReadOnlyList<string> paths);
    bool WriteClipboardImage(string imagePath);
    void TuckAway(IntPtr restoreTargetWindow);
    bool CanSendInputTo(IntPtr targetWindow);
    bool SendCtrlV();
    void ShowHint();
}

/// <summary>
/// 选中项贴回原前台窗口（支持单选及 Shift+方向键多选按顺序合并粘贴）：
/// 写入剪贴板（带 SelfCopy）、收起并恢复焦点、UIPI 检测通过后发送 Ctrl+V，
/// 面对管理员高权限窗口时只复制并弹出顶部 PasteHint。
/// </summary>
public sealed class Paster
{
    private readonly Line _line;
    private readonly IPastePlatform _platform;

    public Paster(Line line, IPastePlatform platform)
    {
        _line = line;
        _platform = platform;
    }

    public bool WriteToClipboard(Note note, bool plain = false)
    {
        if (note.IsMovedAway)
        {
            return false;
        }

        if (plain)
        {
            return _platform.WriteClipboardText(note.FullText, rtfBytes: null);
        }

        if (note.Kind == ItemKind.Image)
        {
            return File.Exists(note.FilePath) && _platform.WriteClipboardImage(note.FilePath);
        }

        if (note.Kind is ItemKind.File or ItemKind.Video)
        {
            var present = ResolvePresentTargets(note);
            return present.Count > 0 && _platform.WriteClipboardFiles(present);
        }

        byte[]? rtf = string.Equals(Path.GetExtension(note.FilePath), ".rtf", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(note.FilePath)
            ? File.ReadAllBytes(note.FilePath)
            : null;
        return _platform.WriteClipboardText(note.FullText, rtf);
    }

    public bool WriteMultipleToClipboard(IReadOnlyList<Note> notes, bool plain = false)
    {
        if (notes.Count == 0) return false;
        if (notes.Count == 1)
        {
            return WriteToClipboard(notes[0], plain);
        }
        if (plain)
        {
            var plainJoined = string.Join("\n\n", notes.Where(n => !n.IsMovedAway).Select(n => n.FullText));
            return !string.IsNullOrEmpty(plainJoined) && _platform.WriteClipboardText(plainJoined, rtfBytes: null);
        }

        if (notes.All(n => n.Kind is ItemKind.File or ItemKind.Image or ItemKind.Video))
        {
            var allFiles = notes.SelectMany(ResolvePresentTargets).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return allFiles.Length > 0 && _platform.WriteClipboardFiles(allFiles);
        }

        var joined = string.Join("\n\n", notes.Where(n => !n.IsMovedAway).Select(n => n.FullText));
        return !string.IsNullOrEmpty(joined) && _platform.WriteClipboardText(joined, rtfBytes: null);
    }

    private static IReadOnlyList<string> ResolvePresentTargets(Note note)
    {
        if (FileList.IsManifestPath(note.FilePath))
        {
            return FileList.PresentPaths(note.FilePath);
        }
        if (note.FileTargets.Count > 0)
        {
            return note.FileTargets.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        }
        return File.Exists(note.FilePath) || Directory.Exists(note.FilePath) ? [note.FilePath] : [];
    }

    public bool PastePicked(IntPtr targetWindow, bool plain = false)
    {
        var selected = _line.GetSelectedNotesInLineOrder();
        return selected.Count > 0 && PasteNotes(selected, targetWindow, plain);
    }

    public bool Paste(Note note, IntPtr targetWindow, bool plain = false) =>
        PasteNotes([note], targetWindow, plain);

    public bool PasteNotes(IReadOnlyList<Note> notes, IntPtr targetWindow, bool plain = false)
    {
        if (notes.Count == 0) return false;
        if (!WriteMultipleToClipboard(notes, plain))
        {
            return false;
        }
        // 从右往左更新使用时间，使最先选中（左侧）的卡片在放回绳头后仍排在最前
        var stamp = DateTime.UtcNow;
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            _line.Use(notes[i].FilePath, stamp.AddMilliseconds(notes.Count - 1 - i));
        }
        _platform.TuckAway(targetWindow);
        if (_platform.CanSendInputTo(targetWindow))
        {
            if (!_platform.SendCtrlV())
            {
                _platform.ShowHint();
            }
        }
        else
        {
            _platform.ShowHint();
        }
        return true;
    }
}

public sealed class Win32PastePlatform : IPastePlatform
{
    private readonly IClipboardBackend _clipboard;
    private readonly Action<IntPtr> _tuckAndRestore;
    private readonly Action _showHint;

    public Win32PastePlatform(IClipboardBackend clipboard, Action<IntPtr> tuckAndRestore, Action showHint)
    {
        _clipboard = clipboard;
        _tuckAndRestore = tuckAndRestore;
        _showHint = showHint;
    }

    public bool WriteClipboardText(string text, byte[]? rtfBytes) => _clipboard.TryWrite(text, rtfBytes);

    public bool WriteClipboardFiles(IReadOnlyList<string> paths) => _clipboard.TryWriteFiles(paths);

    public bool WriteClipboardImage(string imagePath) => _clipboard.TryWriteImage(imagePath);

    public void TuckAway(IntPtr restoreTargetWindow) => _tuckAndRestore(restoreTargetWindow);

    public bool CanSendInputTo(IntPtr targetWindow) => WindowInteropHelperEx.CanSendInputTo(targetWindow);

    public bool SendCtrlV() => PasteKey.PostCtrlV();

    public void ShowHint() => _showHint();
}

/// <summary>
/// 前台窗口焦点保存/归还与 UIPI 完整性级别对比（区分普通窗与管理员提权窗）。
/// </summary>
public static class WindowInteropHelperEx
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int SwRestore = 9;

    public static IntPtr CurrentForegroundWindow() => GetForegroundWindow();

    public static void RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }
        if (GetForegroundWindow() == hwnd) return;

        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint selfThread = GetCurrentThreadId();

        bool attachedFg = false;
        bool attachedTarget = false;
        try
        {
            if (fgThread != 0 && fgThread != selfThread)
            {
                attachedFg = AttachThreadInput(selfThread, fgThread, true);
            }
            if (targetThread != 0 && targetThread != selfThread && targetThread != fgThread)
            {
                attachedTarget = AttachThreadInput(selfThread, targetThread, true);
            }
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(selfThread, targetThread, false);
            if (attachedFg) AttachThreadInput(selfThread, fgThread, false);
        }
    }

    public static bool CanSendInputTo(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow)) return false;
        GetWindowThreadProcessId(targetWindow, out uint pid);
        if (pid == 0) return false;
        if (pid == (uint)Environment.ProcessId) return true;

        var selfIl = GetProcessIntegrityRid(GetCurrentProcess());
        IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProc == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            var targetIl = GetProcessIntegrityRid(hProc);
            if (selfIl is null || targetIl is null) return false;
            return targetIl.Value <= selfIl.Value;
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    private static uint? GetProcessIntegrityRid(IntPtr hProcess)
    {
        if (!OpenProcessToken(hProcess, TokenQuery, out IntPtr hToken)) return null;
        try
        {
            GetTokenInformation(hToken, TokenIntegrityLevel, IntPtr.Zero, 0, out uint needed);
            if (needed == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetTokenInformation(hToken, TokenIntegrityLevel, buf, needed, out _)) return null;
                IntPtr pSid = Marshal.ReadIntPtr(buf);
                IntPtr pCount = GetSidSubAuthorityCount(pSid);
                byte count = Marshal.ReadByte(pCount);
                if (count == 0) return null;
                IntPtr pRid = GetSidSubAuthority(pSid, (uint)(count - 1));
                return (uint)Marshal.ReadInt32(pRid);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            CloseHandle(hToken);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);
}

/// <summary>
/// 顶部非激活胶囊提示条（WS_EX_NOACTIVATE）：用于 UIPI 拦截贴回时提示「已复制，按 Ctrl+V 粘贴」。
/// </summary>
public sealed class PasteHint
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private Window? _window;
    private Border? _rootBorder;
    private TextBlock? _label;
    private readonly DispatcherTimer _hideTimer;

    public PasteHint()
    {
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _hideTimer.Tick += (_, _) => Hide();
    }

    public void Show(string? message = null)
    {
        EnsureWindow();
        if (_window is null || _rootBorder is null || _label is null) return;

        bool dark = ThemePalette.IsDarkMode();
        _rootBorder.Background = ThemePalette.Brush(ThemePalette.ChromeFill(dark));
        _rootBorder.BorderBrush = ThemePalette.Brush(ThemePalette.ChromeBorder(dark));
        _label.Foreground = ThemePalette.Brush(ThemePalette.ChromeText(dark));
        _label.Text = message ?? Localization.L("Copied. Press Ctrl+V to paste.");

        _rootBorder.Measure(new Size(600, 100));
        double w = Math.Max(220, _rootBorder.DesiredSize.Width + 4);
        double h = Math.Max(40, _rootBorder.DesiredSize.Height + 4);

        var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
        var rect = ScreenPlacement.TopBanner(cx, cy, ScreenPlacement.EnumerateMonitors(), w, h, topMargin: 12);
        _window.Left = rect.Left;
        _window.Top = rect.Top;
        _window.Width = rect.Width;
        _window.Height = rect.Height;
        _window.Show();

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void Hide()
    {
        _hideTimer.Stop();
        _window?.Hide();
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;

        _label = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = ThemePalette.Brush(ThemePalette.PaperInk),
            VerticalAlignment = VerticalAlignment.Center
        };

        _rootBorder = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = ThemePalette.Brush(ThemePalette.ChromeFill(dark: false)),
            BorderBrush = ThemePalette.Brush(ThemePalette.ChromeBorder(dark: false)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 8, 16, 8),
            Child = _label
        };

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = null,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.Manual,
            Content = _rootBorder
        };
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            int ex = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, ex | WsExNoActivate | WsExToolWindow);
        };
        _window = win;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
