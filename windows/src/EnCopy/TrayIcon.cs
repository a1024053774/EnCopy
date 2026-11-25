using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace EnCopy;

/// <summary>
/// 系统托盘图标与菜单（麻绳双夹子矢量图标），并承接 Alt+C 冲突气泡通知。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _notify;
    private readonly WF.ToolStripMenuItem _summonItem;
    private readonly WF.ToolStripMenuItem _pauseItem;
    private readonly SD.Icon _activeIcon;
    private readonly SD.Icon _pausedIcon;

    public TrayIcon(
        Action onToggleSummon,
        Func<bool> isSummoned,
        Action<bool> onSetPaused,
        Func<bool> isPaused,
        Action onQuit)
    {
        _activeIcon = CreateRopeIcon(paused: false);
        _pausedIcon = CreateRopeIcon(paused: true);

        _summonItem = new WF.ToolStripMenuItem(Localization.L("Show line"), null, (_, _) => onToggleSummon());
        _pauseItem = new WF.ToolStripMenuItem(Localization.L("Pause recording"), null, (_, _) =>
        {
            bool next = !isPaused();
            onSetPaused(next);
            UpdateState(isSummoned(), next);
        })
        {
            ToolTipText = Localization.L("Nothing you copy is kept while this is on")
        };
        var openFolderItem = new WF.ToolStripMenuItem(Localization.L("Open data folder"), null, (_, _) => OpenDataFolder());
        var quitItem = new WF.ToolStripMenuItem(Localization.L("Quit EnCopy"), null, (_, _) => onQuit());

        var menu = new WF.ContextMenuStrip();
        menu.Opening += (_, _) => UpdateState(isSummoned(), isPaused());
        menu.Items.Add(_summonItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(openFolderItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(quitItem);

        _notify = new WF.NotifyIcon
        {
            Icon = isPaused() ? _pausedIcon : _activeIcon,
            Text = AppIdentity.AppName,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
            {
                onToggleSummon();
            }
        };
    }

    public void UpdateState(bool summoned, bool paused)
    {
        _summonItem.Text = summoned ? Localization.L("Hide line") : Localization.L("Show line");
        _pauseItem.Checked = paused;
        _notify.Icon = paused ? _pausedIcon : _activeIcon;
    }

    public void ShowHotKeyConflict(Shortcut shortcut)
    {
        _notify.BalloonTipTitle = Localization.L("Alt+C could not be registered");
        _notify.BalloonTipText = Localization.Format("Shortcut {0} is in use by another app.", shortcut.Display);
        _notify.BalloonTipIcon = WF.ToolTipIcon.Warning;
        _notify.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _activeIcon.Dispose();
        _pausedIcon.Dispose();
    }

    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(DataFolder.Root);
        Process.Start(new ProcessStartInfo
        {
            FileName = DataFolder.Root,
            UseShellExecute = true
        });
    }

    private static SD.Icon CreateRopeIcon(bool paused)
    {
        using var bmp = new SD.Bitmap(32, 32, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);

            var ropeColor = paused ? SD.Color.FromArgb(145, 145, 145) : SD.Color.FromArgb(204, 152, 92);
            var pegColor = paused ? SD.Color.FromArgb(165, 165, 165) : SD.Color.FromArgb(236, 198, 134);
            var cardColor = paused ? SD.Color.FromArgb(190, 190, 190) : SD.Color.FromArgb(252, 248, 236);

            using var ropePen = new SD.Pen(ropeColor, 3.0f)
            {
                StartCap = SD.Drawing2D.LineCap.Round,
                EndCap = SD.Drawing2D.LineCap.Round
            };
            g.DrawBezier(ropePen,
                new SD.PointF(2, 11),
                new SD.PointF(10, 17),
                new SD.PointF(22, 17),
                new SD.PointF(30, 11));

            DrawCard(g, 8, 13, 9, 12, -5f, cardColor, pegColor);
            DrawCard(g, 16, 13, 9, 12, 6f, cardColor, pegColor);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = SD.Icon.FromHandle(hIcon);
            return (SD.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawCard(
        SD.Graphics g,
        float x,
        float y,
        float w,
        float h,
        float angle,
        SD.Color cardColor,
        SD.Color pegColor)
    {
        var state = g.Save();
        g.TranslateTransform(x + w / 2f, y);
        g.RotateTransform(angle);
        using var cardBrush = new SD.SolidBrush(cardColor);
        using var cardBorder = new SD.Pen(SD.Color.FromArgb(120, 72, 54, 34), 1.0f);
        g.FillRectangle(cardBrush, -w / 2f, 2, w, h);
        g.DrawRectangle(cardBorder, -w / 2f, 2, w, h);
        using var pegBrush = new SD.SolidBrush(pegColor);
        g.FillRectangle(pegBrush, -2f, -3f, 4f, 7f);
        g.Restore(state);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
