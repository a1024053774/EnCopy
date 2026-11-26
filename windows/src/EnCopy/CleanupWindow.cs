using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace EnCopy;

/// <summary>
/// 「清理历史」（Clean Up History，对齐 CleanupPanel.swift，T-061）：
/// 支持按类型、上次使用时间、来源应用、包含文字（FTS5）与是否包含绳上/收藏项筛查；
/// 清理前展示候选数量、样例标题与预计腾出空间，二次确认后移入「最近清掉」保留 7 天并支持整批放回。
/// </summary>
public sealed class CleanupWindow : Window
{
    private readonly Line _line;
    private readonly DispatcherTimer _debounceTimer;

    private ComboBox _kindCombo = null!;
    private ComboBox _ageCombo = null!;
    private ComboBox _appCombo = null!;
    private TextBox _queryBox = null!;
    private CheckBox _includeOnLineCheck = null!;
    private TextBlock _summaryText = null!;
    private StackPanel _samplesHost = null!;
    private StackPanel _actionsHost = null!;
    private StackPanel _batchesHost = null!;
    private Border _rootBackdrop = null!;

    private CleanupPreview _currentPreview = new([], 0);
    private bool _confirming;
    private bool _suppressFilterEvents;

    private static (double? Days, string Label)[] GetAgeChoices() =>
    [
        (null, Localization.L("Any time")),
        (1.0, Localization.L("Over a day ago")),
        (7.0, Localization.L("Over a week ago")),
        (30.0, Localization.L("Over a month ago")),
        (90.0, Localization.L("Over three months ago")),
        (365.0, Localization.L("Over a year ago"))
    ];

    private static (KindFilter Kind, string Label)[] GetKindChoices() =>
    [
        (KindFilter.All, Localization.L("All")),
        (KindFilter.Text, Localization.L("Text")),
        (KindFilter.Link, Localization.L("Links")),
        (KindFilter.Image, Localization.L("Images")),
        (KindFilter.File, Localization.L("Files"))
    ];

    public CleanupWindow(Line line)
    {
        _line = line;
        Title = Localization.L("Clean Up History");
        Width = 530;
        Height = 650;
        MinWidth = 480;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            RefreshPreviewAndBatches();
        };

        BuildUi();
        _line.PropertyChanged += OnLinePropertyChanged;
        Closed += (_, _) =>
        {
            _debounceTimer.Stop();
            _line.PropertyChanged -= OnLinePropertyChanged;
        };
        RefreshAppChoices();
        RefreshPreviewAndBatches();
    }

    public CleanupPreview CurrentPreviewForTest => _currentPreview;

    public void SetFilterForTest(CleanupFilter filter)
    {
        _suppressFilterEvents = true;
        try
        {
            var kinds = GetKindChoices();
            int kIdx = Array.FindIndex(kinds, c => c.Kind == filter.Kind);
            _kindCombo.SelectedIndex = kIdx >= 0 ? kIdx : 0;

            var ages = GetAgeChoices();
            int aIdx = Array.FindIndex(ages, c => c.Days == filter.OlderThanDays);
            _ageCombo.SelectedIndex = aIdx >= 0 ? aIdx : 0;

            _queryBox.Text = filter.QueryText ?? string.Empty;
            _includeOnLineCheck.IsChecked = filter.IncludePinnedAndOnLine;

            RefreshAppChoices(filter.App);
        }
        finally
        {
            _suppressFilterEvents = false;
        }
        RefreshPreviewAndBatches();
    }

    public ClearedBatch? ExecuteCurrentCleanup()
    {
        _debounceTimer.Stop();
        var latest = _line.PreviewCleanup(ReadCurrentFilter());
        var batch = _line.CleanUp(latest.CandidatePaths);
        _confirming = false;
        RefreshAppChoices((_appCombo.SelectedItem as ComboBoxItem)?.Tag as string);
        RefreshPreviewAndBatches();
        return batch;
    }

    public void RenderOffscreen(double width = 530, double height = 650)
    {
        Width = width;
        Height = height;
        _rootBackdrop.Measure(new Size(width, height));
        _rootBackdrop.Arrange(new Rect(0, 0, width, height));
        _rootBackdrop.UpdateLayout();
    }

    private void OnLinePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(Line.Theme), StringComparison.Ordinal))
        {
            BuildUi();
            RefreshAppChoices();
            RefreshPreviewAndBatches();
        }
    }

    private void BuildUi()
    {
        var theme = _line.Theme;
        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, PaperKind.Receipt));
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, PaperKind.Receipt));
        var font = ThemePalette.CardFont(theme, code: false);

        var outerStack = new StackPanel { Margin = new Thickness(18, 16, 18, 18) };

        // 区块 1：筛选条件与预览
        var filterBody = new StackPanel();
        _kindCombo = new ComboBox { Width = 180 };
        foreach (var (_, label) in GetKindChoices())
        {
            _kindCombo.Items.Add(new ComboBoxItem { Content = label });
        }
        _kindCombo.SelectedIndex = 0;
        _kindCombo.SelectionChanged += (_, _) => OnFilterChanged(immediate: true);
        filterBody.Children.Add(BuildRow(Localization.L("Kind"), _kindCombo, ink, soft, font));

        _ageCombo = new ComboBox { Width = 180 };
        foreach (var (_, label) in GetAgeChoices())
        {
            _ageCombo.Items.Add(new ComboBoxItem { Content = label });
        }
        _ageCombo.SelectedIndex = 3; // 默认一个月前（对齐 CleanupModel: olderThanDays: 30）
        _ageCombo.SelectionChanged += (_, _) => OnFilterChanged(immediate: true);
        filterBody.Children.Add(BuildRow(Localization.L("Last used"), _ageCombo, ink, soft, font));

        _appCombo = new ComboBox { Width = 180 };
        _appCombo.SelectionChanged += (_, _) => OnFilterChanged(immediate: true);
        filterBody.Children.Add(BuildRow(Localization.L("Copied from"), _appCombo, ink, soft, font));

        _queryBox = new TextBox { Width = 180, Padding = new Thickness(6, 3, 6, 3) };
        _queryBox.TextChanged += (_, _) => OnFilterChanged(immediate: false);
        filterBody.Children.Add(BuildRow(Localization.L("Containing"), _queryBox, ink, soft, font));

        _includeOnLineCheck = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        _includeOnLineCheck.Checked += (_, _) => OnFilterChanged(immediate: true);
        _includeOnLineCheck.Unchecked += (_, _) => OnFilterChanged(immediate: true);
        filterBody.Children.Add(BuildRow(Localization.L("Include items on the line and pinned"), _includeOnLineCheck, ink, soft, font));

        _summaryText = new TextBlock
        {
            FontSize = 11.5,
            FontFamily = font,
            Foreground = soft,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 10, 16, 6)
        };
        filterBody.Children.Add(_summaryText);

        _samplesHost = new StackPanel { Margin = new Thickness(16, 0, 16, 8) };
        filterBody.Children.Add(_samplesHost);

        _actionsHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 4, 16, 14)
        };
        filterBody.Children.Add(_actionsHost);

        outerStack.Children.Add(WrapThemedSheet(Localization.L("Clean Up History"), filterBody, theme, ink, font));

        // 区块 2：最近清掉（7 天批次可放回）
        _batchesHost = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var batchesSheet = WrapThemedSheet(Localization.L("Recently Cleared"), _batchesHost, theme, ink, font);
        batchesSheet.Margin = new Thickness(0, 14, 0, 0);
        outerStack.Children.Add(batchesSheet);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = outerStack
        };

        _rootBackdrop = new Border
        {
            Background = ThemePalette.Brush(ThemePalette.SettingsBackdropColor(theme)),
            Child = scroller
        };
        Content = _rootBackdrop;
    }

    private void RefreshAppChoices(string? preferredApp = null)
    {
        var currentTag = preferredApp ?? (_appCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        _suppressFilterEvents = true;
        try
        {
            _appCombo.Items.Clear();
            _appCombo.Items.Add(new ComboBoxItem { Content = Localization.L("Any app"), Tag = null });

            var seenByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _line.History.AllEntriesWithMeta())
            {
                if (string.IsNullOrWhiteSpace(entry.AppName)) continue;
                var key = SourceApp.NormalizeRuleKey(entry.AppName);
                if (!seenByKey.ContainsKey(key))
                {
                    seenByKey[key] = entry.AppName;
                }
            }

            int selectedIndex = 0;
            int idx = 1;
            foreach (var appRaw in seenByKey.Values.OrderBy(a => SourceApp.GetDisplayName(a) ?? a, StringComparer.CurrentCultureIgnoreCase))
            {
                _appCombo.Items.Add(new ComboBoxItem
                {
                    Content = SourceApp.GetDisplayName(appRaw) ?? appRaw,
                    Tag = appRaw
                });
                if (!string.IsNullOrWhiteSpace(currentTag) &&
                    string.Equals(SourceApp.NormalizeRuleKey(appRaw), SourceApp.NormalizeRuleKey(currentTag), StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = idx;
                }
                idx++;
            }
            _appCombo.SelectedIndex = selectedIndex;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    private void OnFilterChanged(bool immediate)
    {
        if (_suppressFilterEvents) return;
        _confirming = false;
        _debounceTimer.Stop();
        if (immediate)
        {
            RefreshPreviewAndBatches();
        }
        else
        {
            _debounceTimer.Start();
        }
    }

    private CleanupFilter ReadCurrentFilter()
    {
        var kinds = GetKindChoices();
        var kind = _kindCombo.SelectedIndex >= 0 && _kindCombo.SelectedIndex < kinds.Length
            ? kinds[_kindCombo.SelectedIndex].Kind
            : KindFilter.All;
        var ages = GetAgeChoices();
        var days = _ageCombo.SelectedIndex >= 0 && _ageCombo.SelectedIndex < ages.Length
            ? ages[_ageCombo.SelectedIndex].Days
            : null;
        var app = (_appCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var text = _queryBox.Text ?? string.Empty;
        bool includeOnLine = _includeOnLineCheck.IsChecked == true;
        return new CleanupFilter(kind, days, app, text, includeOnLine);
    }

    public void RefreshPreviewAndBatches()
    {
        var theme = _line.Theme;
        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, PaperKind.Receipt));
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, PaperKind.Receipt));
        var font = ThemePalette.CardFont(theme, code: false);

        _currentPreview = _line.PreviewCleanup(ReadCurrentFilter());
        _summaryText.Text = Localization.Format("{0} items ({1}) will go to Recently Cleared, where they stay 7 days.",
            _currentPreview.CandidatePaths.Count,
            Retention.FormatBytes(_currentPreview.EstimatedFreedBytes));

        _samplesHost.Children.Clear();
        foreach (var path in _currentPreview.CandidatePaths.Take(6))
        {
            var rel = _line.History.StoredKeyFor(path) ?? path;
            var note = Note.Load(path, rel, DateTime.Now);
            var label = note is not null && !string.IsNullOrWhiteSpace(note.SummaryLine)
                ? note.SummaryLine
                : Path.GetFileName(path);
            _samplesHost.Children.Add(new TextBlock
            {
                Text = "• " + label,
                FontSize = 11.5,
                FontFamily = font,
                Foreground = ink,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1.5, 0, 1.5)
            });
        }

        _actionsHost.Children.Clear();
        if (_confirming && _currentPreview.CandidatePaths.Count > 0)
        {
            var cancelBtn = new Button
            {
                Content = Localization.L("Cancel"),
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (_, _) =>
            {
                _confirming = false;
                RefreshPreviewAndBatches();
            };

            var confirmBtn = new Button
            {
                Content = Localization.Format("Confirm Clear {0}", _currentPreview.CandidatePaths.Count),
                Padding = new Thickness(12, 5, 12, 5),
                FontWeight = FontWeights.SemiBold
            };
            confirmBtn.Click += (_, _) => ExecuteCurrentCleanup();

            _actionsHost.Children.Add(cancelBtn);
            _actionsHost.Children.Add(confirmBtn);
        }
        else
        {
            var clearBtn = new Button
            {
                Content = Localization.Format("Clear {0}…", _currentPreview.CandidatePaths.Count),
                Padding = new Thickness(14, 5, 14, 5),
                IsEnabled = _currentPreview.CandidatePaths.Count > 0
            };
            clearBtn.Click += (_, _) =>
            {
                _confirming = true;
                RefreshPreviewAndBatches();
            };
            _actionsHost.Children.Add(clearBtn);
        }

        _batchesHost.Children.Clear();
        var batches = _line.Clearing.Batches();
        if (batches.Count == 0)
        {
            _batchesHost.Children.Add(new TextBlock
            {
                Text = Localization.L("Nothing cleared in the last 7 days."),
                FontSize = 11.5,
                FontFamily = font,
                Foreground = soft,
                Margin = new Thickness(16, 8, 16, 8)
            });
            return;
        }

        foreach (var batch in batches)
        {
            var rowGrid = new Grid { Margin = new Thickness(16, 7, 16, 7) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = batch.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = font,
                Foreground = ink
            });
            left.Children.Add(new TextBlock
            {
                Text = string.Equals(batch.Reason, "expired", StringComparison.OrdinalIgnoreCase)
                    ? Localization.Format("{0} expired", batch.Entries.Count)
                    : Localization.Format("{0} cleared by you", batch.Entries.Count),
                FontSize = 11,
                FontFamily = font,
                Foreground = soft
            });
            Grid.SetColumn(left, 0);
            rowGrid.Children.Add(left);

            var putBackBtn = new Button
            {
                Content = Localization.L("Put Back"),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            var capturedId = batch.Id;
            putBackBtn.Click += (_, _) =>
            {
                _line.PutBackClearedBatch(capturedId);
                RefreshAppChoices((_appCombo.SelectedItem as ComboBoxItem)?.Tag as string);
                RefreshPreviewAndBatches();
            };
            Grid.SetColumn(putBackBtn, 1);
            rowGrid.Children.Add(putBackBtn);

            _batchesHost.Children.Add(rowGrid);
        }
    }

    private static FrameworkElement BuildRow(string label, UIElement control, Brush ink, Brush soft, FontFamily font)
    {
        var wrap = new StackPanel();
        var grid = new Grid { Margin = new Thickness(16, 8, 16, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var txt = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = font,
            Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0)
        };
        Grid.SetColumn(txt, 0);
        grid.Children.Add(txt);

        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        wrap.Children.Add(grid);

        wrap.Children.Add(new Border
        {
            Height = 0.8,
            Background = soft,
            Opacity = 0.22,
            Margin = new Thickness(16, 0, 16, 0)
        });
        return wrap;
    }

    internal static Border WrapThemedSheet(string? title, UIElement content, LineTheme theme, Brush ink, FontFamily font)
    {
        var edge = ThemePalette.CardEdge(theme, PaperKind.Receipt);
        var stack = new StackPanel();
        if (!string.IsNullOrEmpty(title))
        {
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                FontFamily = font,
                Foreground = ink,
                Margin = new Thickness(16, 13, 16, 4)
            });
        }
        stack.Children.Add(content);

        return new Border
        {
            CornerRadius = new CornerRadius(Math.Max(2.0, ThemePalette.CardCorner(theme))),
            Background = ThemePalette.Brush(ThemePalette.CardFill(theme, PaperKind.Receipt)),
            BorderBrush = edge is { } e ? ThemePalette.Brush(e.Color) : ThemePalette.Brush(Color.FromArgb(0x28, 0x3D, 0x32, 0x24)),
            BorderThickness = new Thickness(edge?.Width ?? 0.8),
            Effect = new DropShadowEffect
            {
                BlurRadius = theme == LineTheme.Pixel ? 0 : 8,
                ShadowDepth = theme == LineTheme.Pixel ? 3 : 2,
                Direction = 315,
                Opacity = theme == LineTheme.Pixel ? 0.75 : 0.16,
                Color = Colors.Black
            },
            Child = stack
        };
    }
}
