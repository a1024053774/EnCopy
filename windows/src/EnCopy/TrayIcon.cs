using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace EnCopy;

/// <summary>
/// 系统托盘图标与菜单（麻绳双夹子矢量图标），提供唤起、暂停、绳上容量、快捷键修改与数据文件夹入口。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _notify;
    private readonly WF.ToolStripMenuItem _summonItem;
    private readonly WF.ToolStripMenuItem _pauseItem;
    private readonly WF.ToolStripMenuItem _shortcutItem;
    private readonly WF.ToolStripMenuItem _keepFitItem;
    private readonly List<(int Value, WF.ToolStripMenuItem Item)> _keepChoiceItems = [];
    private readonly SD.Icon _activeIcon;
    private readonly SD.Icon _pausedIcon;

    public TrayIcon(
        Action onToggleSummon,
        Func<bool> isSummoned,
        Action<bool> onSetPaused,
        Func<bool> isPaused,
        Action<int?> onSetKeepOnLine,
        Func<int?> getKeepOnLine,
        Action onOpenShortcutRecorder,
        Func<Shortcut?> getActiveShortcut,
        Action onQuit)
    {
        _activeIcon = CreateRopeIcon(paused: false);
        _pausedIcon = CreateRopeIcon(paused: true);

        _summonItem = new WF.ToolStripMenuItem(Localization.L("Show line"), null, (_, _) => onToggleSummon());
        _pauseItem = new WF.ToolStripMenuItem(Localization.L("Pause recording"), null, (_, _) =>
        {
            bool next = !isPaused();
            onSetPaused(next);
            UpdateState(isSummoned(), next, getKeepOnLine(), getActiveShortcut());
        })
        {
            ToolTipText = Localization.L("Nothing you copy is kept while this is on")
        };

        var keepMenu = new WF.ToolStripMenuItem(Localization.L("Keep on line"));
        _keepFitItem = new WF.ToolStripMenuItem(Localization.L("As many as fit"), null, (_, _) =>
        {
            onSetKeepOnLine(null);
            UpdateState(isSummoned(), isPaused(), null, getActiveShortcut());
        });
        keepMenu.DropDownItems.Add(_keepFitItem);
        keepMenu.DropDownItems.Add(new WF.ToolStripSeparator());
        foreach (int choice in AppSettings.KeepChoices)
        {
            int captured = choice;
            var item = new WF.ToolStripMenuItem(Localization.Format("{0} items", captured), null, (_, _) =>
            {
                onSetKeepOnLine(captured);
                UpdateState(isSummoned(), isPaused(), captured, getActiveShortcut());
            });
            _keepChoiceItems.Add((captured, item));
            keepMenu.DropDownItems.Add(item);
        }

        _shortcutItem = new WF.ToolStripMenuItem(FormatShortcutLabel(getActiveShortcut()), null, (_, _) => onOpenShortcutRecorder());

        var openFolderItem = new WF.ToolStripMenuItem(Localization.L("Open data folder"), null, (_, _) => OpenDataFolder());
        var quitItem = new WF.ToolStripMenuItem(Localization.L("Quit EnCopy"), null, (_, _) => onQuit());

        var menu = new WF.ContextMenuStrip();
        menu.Opening += (_, _) => UpdateState(isSummoned(), isPaused(), getKeepOnLine(), getActiveShortcut());
        menu.Items.Add(_summonItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(keepMenu);
        menu.Items.Add(_shortcutItem);
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

    public void UpdateState(bool summoned, bool paused, int? keepOnLine, Shortcut? activeShortcut)
    {
        _summonItem.Text = summoned ? Localization.L("Hide line") : Localization.L("Show line");
        _pauseItem.Checked = paused;
        _keepFitItem.Checked = keepOnLine is null;
        foreach (var (value, item) in _keepChoiceItems)
        {
            item.Checked = keepOnLine == value;
        }
        _shortcutItem.Text = FormatShortcutLabel(activeShortcut);
        _notify.Icon = paused ? _pausedIcon : _activeIcon;
    }

    public void ShowHotKeyConflict(Shortcut shortcut)
    {
        _notify.BalloonTipTitle = Localization.L("Global shortcut could not be registered");
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

    private static string FormatShortcutLabel(Shortcut? shortcut)
    {
        var keyText = shortcut is { } s ? s.Display : Localization.L("None");
        return $"{Localization.L("Change Shortcut…")} ({keyText})";
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
