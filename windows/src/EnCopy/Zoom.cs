using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace EnCopy;

public sealed class HoverZoomState
{
    public static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(350);
    public static readonly TimeSpan GapGrace = TimeSpan.FromMilliseconds(150);

    private string? _candidatePath;
    private DateTime _candidateSinceUtc;
    private DateTime? _lostSinceUtc;

    public string? ShownPath { get; private set; }

    public string? Update(string? hoveredPath, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(hoveredPath))
        {
            _candidatePath = null;
            if (ShownPath is null) return null;
            _lostSinceUtc ??= nowUtc;
            if (nowUtc - _lostSinceUtc.Value >= GapGrace)
            {
                Hide();
                return null;
            }
            return ShownPath;
        }

        _lostSinceUtc = null;
        if (ShownPath is not null)
        {
            if (string.Equals(ShownPath, hoveredPath, StringComparison.OrdinalIgnoreCase))
            {
                return ShownPath;
            }
            // 已处于展开预览态时横移到相邻卡片：立即切到新卡片（对齐 Zoom.swift 注释）
            ShownPath = hoveredPath;
            _candidatePath = hoveredPath;
            _candidateSinceUtc = nowUtc;
            return ShownPath;
        }

        if (!string.Equals(_candidatePath, hoveredPath, StringComparison.OrdinalIgnoreCase))
        {
            _candidatePath = hoveredPath;
            _candidateSinceUtc = nowUtc;
            return null;
        }

        if (nowUtc - _candidateSinceUtc >= OpenDelay)
        {
            ShownPath = hoveredPath;
            return ShownPath;
        }
        return null;
    }

    public void Hide()
    {
        ShownPath = null;
        _candidatePath = null;
        _lostSinceUtc = null;
    }
}

/// <summary>
/// 悬停放大预览浮窗（T-060）：鼠标穿透、不抢焦点，固定在屏幕水平正中、绳子正下方。
/// </summary>
public sealed class HoverZoomController : IDisposable
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private Window? _window;
    private Border? _cardFrame;
    private string? _renderedKey;

    public HoverZoomState State { get; } = new();

    public static double ComputeZoomWindowTopDip(
        double workTopDip,
        double panelContentBottomDip,
        double workHeightDip,
        double zoomWindowHeightDip)
    {
        double desired = Math.Max(workTopDip + 228.0, panelContentBottomDip + 8.0);
        double maxTop = Math.Max(workTopDip + 40.0, workTopDip + workHeightDip - zoomWindowHeightDip - 8.0);
        return Math.Min(desired, maxTop);
    }

    public void Update(
        Note? hoveredNote,
        DisplayMonitor monitor,
        bool isDark,
        LineTheme theme,
        double panelContentBottomDip = 0.0,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var activePath = State.Update(hoveredNote?.FilePath, now);
        if (activePath is null || hoveredNote is null)
        {
            if (activePath is null)
            {
                _window?.Hide();
                _renderedKey = null;
            }
            return;
        }

        if (IsInChecksRunner())
        {
            return;
        }

        EnsureWindow();
        if (_window is null || _cardFrame is null) return;

        string key = $"{hoveredNote.FilePath}|{theme}|{isDark}";
        if (!string.Equals(_renderedKey, key, StringComparison.Ordinal))
        {
            _renderedKey = key;
            _cardFrame.Child = BuildZoomContent(hoveredNote, theme);
            _cardFrame.Background = ThemePalette.Brush(ThemePalette.CardFill(theme, hoveredNote.Paper));
            _cardFrame.BorderBrush = ThemePalette.Brush(ThemePalette.PickedColor(theme));
            _cardFrame.BorderThickness = new Thickness(2.0);
            _cardFrame.CornerRadius = new CornerRadius(Math.Max(6.0, ThemePalette.CardCorner(theme) * 1.5));
        }

        double scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        double workLeft = monitor.WorkArea.Left / scale;
        double workTop = monitor.WorkArea.Top / scale;
        double workWidth = monitor.WorkArea.Width / scale;
        double workHeight = monitor.WorkArea.Height / scale;
        double effectiveBottom = panelContentBottomDip > workTop ? panelContentBottomDip : workTop + 280.0;

        double maxW = Math.Min(520.0, workWidth - 48.0);
        double maxH = Math.Min(360.0, Math.Max(160.0, workTop + workHeight - effectiveBottom - 40.0));
        _cardFrame.Measure(new Size(maxW, maxH));
        double w = Math.Clamp(_cardFrame.DesiredSize.Width + 32.0, 280.0, maxW + 32.0);
        double h = Math.Clamp(_cardFrame.DesiredSize.Height + 32.0, 160.0, maxH + 32.0);

        _window.Width = w;
        _window.Height = h;
        _window.Left = workLeft + (workWidth - w) / 2.0;
        _window.Top = ComputeZoomWindowTopDip(workTop, effectiveBottom, workHeight, h);
        if (!_window.IsVisible)
        {
            _window.Show();
        }
    }

    public void Hide()
    {
        State.Hide();
        _renderedKey = null;
        _window?.Hide();
    }

    public void Dispose()
    {
        State.Hide();
        if (_window is not null)
        {
            _window.Close();
            _window = null;
        }
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;

        _cardFrame = new Border
        {
            Margin = new Thickness(16),
            Padding = new Thickness(14),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 8, Opacity = 0.30, Color = Colors.Black }
        };

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsHitTestVisible = false,
            Content = _cardFrame
        };
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            int ex = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, ex | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };
        _window = win;
    }

    private static UIElement BuildZoomContent(Note note, LineTheme theme)
    {
        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, note.Paper));
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, note.Paper));

        if (note.Kind == ItemKind.Image && File.Exists(note.FilePath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 640;
                bmp.UriSource = new Uri(note.FilePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return new Image
                {
                    Source = bmp,
                    MaxWidth = 480,
                    MaxHeight = 320,
                    Stretch = Stretch.Uniform
                };
            }
            catch
            {
                // 解码失败时退回文本摘要展示
            }
        }

        var stack = new StackPanel { MaxWidth = 470 };
        stack.Children.Add(new ContentControl
        {
            Content = PaperCardView.BuildKindTag(note.Kind, scale: 1.05),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8)
        });

        if (!string.IsNullOrEmpty(note.Title))
        {
            stack.Children.Add(new TextBlock
            {
                Text = note.Title,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = ThemePalette.CardFont(theme, code: note.IsCode),
                Foreground = ink,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        var bodyText = !string.IsNullOrEmpty(note.Body) ? note.Body : note.FullText;
        if (!string.IsNullOrEmpty(bodyText))
        {
            stack.Children.Add(new TextBlock
            {
                Text = bodyText.Length > 900 ? bodyText[..900] + "…" : bodyText,
                FontSize = note.IsCode ? 12.0 : 13.0,
                FontFamily = ThemePalette.CardFont(theme, code: note.IsCode || note.Kind == ItemKind.Link),
                Foreground = string.IsNullOrEmpty(note.Title) ? ink : soft,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 230,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = note.Caption,
            FontSize = 11,
            Foreground = soft,
            Margin = new Thickness(0, 8, 0, 0)
        });
        return stack;
    }

    private static bool IsInChecksRunner() =>
        string.Equals(Environment.GetEnvironmentVariable(DataFolder.ChecksRunnerEnv), "1", StringComparison.Ordinal);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
