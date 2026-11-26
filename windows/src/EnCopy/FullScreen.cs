using System.Runtime.InteropServices;
using System.Text;

namespace EnCopy;

public enum HoverAction
{
    None,
    RevealByHover,
    Retract
}

/// <summary>
/// 全屏应用避让检测（T-060）：
/// 同时覆盖 SHQueryUserNotificationState（D3D 独占全屏 / 演示模式）与显示器级无边框全屏窗口（浏览器 F11、全屏视频等）。
/// 默认避让全屏应用；显式按 Alt+C（openedOnPurpose）或开启「在全屏应用上也显示」时放行。
/// </summary>
public static class FullScreenDetector
{
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow"
    };

    public static bool IsWindowCoveringMonitor(PixelRect windowRect, string? windowClass, DisplayMonitor monitor)
    {
        if (!string.IsNullOrWhiteSpace(windowClass) && ShellWindowClasses.Contains(windowClass.Trim()))
        {
            return false;
        }
        if (windowRect.Width <= 0 || windowRect.Height <= 0 || monitor.Bounds.Width <= 0 || monitor.Bounds.Height <= 0)
        {
            return false;
        }
        return windowRect.Left <= monitor.Bounds.Left &&
               windowRect.Top <= monitor.Bounds.Top &&
               windowRect.Right >= monitor.Bounds.Right &&
               windowRect.Bottom >= monitor.Bounds.Bottom;
    }

    public static bool IsNotificationStateFullScreen(int qunsState) =>
        qunsState is 3 or 4; // QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4

    public static bool ShouldSuppressAutoReveal(bool isMonitorFullScreen, bool showOverFullScreen, bool openedOnPurpose) =>
        !openedOnPurpose && !showOverFullScreen && isMonitorFullScreen;

    public static bool BlocksLineOnMonitor(DisplayMonitor monitor, bool showOverFullScreen, IntPtr selfHwnd)
    {
        if (showOverFullScreen) return false;

        if (monitor.IsPrimary && SHQueryUserNotificationState(out int quns) == 0 && IsNotificationStateFullScreen(quns))
        {
            return true;
        }

        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == selfHwnd || !IsWindowVisible(fg) || IsIconic(fg))
        {
            return false;
        }

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        var sb = new StringBuilder(96);
        GetClassName(fg, sb, sb.Capacity);
        if (!GetWindowRect(fg, out var rect))
        {
            return false;
        }

        return IsWindowCoveringMonitor(
            new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
            sb.ToString(),
            monitor);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}

/// <summary>
/// 屏幕顶边正中 1/3 悬停路过状态机（T-060）：
/// 仅在 y <= Bounds.Top + 2px 且正中 1/3 区域静置满 0.25s（无鼠标按键按下）时滑下；
/// 在顶栏点击（如点浏览器标签或顶栏图标）会抑制触发直到指针离开顶栏；离开面板后延时 0.45s 收起。
/// </summary>
public sealed class TopEdgeHoverTracker
{
    public const int TopEdgeTolerancePx = 2;
    private const int TopBarSuppressionHeightPx = 40;
    private const int PanelGraceMarginPx = 24;

    public static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan RetractDelay = TimeSpan.FromMilliseconds(450);

    private DateTime? _enteredTopUtc;
    private DateTime? _leftPanelUtc;
    private bool _suppressedByTopClick;

    public static bool IsInTopCenterBand(int cursorX, int cursorY, DisplayMonitor monitor)
    {
        if (monitor.Bounds.Width <= 0 || monitor.Bounds.Height <= 0) return false;
        if (cursorY < monitor.Bounds.Top || cursorY > monitor.Bounds.Top + TopEdgeTolerancePx)
        {
            return false;
        }
        int third = monitor.Bounds.Width / 3;
        int minX = monitor.Bounds.Left + third;
        int maxX = monitor.Bounds.Right - third;
        return cursorX >= minX && cursorX < maxX;
    }

    public HoverAction Update(
        int cursorX,
        int cursorY,
        DisplayMonitor monitor,
        bool mouseButtonDown,
        bool isRevealed,
        bool isPinnedBySummon,
        bool keepOpenForMenuOrDrag,
        int panelBottomPhysicalY,
        DateTime nowUtc)
    {
        if (!isRevealed)
        {
            _leftPanelUtc = null;
            bool inTopStrip = cursorY >= monitor.Bounds.Top && cursorY <= monitor.Bounds.Top + TopBarSuppressionHeightPx;
            if (mouseButtonDown && inTopStrip)
            {
                _suppressedByTopClick = true;
                _enteredTopUtc = null;
                return HoverAction.None;
            }
            if (_suppressedByTopClick)
            {
                if (!mouseButtonDown && !inTopStrip)
                {
                    _suppressedByTopClick = false;
                }
                return HoverAction.None;
            }

            if (!mouseButtonDown && IsInTopCenterBand(cursorX, cursorY, monitor))
            {
                _enteredTopUtc ??= nowUtc;
                if (nowUtc - _enteredTopUtc.Value >= RevealDelay)
                {
                    _enteredTopUtc = null;
                    return HoverAction.RevealByHover;
                }
            }
            else
            {
                _enteredTopUtc = null;
            }
            return HoverAction.None;
        }

        _enteredTopUtc = null;
        if (isPinnedBySummon)
        {
            _leftPanelUtc = null;
            return HoverAction.None;
        }

        if (keepOpenForMenuOrDrag || mouseButtonDown || cursorY <= panelBottomPhysicalY + PanelGraceMarginPx)
        {
            _leftPanelUtc = null;
            return HoverAction.None;
        }

        _leftPanelUtc ??= nowUtc;
        if (nowUtc - _leftPanelUtc.Value >= RetractDelay)
        {
            _leftPanelUtc = null;
            return HoverAction.Retract;
        }
        return HoverAction.None;
    }

    public void Reset()
    {
        _enteredTopUtc = null;
        _leftPanelUtc = null;
        _suppressedByTopClick = false;
    }
}
