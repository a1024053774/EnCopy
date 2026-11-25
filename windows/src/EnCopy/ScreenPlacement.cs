using System.Runtime.InteropServices;

namespace EnCopy;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public readonly record struct DisplayMonitor(PixelRect Bounds, PixelRect WorkArea, double DpiScale, bool IsPrimary);

public readonly record struct DipRect(double Left, double Top, double Width, double Height);

/// <summary>
/// 按鼠标所在显示器及该屏 DPI 计算顶部晾衣绳面板、横幅与卡片容量，支持真实显示器与测试注入显示器列表。
/// </summary>
public static class ScreenPlacement
{
    public const double CardWidth = 148.0;
    public const double CardHeight = 108.0;
    public const double CardSpacing = 172.0;
    public const double RopeTop = 10.0;
    public const double LineBandHeight = 208.0;

    // Known unverified (T-058 / T-059): Current dev machine has a single display (\\.\DISPLAY1 at 125% DPI),
    // so mixed-DPI multi-monitor Window.Left/Top DIP-to-physical conversion is covered by unit math
    // (TestScreenPlacementMultiMonitorDpi) rather than hardware transition across monitors.
    // Impact: Secondary monitor placement at a different DPI scale may need post-SourceInitialized physical SetWindowPos adjustment.
    // Removal condition: Verify live on a physical external monitor with differing DPI scale and remove or adjust.
    public static DipRect TopLineStrip(
        int cursorPhysicalX,
        int cursorPhysicalY,
        IReadOnlyList<DisplayMonitor> monitors,
        double panelHeight)
    {
        var mon = PickMonitor(cursorPhysicalX, cursorPhysicalY, monitors);
        double scale = mon.DpiScale > 0 ? mon.DpiScale : 1.0;
        double workLeft = mon.WorkArea.Left / scale;
        double workTop = mon.WorkArea.Top / scale;
        double workWidth = mon.WorkArea.Width / scale;
        double workHeight = mon.WorkArea.Height / scale;
        double height = Math.Min(panelHeight, Math.Max(260, workHeight - 8));
        return new DipRect(workLeft, workTop, workWidth, height);
    }

    public static int ComputeVisibleCapacity(double monitorWorkWidthDip)
    {
        return Math.Max(3, (int)Math.Floor((monitorWorkWidthDip - 160.0) / CardSpacing));
    }

    public static double RopeSag(double width) => Math.Min(28.0, width * 0.017);

    public static double RopeY(double x, double width, double sagScale = 1.0)
    {
        if (width <= 0) return RopeTop;
        double f = Math.Clamp(x / width, 0.0, 1.0);
        return RopeTop + 4.0 * RopeSag(width) * sagScale * f * (1.0 - f);
    }

    public static DisplayMonitor PickMonitor(int cursorPhysicalX, int cursorPhysicalY, IReadOnlyList<DisplayMonitor> monitors)
    {
        if (monitors.Count == 0)
        {
            return new DisplayMonitor(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true);
        }
        foreach (var m in monitors)
        {
            if (m.Bounds.Contains(cursorPhysicalX, cursorPhysicalY))
            {
                return m;
            }
        }
        foreach (var m in monitors)
        {
            if (m.IsPrimary) return m;
        }
        return monitors[0];
    }

    public static DipRect TopBanner(
        int cursorPhysicalX,
        int cursorPhysicalY,
        IReadOnlyList<DisplayMonitor> monitors,
        double width,
        double height,
        double topMargin = 10)
    {
        var mon = PickMonitor(cursorPhysicalX, cursorPhysicalY, monitors);
        double scale = mon.DpiScale > 0 ? mon.DpiScale : 1.0;
        double workLeft = mon.WorkArea.Left / scale;
        double workTop = mon.WorkArea.Top / scale;
        double workWidth = mon.WorkArea.Width / scale;
        double left = workLeft + (workWidth - width) / 2.0;
        double top = workTop + topMargin;
        return new DipRect(left, top, width, height);
    }

    public static (int X, int Y) CurrentCursorPhysical()
    {
        return GetCursorPos(out var pt) ? (pt.X, pt.Y) : (0, 0);
    }

    public static IReadOnlyList<DisplayMonitor> EnumerateMonitors()
    {
        var list = new List<DisplayMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref info))
            {
                double scale = 1.0;
                if (GetDpiForMonitor(hMon, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                {
                    scale = dpiX / 96.0;
                }
                list.Add(new DisplayMonitor(
                    new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                    new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
                    scale,
                    (info.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
