using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace EnCopy;

/// <summary>
/// 系统托盘图标与菜单（麻绳双夹子矢量图标）：
/// 图标随暂停状态、绳上挂物数量（0/1/2）与后台新复制轻晃（[8, -6, 4, -2, 0]° over 0.6s）动态重绘。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _notify;
    private readonly WF.ToolStripMenuItem _summonItem;
    private readonly WF.ToolStripMenuItem _pauseItem;
    private readonly WF.ToolStripMenuItem _shortcutItem;
    private readonly WF.ToolStripMenuItem _keepFitItem;
    private readonly WF.ToolStripMenuItem _zoomHoverItem;
    private readonly WF.ToolStripMenuItem _hangAllBackItem;
    private readonly List<(int Value, WF.ToolStripMenuItem Item)> _keepChoiceItems = [];
    private readonly List<(LineTheme Theme, WF.ToolStripMenuItem Item)> _themeItems = [];
    private readonly DispatcherTimer _swayTimer;

    private readonly Func<bool> _isSummoned;
    private readonly Func<bool> _isPaused;
    private readonly Func<int> _getItemCount;
    private readonly Func<int?> _getKeepOnLine;
    private readonly Func<Shortcut?> _getActiveShortcut;
    private readonly Func<LineTheme> _getTheme;
    private readonly Func<bool> _getZoomOnHover;
    private readonly Func<bool> _hasHeldItems;

    private SD.Icon? _currentIcon;
    private int _swayStepIndex = -1;

    public TrayIcon(
        Action onToggleSummon,
        Func<bool> isSummoned,
        Action<bool> onSetPaused,
        Func<bool> isPaused,
        Func<int> getItemCount,
        Action<int?> onSetKeepOnLine,
        Func<int?> getKeepOnLine,
        Action<LineTheme> onSetTheme,
        Func<LineTheme> getTheme,
        Action<bool> onSetZoomOnHover,
        Func<bool> getZoomOnHover,
        Action onHangAllBack,
        Func<bool> hasHeldItems,
        Action onOpenShortcutRecorder,
        Func<Shortcut?> getActiveShortcut,
        Action onQuit,
        Action? onOpenCleanup = null,
        Action? onOpenSettings = null)
    {
        _isSummoned = isSummoned;
        _isPaused = isPaused;
        _getItemCount = getItemCount;
        _getKeepOnLine = getKeepOnLine;
        _getActiveShortcut = getActiveShortcut;
        _getTheme = getTheme;
        _getZoomOnHover = getZoomOnHover;
        _hasHeldItems = hasHeldItems;

        _swayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Motion.TraySwayDuration / Motion.TraySwayAngles.Length) };
        _swayTimer.Tick += (_, _) => StepSway();

        _summonItem = new WF.ToolStripMenuItem(Localization.L("Show line"), null, (_, _) => onToggleSummon());
        _pauseItem = new WF.ToolStripMenuItem(Localization.L("Pause recording"), null, (_, _) =>
        {
            bool next = !isPaused();
            onSetPaused(next);
            Refresh();
        })
        {
            ToolTipText = Localization.L("Nothing you copy is kept while this is on")
        };

        var themeMenu = new WF.ToolStripMenuItem(Localization.L("Theme"));
        foreach (var theme in Enum.GetValues<LineTheme>())
        {
            var captured = theme;
            var tItem = new WF.ToolStripMenuItem(ThemePalette.ThemeTitle(captured), null, (_, _) =>
            {
                onSetTheme(captured);
                Refresh();
            });
            _themeItems.Add((captured, tItem));
            themeMenu.DropDownItems.Add(tItem);
        }

        var keepMenu = new WF.ToolStripMenuItem(Localization.L("Keep on line"));
        _keepFitItem = new WF.ToolStripMenuItem(Localization.L("As many as fit"), null, (_, _) =>
        {
            onSetKeepOnLine(null);
            Refresh();
        });
        keepMenu.DropDownItems.Add(_keepFitItem);
        keepMenu.DropDownItems.Add(new WF.ToolStripSeparator());
        foreach (int choice in AppSettings.KeepChoices)
        {
            int captured = choice;
            var item = new WF.ToolStripMenuItem(Localization.Format("{0} items", captured), null, (_, _) =>
            {
                onSetKeepOnLine(captured);
                Refresh();
            });
            _keepChoiceItems.Add((captured, item));
            keepMenu.DropDownItems.Add(item);
        }

        _zoomHoverItem = new WF.ToolStripMenuItem(Localization.L("Zoom on hover"), null, (_, _) =>
        {
            onSetZoomOnHover(!getZoomOnHover());
            Refresh();
        });

        _hangAllBackItem = new WF.ToolStripMenuItem(Localization.L("Hang Everything Back"), null, (_, _) =>
        {
            onHangAllBack();
            Refresh();
        });

        _shortcutItem = new WF.ToolStripMenuItem(FormatShortcutLabel(getActiveShortcut()), null, (_, _) => onOpenShortcutRecorder());

        var cleanupItem = onOpenCleanup is not null
            ? new WF.ToolStripMenuItem(Localization.L("Clean Up History…"), null, (_, _) => onOpenCleanup())
            : null;
        var settingsItem = onOpenSettings is not null
            ? new WF.ToolStripMenuItem(Localization.L("Settings…"), null, (_, _) => onOpenSettings())
            : null;
        var openFolderItem = new WF.ToolStripMenuItem(Localization.L("Open data folder"), null, (_, _) => OpenDataFolder());
        var quitItem = new WF.ToolStripMenuItem(Localization.L("Quit EnCopy"), null, (_, _) => onQuit());

        var menu = new WF.ContextMenuStrip();
        menu.Opening += (_, _) => Refresh();
        menu.Items.Add(_summonItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(themeMenu);
        menu.Items.Add(keepMenu);
        menu.Items.Add(_zoomHoverItem);
        menu.Items.Add(_hangAllBackItem);
        menu.Items.Add(_shortcutItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        if (cleanupItem is not null) menu.Items.Add(cleanupItem);
        if (settingsItem is not null) menu.Items.Add(settingsItem);
        menu.Items.Add(openFolderItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(quitItem);

        _currentIcon = CreateRopeIcon(isPaused(), getItemCount(), swayAngle: 0f);
        _notify = new WF.NotifyIcon
        {
            Icon = _currentIcon,
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

    public void Refresh()
    {
        bool summoned = _isSummoned();
        bool paused = _isPaused();
        int? keepOnLine = _getKeepOnLine();
        var activeTheme = _getTheme();

        _summonItem.Text = summoned ? Localization.L("Hide line") : Localization.L("Show line");
        _pauseItem.Checked = paused;
        _keepFitItem.Checked = keepOnLine is null;
        foreach (var (value, item) in _keepChoiceItems)
        {
            item.Checked = keepOnLine == value;
        }
        foreach (var (theme, item) in _themeItems)
        {
            item.Checked = theme == activeTheme;
            item.Text = ThemePalette.ThemeTitle(theme);
        }
        _zoomHoverItem.Checked = _getZoomOnHover();
        _hangAllBackItem.Visible = _hasHeldItems();
        _shortcutItem.Text = FormatShortcutLabel(_getActiveShortcut());

        if (_swayStepIndex < 0)
        {
            ApplyIcon(paused, _getItemCount(), swayAngle: 0f);
        }
    }

    public void TriggerCopySway()
    {
        if (Motion.IsReduced || _isPaused())
        {
            Refresh();
            return;
        }
        _swayStepIndex = 0;
        ApplyIcon(_isPaused(), _getItemCount(), Motion.TraySwayAngles[0]);
        _swayTimer.Stop();
        _swayTimer.Start();
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
        _swayTimer.Stop();
        _notify.Visible = false;
        _notify.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }

    public static byte[] RenderIconRgbaForTest(bool paused, int itemCount, float swayAngle = 0f)
    {
        using var bmp = CreateRopeBitmap(paused, itemCount, swayAngle);
        var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, SD.Imaging.ImageLockMode.ReadOnly, SD.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bmp.Width * bmp.Height * 4];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private void StepSway()
    {
        _swayStepIndex++;
        if (_swayStepIndex < 0 || _swayStepIndex >= Motion.TraySwayAngles.Length)
        {
            _swayStepIndex = -1;
            _swayTimer.Stop();
            ApplyIcon(_isPaused(), _getItemCount(), swayAngle: 0f);
            return;
        }
        ApplyIcon(_isPaused(), _getItemCount(), Motion.TraySwayAngles[_swayStepIndex]);
    }

    private void ApplyIcon(bool paused, int itemCount, float swayAngle)
    {
        var next = CreateRopeIcon(paused, itemCount, swayAngle);
        var prev = _currentIcon;
        _currentIcon = next;
        _notify.Icon = next;
        prev?.Dispose();
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

    private static SD.Icon CreateRopeIcon(bool paused, int itemCount, float swayAngle)
    {
        using var bmp = CreateRopeBitmap(paused, itemCount, swayAngle);
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

    private static SD.Bitmap CreateRopeBitmap(bool paused, int itemCount, float swayAngle)
    {
        var bmp = new SD.Bitmap(32, 32, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
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

        int visibleCards = Math.Clamp(itemCount, 0, 2);
        if (visibleCards == 0)
        {
            using var pegBrush = new SD.SolidBrush(pegColor);
            g.FillRectangle(pegBrush, 10f, 9f, 3.5f, 7f);
            g.FillRectangle(pegBrush, 18.5f, 9f, 3.5f, 7f);
        }
        else if (visibleCards == 1)
        {
            DrawCard(g, 11.5f, 13f, 9f, 12f, -3f + swayAngle, cardColor, pegColor);
        }
        else
        {
            DrawCard(g, 7.5f, 13f, 9f, 12f, -5f + swayAngle, cardColor, pegColor);
            DrawCard(g, 16.5f, 13f, 9f, 12f, 6f + swayAngle * 0.85f, cardColor, pegColor);
        }

        return bmp;
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
