using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EnCopy;

/// <summary>
/// Windows 版内置快速查看窗：无边框、半透明毛玻璃外壳，自己吞下
/// Ctrl+Y / ← / → / Esc / Enter，关闭后由 LinePanel 把键盘焦点还给搜索框。
/// </summary>
public sealed class QuickLookWindow : Window
{
    private readonly QuickLookSession _session;
    private readonly LineTheme _theme;
    private readonly bool _dark;
    private TextBlock _kindText = null!;
    private Border _kindPill = null!;
    private TextBlock _titleText = null!;
    private TextBlock _counterText = null!;
    private TextBlock _statusText = null!;
    private ContentControl _contentHost = null!;

    private MediaElement? _media;
    private Slider? _positionSlider;
    private TextBlock? _timeText;
    private Button? _playButton;
    private DispatcherTimer? _videoTimer;
    private bool _videoPlaying;
    private bool _seeking;

    public QuickLookWindow(QuickLookSession session, LineTheme theme, DipRect workArea)
    {
        _session = session;
        _theme = theme;
        _dark = ThemePalette.IsDarkMode();

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Manual;

        Width = Math.Min(workArea.Width - 48, Math.Max(560, workArea.Width * 0.62));
        Height = Math.Min(workArea.Height - 64, Math.Max(420, workArea.Height * 0.68));
        if (Width < 320) Width = Math.Max(240, workArea.Width - 16);
        if (Height < 300) Height = Math.Max(220, workArea.Height - 16);
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;

        Content = BuildShell();
        PreviewKeyDown += QuickLookWindow_PreviewKeyDown;
        SourceInitialized += (_, _) => TryEnableBlurBehind();
        Closed += (_, _) => StopVideo();
        RenderCurrent();
    }

    private UIElement BuildShell()
    {
        var ink = ThemePalette.Brush(ThemePalette.ChromeText(_dark));
        var soft = ThemePalette.Brush(ThemePalette.ChromeSubText(_dark));

        _kindText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = _theme == LineTheme.Neon ? Brushes.White : ink
        };
        _kindPill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3, 9, 3),
            Child = _kindText,
            VerticalAlignment = VerticalAlignment.Center
        };

        _titleText = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0)
        };
        _counterText = new TextBlock
        {
            FontSize = 12,
            Foreground = soft,
            VerticalAlignment = VerticalAlignment.Center
        };

        var header = new Grid { Margin = new Thickness(18, 14, 18, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_kindPill, 0);
        Grid.SetColumn(_titleText, 1);
        Grid.SetColumn(_counterText, 2);
        header.Children.Add(_kindPill);
        header.Children.Add(_titleText);
        header.Children.Add(_counterText);

        _contentHost = new ContentControl
        {
            Margin = new Thickness(18, 0, 18, 10),
            MinHeight = 180
        };

        _statusText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = soft,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        var hintText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = soft,
            Text = Localization.L("Quick Look keyboard hint"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var footer = new Grid { Margin = new Thickness(18, 0, 18, 14), Height = 24 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_statusText, 0);
        Grid.SetColumn(hintText, 1);
        footer.Children.Add(_statusText);
        footer.Children.Add(hintText);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(_contentHost, 1);
        Grid.SetRow(footer, 2);
        layout.Children.Add(header);
        layout.Children.Add(_contentHost);
        layout.Children.Add(footer);

        var chrome = ThemePalette.ChromeFill(_dark);
        var fill = Color.FromArgb((byte)(_theme == LineTheme.Neon ? 0xE2 : 0xEA), chrome.R, chrome.G, chrome.B);
        var shell = new Border
        {
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(_theme == LineTheme.Pixel ? 0 : 16),
            BorderThickness = new Thickness(_theme == LineTheme.Pixel ? 2 : 1),
            BorderBrush = ThemePalette.Brush(ThemePalette.ChromeBorder(_dark)),
            Background = ThemePalette.Brush(fill),
            Child = layout,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 5,
                Opacity = 0.30,
                Color = Colors.Black
            }
        };

        var root = new Grid();
        root.Children.Add(shell);
        return root;
    }

    public void RenderCurrent()
    {
        StopVideo();
        var note = _session.Current;
        var content = QuickLookContentFactory.For(note);

        _kindText.Text = KindTitle(note.Kind);
        var tag = ThemePalette.TagColor(note.Kind);
        _kindPill.Background = ThemePalette.Brush(Color.FromArgb(0x38, tag.R, tag.G, tag.B));
        _titleText.Text = content.Title;
        _counterText.Text = Localization.Format("{0} of {1}", _session.Index + 1, _session.Items.Count);
        _statusText.Text = note.IsMovedAway ? Localization.L("Moved away") : string.Empty;

        _contentHost.Content = content.Kind switch
        {
            QuickLookContentKind.Image => BuildImage(content),
            QuickLookContentKind.Video => BuildVideo(content),
            QuickLookContentKind.Files => BuildFiles(content),
            QuickLookContentKind.Missing => BuildMissing(),
            _ => BuildText(note, content)
        };
    }

    private string KindTitle(ItemKind kind) => kind switch
    {
        ItemKind.Code => Localization.L("Code"),
        ItemKind.Link => Localization.L("Link"),
        ItemKind.Image => Localization.L("Image"),
        ItemKind.Video => Localization.L("Video"),
        ItemKind.File => Localization.L("File"),
        _ => Localization.L("Text")
    };

    private UIElement BuildImage(QuickLookContent content)
    {
        if (content.MediaPath is null) return BuildMissing();
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(content.MediaPath);
            bitmap.EndInit();

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return new Border
            {
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Background = ThemePalette.Brush(_dark ? Color.FromRgb(0x12, 0x13, 0x16) : Color.FromRgb(0xEA, 0xE7, 0xDF)),
                Child = image
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or UriFormatException)
        {
            _statusText.Text = Localization.L("The file could not be opened");
            return BuildMissing();
        }
    }

    private UIElement BuildText(Note note, QuickLookContent content)
    {
        var box = new TextBox
        {
            Text = content.Text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = ThemePalette.Brush(ThemePalette.ChromeText(_dark)),
            FontFamily = ThemePalette.CardFont(_theme, note.IsCode),
            FontSize = 14,
            Padding = new Thickness(16),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CaretBrush = ThemePalette.Brush(ThemePalette.ChromeText(_dark))
        };
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Background = ThemePalette.Brush(ThemePalette.ChipIdleFill(_dark)),
            Child = box
        };
    }

    private UIElement BuildFiles(QuickLookContent content)
    {
        var ink = ThemePalette.Brush(ThemePalette.ChromeText(_dark));
        var soft = ThemePalette.Brush(ThemePalette.ChromeSubText(_dark));
        var stack = new StackPanel { Margin = new Thickness(10, 10, 10, 10) };
        foreach (var file in content.Files)
        {
            string state = !file.Exists
                ? Localization.L("Moved away")
                : file.IsDirectory
                    ? Localization.L("Folder")
                    : Retention.FormatBytes(file.SizeBytes ?? 0);

            var name = new TextBlock
            {
                Text = file.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = ink,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var detail = new TextBlock
            {
                Text = state,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = file.Exists ? soft : ThemePalette.Brush(ThemePalette.NoticeFg(_dark)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 0, 0)
            };
            var firstLine = new Grid();
            firstLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            firstLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(name, 0);
            Grid.SetColumn(detail, 1);
            firstLine.Children.Add(name);
            firstLine.Children.Add(detail);

            var path = new TextBlock
            {
                Text = file.Path,
                FontSize = 11.5,
                Foreground = soft,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var body = new StackPanel();
            body.Children.Add(firstLine);
            body.Children.Add(path);

            stack.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(8),
                Background = ThemePalette.Brush(ThemePalette.ChipIdleFill(_dark)),
                Child = body
            });
        }

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack
        };
    }

    private UIElement BuildVideo(QuickLookContent content)
    {
        if (content.MediaPath is null) return BuildMissing();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            ScrubbingEnabled = true,
            Source = new Uri(content.MediaPath)
        };
        _media.MediaOpened += Media_MediaOpened;
        _media.MediaEnded += Media_MediaEnded;
        _media.MediaFailed += Media_MediaFailed;

        var videoHost = new Border
        {
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Background = Brushes.Black,
            Child = _media
        };
        Grid.SetRow(videoHost, 0);
        grid.Children.Add(videoHost);

        _playButton = new Button
        {
            Width = 34,
            Height = 28,
            Margin = new Thickness(0, 8, 10, 0),
            Content = "❚❚",
            FontSize = 12,
            IsEnabled = true
        };
        _playButton.Click += (_, _) => ToggleVideoPlayback();

        _positionSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Height = 28,
            Margin = new Thickness(0, 8, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true
        };
        _positionSlider.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _positionSlider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _seeking = false;
            SeekVideo(_positionSlider.Value);
        };
        _positionSlider.ValueChanged += (_, _) =>
        {
            if (_seeking) SeekVideo(_positionSlider.Value);
        };

        _timeText = new TextBlock
        {
            Width = 82,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 11.5,
            Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(_dark)),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Text = "0:00"
        };

        var controls = new Grid();
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_playButton, 0);
        Grid.SetColumn(_positionSlider, 1);
        Grid.SetColumn(_timeText, 2);
        controls.Children.Add(_playButton);
        controls.Children.Add(_positionSlider);
        controls.Children.Add(_timeText);
        Grid.SetRow(controls, 1);
        grid.Children.Add(controls);

        _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _videoTimer.Tick += (_, _) => UpdateVideoPosition();
        _videoTimer.Start();
        _videoPlaying = true;
        _media.Play();
        return grid;
    }

    private UIElement BuildMissing()
    {
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ThemePalette.Brush(ThemePalette.ChipIdleFill(_dark)),
            Child = new TextBlock
            {
                Text = Localization.L("This item is missing"),
                FontSize = 15,
                Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(_dark)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(24)
            }
        };
    }

    private void QuickLookWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_session.HandleKey(e.Key, Keyboard.Modifiers, out bool closeRequested)) return;
        e.Handled = true;
        if (closeRequested)
        {
            Close();
        }
        else
        {
            RenderCurrent();
        }
    }

    private void Media_MediaOpened(object? sender, RoutedEventArgs e)
    {
        if (_media is null || _positionSlider is null || _playButton is null) return;
        if (_media.NaturalDuration.HasTimeSpan)
        {
            _positionSlider.Maximum = Math.Max(1, _media.NaturalDuration.TimeSpan.TotalSeconds);
        }
        _playButton.IsEnabled = true;
        if (_videoPlaying)
        {
            _media.Play();
        }
        _playButton.Content = _videoPlaying ? "❚❚" : "▶";
        UpdateVideoPosition();
    }

    private void Media_MediaEnded(object? sender, RoutedEventArgs e)
    {
        _videoPlaying = false;
        if (_playButton is not null) _playButton.Content = "▶";
        _media?.Stop();
    }

    private void Media_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _videoPlaying = false;
        if (_playButton is not null) _playButton.IsEnabled = false;
        _statusText.Text = Localization.L("The file could not be opened");
    }

    private void ToggleVideoPlayback()
    {
        if (_media is null || _playButton is null) return;
        if (_videoPlaying)
        {
            _media.Pause();
            _videoPlaying = false;
            _playButton.Content = "▶";
        }
        else
        {
            _media.Play();
            _videoPlaying = true;
            _playButton.Content = "❚❚";
        }
    }

    private void SeekVideo(double seconds)
    {
        if (_media is null || !_media.NaturalDuration.HasTimeSpan) return;
        _media.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _media.NaturalDuration.TimeSpan.TotalSeconds));
        UpdateVideoPosition();
    }

    private void UpdateVideoPosition()
    {
        if (_media is null || _positionSlider is null || _timeText is null) return;
        var position = _media.Position;
        if (!_seeking && _media.NaturalDuration.HasTimeSpan)
        {
            _positionSlider.Value = Math.Min(_positionSlider.Maximum, position.TotalSeconds);
        }
        var duration = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        _timeText.Text = $"{FormatVideoTime(position)} / {FormatVideoTime(duration)}";
    }

    private static string FormatVideoTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private void StopVideo()
    {
        _videoTimer?.Stop();
        _videoTimer = null;
        if (_media is not null)
        {
            _media.Stop();
            _media.Close();
            _media.Source = null;
        }
        _media = null;
        _positionSlider = null;
        _timeText = null;
        _playButton = null;
        _videoPlaying = false;
        _seeking = false;
    }

    private void TryEnableBlurBehind()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var blur = new DwmBlurBehind
        {
            Flags = DwmBlurBehind.Enable,
            Enabled = true,
            BlurRegion = IntPtr.Zero,
            TransitionOnMaximized = false
        };
        _ = DwmEnableBlurBehindWindow(hwnd, ref blur);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public const int Enable = 0x00000001;
        public int Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enabled;
        public IntPtr BlurRegion;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);
}
