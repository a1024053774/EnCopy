using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace EnCopy;

public static class HeldGeometry
{
    public static Rect ZoomAroundAnchor(
        Rect currentFrame,
        Size naturalSize,
        bool isTextNote,
        ref double noteScale,
        double factor,
        Point anchorDip)
    {
        if (currentFrame.Width <= 1 || currentFrame.Height <= 1 || factor <= 0)
        {
            return currentFrame;
        }

        double fx = Math.Clamp((anchorDip.X - currentFrame.Left) / currentFrame.Width, 0.0, 1.0);
        double fy = Math.Clamp((anchorDip.Y - currentFrame.Top) / currentFrame.Height, 0.0, 1.0);

        if (isTextNote)
        {
            double nextScale = Math.Clamp(noteScale * factor, 0.9, 8.0);
            double ratio = nextScale / Math.Max(0.1, noteScale);
            double w = currentFrame.Width * ratio;
            double h = currentFrame.Height * ratio;
            if (w < 120.0 || h < 80.0)
            {
                return currentFrame;
            }
            noteScale = nextScale;
            return new Rect(anchorDip.X - w * fx, anchorDip.Y - h * fy, w, h);
        }

        double baseW = Math.Max(60.0, naturalSize.Width);
        double baseH = Math.Max(60.0, naturalSize.Height);
        double imgScale = Math.Clamp((currentFrame.Width * factor) / baseW, 0.15, 4.0);
        double nextW = baseW * imgScale;
        double nextH = baseH * imgScale;
        if (nextW < 60.0 || nextH < 60.0)
        {
            return currentFrame;
        }
        return new Rect(anchorDip.X - nextW * fx, anchorDip.Y - nextH * fy, nextW, nextH);
    }
}

/// <summary>
/// 「拿在手上」置顶半透明无边框浮窗（对齐 Mac Held.swift）：
/// 图片仅解码一次并传入浮窗；当卡片从绳上被取下时同步关闭对应浮窗。
/// </summary>
public sealed class HeldWindowManager : IDisposable
{
    private sealed record HeldEntry(
        string FullPath,
        Note Note,
        HeldWindow? Window,
        Rect LastFrameDip,
        Line Line,
        Action<string, Rect, BitmapSource?>? OnHangBack);

    private readonly Dictionary<string, HeldEntry> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _heldPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Line> _observedLines = [];
    private bool _suppressSync;

    public bool HasOpenWindows => _open.Count > 0;
    public IReadOnlySet<string> HeldPaths => _heldPaths;

    public bool Hold(
        Note note,
        Line line,
        Func<IReadOnlyList<Note>, bool> onCopyNotes,
        Action<string, Rect, BitmapSource?>? onHangBack = null,
        bool showWindow = true)
    {
        var full = Path.GetFullPath(note.FilePath);
        if (_open.TryGetValue(full, out var existing))
        {
            existing.Window?.Activate();
            return true;
        }

        ObserveLine(line);

        var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
        var mon = ScreenPlacement.PickMonitor(cx, cy, ScreenPlacement.EnumerateMonitors());
        double dpi = mon.DpiScale > 0 ? mon.DpiScale : 1.0;
        var workDip = new Rect(
            mon.WorkArea.Left / dpi,
            mon.WorkArea.Top / dpi,
            mon.WorkArea.Width / dpi,
            mon.WorkArea.Height / dpi);

        var decodedImage = TryDecodeImageOnce(note);
        var natural = ComputeNaturalSize(decodedImage, workDip);
        double step = (_open.Count % 8) * 24.0;
        var startFrame = new Rect(
            workDip.Left + (workDip.Width - natural.Width) / 2.0 + step,
            workDip.Top + (workDip.Height - natural.Height) / 2.0 + step,
            natural.Width,
            natural.Height);

        HeldWindow? win = null;
        if (showWindow)
        {
            win = new HeldWindow(
                note,
                decodedImage,
                line.Theme,
                natural,
                startFrame,
                onRequestHangBack: () => HangBack(full),
                onRequestCopy: () => onCopyNotes([note]));
            win.Show();
        }

        _suppressSync = true;
        try
        {
            _heldPaths.Add(full);
            line.SetHeld(full, true);
            _open[full] = new HeldEntry(full, note, win, startFrame, line, onHangBack);
        }
        finally
        {
            _suppressSync = false;
        }
        return true;
    }

    public Rect? HangBack(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        if (!_open.TryGetValue(full, out var entry))
        {
            return null;
        }

        _suppressSync = true;
        try
        {
            _open.Remove(full);
            _heldPaths.Remove(full);

            Rect frame = entry.LastFrameDip;
            BitmapSource? snap = null;
            if (entry.Window is { } win)
            {
                frame = new Rect(win.Left, win.Top, Math.Max(80, win.ActualWidth), Math.Max(80, win.ActualHeight));
                if (win.Content is UIElement content)
                {
                    snap = FlightOverlay.CaptureElement(content, (int)Math.Ceiling(frame.Width), (int)Math.Ceiling(frame.Height));
                }
                win.CloseInternal();
            }

            entry.Line.SetHeld(full, false);
            entry.OnHangBack?.Invoke(full, frame, snap);
            return frame;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    public void HangAllBack()
    {
        foreach (var key in _open.Keys.ToList())
        {
            HangBack(key);
        }
    }

    public void Dispose()
    {
        _suppressSync = true;
        try
        {
            foreach (var line in _observedLines)
            {
                line.PropertyChanged -= OnLinePropertyChanged;
            }
            _observedLines.Clear();
            foreach (var (_, entry) in _open)
            {
                entry.Window?.CloseInternal();
                entry.Line.SetHeld(entry.FullPath, false);
            }
            _open.Clear();
            _heldPaths.Clear();
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private void ObserveLine(Line line)
    {
        if (_observedLines.Add(line))
        {
            line.PropertyChanged += OnLinePropertyChanged;
        }
    }

    private void OnLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSync || sender is not Line line) return;
        if (!string.Equals(e.PropertyName, nameof(Line.HeldPaths), StringComparison.Ordinal)) return;

        var stale = _open.Where(kv => ReferenceEquals(kv.Value.Line, line) && !line.HeldPaths.Contains(kv.Key))
                         .Select(kv => kv.Key)
                         .ToList();
        foreach (var key in stale)
        {
            if (_open.Remove(key, out var entry))
            {
                _heldPaths.Remove(key);
                entry.Window?.CloseInternal();
            }
        }
    }

    private static BitmapSource? TryDecodeImageOnce(Note note)
    {
        if (note.Kind != ItemKind.Image || !File.Exists(note.FilePath))
        {
            return null;
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(note.FilePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp.PixelWidth > 0 && bmp.PixelHeight > 0 ? bmp : null;
        }
        catch
        {
            // 损坏或无法解码的图片降级按文件摘要展示，不崩主线程
            return null;
        }
    }

    private static Size ComputeNaturalSize(BitmapSource? decodedImage, Rect workDip)
    {
        if (decodedImage is not null)
        {
            double maxW = workDip.Width * 0.65;
            double maxH = workDip.Height * 0.65;
            double fit = Math.Min(1.0, Math.Min(maxW / Math.Max(1.0, decodedImage.Width), maxH / Math.Max(1.0, decodedImage.Height)));
            return new Size(Math.Max(160.0, decodedImage.Width * fit + 16.0), Math.Max(120.0, decodedImage.Height * fit + 16.0));
        }
        return new Size(320.0, 230.0);
    }
}

internal sealed class HeldWindow : Window
{
    private readonly Size _naturalSize;
    private readonly bool _isTextNote;
    private readonly Action _onRequestHangBack;
    private readonly Action _onRequestCopy;
    private readonly Border _hangBackButton;
    private readonly TextBox? _textEditor;
    private double _noteScale = 1.0;
    private bool _closingInternal;

    public HeldWindow(
        Note note,
        BitmapSource? decodedImage,
        LineTheme theme,
        Size naturalSize,
        Rect startFrame,
        Action onRequestHangBack,
        Action onRequestCopy)
    {
        _naturalSize = naturalSize;
        _isTextNote = decodedImage is null;
        _onRequestHangBack = onRequestHangBack;
        _onRequestCopy = onRequestCopy;

        Title = $"{AppIdentity.AppName} — {Localization.L("Hold")}";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = _isTextNote ? 140 : 90;
        MinHeight = 90;
        Left = startFrame.Left;
        Top = startFrame.Top;
        Width = startFrame.Width;
        Height = startFrame.Height;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12)
        });

        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, note.Paper));
        var fill = ThemePalette.CardFill(theme, note.Paper);
        var glassFill = Color.FromArgb(0xF0, fill.R, fill.G, fill.B);

        var bodyHost = new Grid { Margin = new Thickness(8) };
        if (decodedImage is not null)
        {
            bodyHost.Children.Add(new Image
            {
                Source = decodedImage,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false
            });
        }
        else
        {
            // 顶部留出 28px Margin 给外框拖拽区，避免 TextBox 吃掉顶部标题带拖拽点击
            _textEditor = new TextBox
            {
                Text = note.FullText,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = ink,
                FontFamily = ThemePalette.CardFont(theme, code: note.IsCode),
                FontSize = 13.0,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 28, 0, 0),
                Padding = new Thickness(8, 2, 8, 8)
            };
            bodyHost.Children.Add(_textEditor);
        }

        _hangBackButton = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = ThemePalette.Brush(Color.FromArgb(0xDF, 0x28, 0x24, 0x20)),
            BorderBrush = ThemePalette.Brush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0.8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 10, 0, 0),
            Cursor = Cursors.Hand,
            Opacity = 0.0,
            ToolTip = Localization.L("Hang back"),
            Child = new TextBlock
            {
                Text = "↖",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        _hangBackButton.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _onRequestHangBack();
        };

        var frameBorder = new Border
        {
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(12),
            Background = ThemePalette.Brush(glassFill),
            BorderBrush = ThemePalette.Brush(ThemePalette.PickedColor(theme)),
            BorderThickness = new Thickness(1.4),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.28, Color = Colors.Black },
            Child = new Grid { Children = { bodyHost, _hangBackButton } }
        };

        var menu = new ContextMenu();
        var hangItem = new MenuItem { Header = Localization.L("Hang back") };
        hangItem.Click += (_, _) => _onRequestHangBack();
        var copyItem = new MenuItem { Header = Localization.L("Copy") };
        copyItem.Click += (_, _) => _onRequestCopy();
        var sizeItem = new MenuItem { Header = Localization.L("Actual Size") };
        sizeItem.Click += (_, _) => ResetActualSize();
        menu.Items.Add(hangItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(copyItem);
        menu.Items.Add(sizeItem);
        frameBorder.ContextMenu = menu;

        frameBorder.MouseEnter += (_, _) => _hangBackButton.Opacity = 1.0;
        frameBorder.MouseLeave += (_, _) => _hangBackButton.Opacity = 0.0;
        frameBorder.MouseLeftButtonDown += (_, e) =>
        {
            Activate();
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        Content = frameBorder;
        PreviewMouseWheel += HeldWindow_PreviewMouseWheel;
        PreviewKeyDown += HeldWindow_PreviewKeyDown;
        Closing += (_, e) =>
        {
            if (!_closingInternal)
            {
                e.Cancel = true;
                _onRequestHangBack();
            }
        };
    }

    public void CloseInternal()
    {
        _closingInternal = true;
        Close();
    }

    private void ResetActualSize()
    {
        _noteScale = 1.0;
        if (_textEditor is not null)
        {
            _textEditor.FontSize = 13.0;
        }
        double cx = Left + ActualWidth / 2.0;
        double cy = Top + ActualHeight / 2.0;
        Width = _naturalSize.Width;
        Height = _naturalSize.Height;
        Left = cx - _naturalSize.Width / 2.0;
        Top = cy - _naturalSize.Height / 2.0;
    }

    private void HeldWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isTextNote && (Keyboard.Modifiers & ModifierKeys.Control) == 0 && _textEditor is { ExtentHeight: > 0 } tb && tb.ExtentHeight > tb.ViewportHeight + 4)
        {
            return;
        }

        double factor = e.Delta > 0 ? 1.12 : (1.0 / 1.12);
        var rel = e.GetPosition(this);
        var anchor = new Point(Left + rel.X, Top + rel.Y);
        var cur = new Rect(Left, Top, ActualWidth, ActualHeight);
        var next = HeldGeometry.ZoomAroundAnchor(cur, _naturalSize, _isTextNote, ref _noteScale, factor, anchor);
        Left = next.Left;
        Top = next.Top;
        Width = next.Width;
        Height = next.Height;
        if (_textEditor is not null)
        {
            _textEditor.FontSize = Math.Clamp(13.0 * _noteScale, 10.5, 48.0);
        }
        e.Handled = true;
    }

    private void HeldWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _onRequestHangBack();
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            if (_textEditor is { SelectionLength: > 0 })
            {
                return;
            }
            e.Handled = true;
            _onRequestCopy();
        }
    }
}
