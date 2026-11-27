using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace EnCopy;

public sealed record SettingsCallbacks(
    Action SaveAndApplySettings,
    Action<Action> ChangeShortcut,
    Action OpenCleanup,
    Action OpenScreenshotsFolder,
    Action OpenDataFolder);

/// <summary>
/// 跟主题走的五页纸签设置窗口（T-051 / T-061）：
/// 页签包括「通用」「挂上复制的内容」「截图」「晾衣绳」「保留」，
/// 底板、纸张与选中色随六套主题实时变换，持久化到 Settings.json。
/// </summary>
public sealed class SettingsWindow : Window
{
    public const string CurrentVersion = "0.4.4";

    private readonly AppSettings _settings;
    private readonly Line _line;
    private readonly SettingsCallbacks _callbacks;
    private readonly OcrLanguageStatus _ocrStatus;

    private string? _frontApp;
    private int _activeTab;
    private bool _showsStorageDetails;
    private Border _rootBackdrop = null!;

    public SettingsWindow(
        AppSettings settings,
        Line line,
        SettingsCallbacks callbacks,
        string? frontApp = null,
        OcrLanguageStatus? ocrStatus = null)
    {
        _settings = settings;
        _line = line;
        _callbacks = callbacks;
        _frontApp = frontApp;
        _ocrStatus = ocrStatus ?? OcrLanguageResolver.DetectSystemStatus();

        Title = Localization.L("Settings");
        Width = 610;
        Height = 640;
        MinWidth = 560;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Rebuild();
    }

    public int ActiveTab => _activeTab;

    public void SelectTab(int tabIndex)
    {
        _activeTab = Math.Clamp(tabIndex, 0, 4);
        Rebuild();
    }

    public void RefreshWithFrontApp(string? frontApp)
    {
        _frontApp = frontApp;
        Rebuild();
    }

    public void RenderOffscreen(double width = 610, double height = 640)
    {
        Width = width;
        Height = height;
        _rootBackdrop.Measure(new Size(width, height));
        _rootBackdrop.Arrange(new Rect(0, 0, width, height));
        _rootBackdrop.UpdateLayout();
    }

    public void Rebuild()
    {
        var theme = _settings.GetActiveTheme();
        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, PaperKind.Receipt));
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, PaperKind.Receipt));
        var font = ThemePalette.CardFont(theme, code: false);

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 顶部五枚纸签页签
        var tabsPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(18, 14, 18, 10)
        };
        (string Title, string Glyph)[] tabs =
        [
            (Localization.L("General"), "⚙"),
            (Localization.L("Hang what you copy"), "📋"),
            (Localization.L("Screenshots"), "📷"),
            (Localization.L("The line"), "〰"),
            (Localization.L("Keeping"), "🗄")
        ];
        for (int i = 0; i < tabs.Length; i++)
        {
            int captured = i;
            tabsPanel.Children.Add(BuildPaperTabTag(tabs[i].Title, tabs[i].Glyph, selected: _activeTab == i, theme, () => SelectTab(captured)));
        }
        Grid.SetRow(tabsPanel, 0);
        mainGrid.Children.Add(tabsPanel);

        var pageStack = new StackPanel { Margin = new Thickness(18, 4, 18, 20) };
        switch (_activeTab)
        {
            case 0:
                BuildGeneralTab(pageStack, theme, ink, soft, font);
                break;
            case 1:
                BuildRecordingTab(pageStack, theme, ink, soft, font);
                break;
            case 2:
                BuildScreenshotsTab(pageStack, theme, ink, soft, font);
                break;
            case 3:
                BuildLineTab(pageStack, theme, ink, soft, font);
                break;
            default:
                BuildKeepingTab(pageStack, theme, ink, soft, font);
                break;
        }

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = pageStack
        };
        Grid.SetRow(scroller, 1);
        mainGrid.Children.Add(scroller);

        _rootBackdrop = new Border
        {
            Background = ThemePalette.Brush(ThemePalette.SettingsBackdropColor(theme)),
            Child = mainGrid
        };
        Content = _rootBackdrop;
    }

    private void BuildGeneralTab(StackPanel host, LineTheme theme, Brush ink, Brush soft, FontFamily font)
    {
        var generalRows = new StackPanel();
        generalRows.Children.Add(BuildSettingRow(
            Localization.L("Open at login"),
            null,
            BuildToggle(LaunchAtLogin.IsEnabled(), on =>
            {
                LaunchAtLogin.SetEnabled(on);
                Rebuild();
            }),
            ink, soft, font));

        generalRows.Children.Add(BuildSettingRow(
            Localization.L("Sounds"),
            null,
            BuildToggle(_settings.SoundsEnabled, on =>
            {
                _settings.SoundsEnabled = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font));

        var shortcutBtn = new Button
        {
            Content = Localization.L("Change Shortcut…"),
            Padding = new Thickness(10, 4, 10, 4)
        };
        shortcutBtn.Click += (_, _) => _callbacks.ChangeShortcut(Rebuild);
        var shortcutDisplay = _settings.GetActiveShortcut()?.Display ?? Localization.L("None");
        generalRows.Children.Add(BuildSettingRow(
            Localization.L("Shortcut"),
            shortcutDisplay,
            shortcutBtn,
            ink, soft, font,
            isLast: true));

        host.Children.Add(CleanupWindow.WrapThemedSheet(null, generalRows, theme, ink, font));

        var updateRows = new StackPanel();
        var versionDetail = Localization.Format("This is version {0}", CurrentVersion);
        if (AppIdentity.AppName.EndsWith("Dev", StringComparison.Ordinal))
        {
            versionDetail += " · " + Localization.L("The development copy doesn't check for updates");
        }
        updateRows.Children.Add(BuildSettingRow(
            Localization.L("Updates"),
            versionDetail,
            null,
            ink, soft, font,
            isLast: true));
        var updateSheet = CleanupWindow.WrapThemedSheet(null, updateRows, theme, ink, font);
        updateSheet.Margin = new Thickness(0, 14, 0, 0);
        host.Children.Add(updateSheet);
    }

    private void BuildRecordingTab(StackPanel host, LineTheme theme, Brush ink, Brush soft, FontFamily font)
    {
        var typesRows = new StackPanel();
        typesRows.Children.Add(BuildSettingRow(
            Localization.L("Images"),
            Localization.L("Images you copy hang on the line too"),
            BuildToggle(_settings.ImagesEnabled, on =>
            {
                _settings.ImagesEnabled = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font));

        if (_ocrStatus.IsChinesePackMissing)
        {
            typesRows.Children.Add(BuildSettingRow(
                Localization.L("Chinese text recognition pack is not installed"),
                Localization.L("Install Simplified Chinese (Optical character recognition) in Windows Settings → Time & language → Language & region so Chinese words in images can be found too."),
                null,
                ink, soft, font));
        }

        typesRows.Children.Add(BuildSettingRow(
            Localization.L("Text"),
            Localization.L("Text you copy hangs on the line too"),
            BuildToggle(_settings.TextEnabled, on =>
            {
                _settings.TextEnabled = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font));

        typesRows.Children.Add(BuildSettingRow(
            Localization.L("Links"),
            Localization.L("Links you copy hang on the line too"),
            BuildToggle(_settings.LinksEnabled, on =>
            {
                _settings.LinksEnabled = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font));

        typesRows.Children.Add(BuildSettingRow(
            Localization.L("Files"),
            Localization.L("Files you copy in Explorer hang on the line, where they are"),
            BuildToggle(_settings.FilesEnabled, on =>
            {
                _settings.FilesEnabled = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font));

        typesRows.Children.Add(BuildSettingRow(
            Localization.L("Show the app it came from"),
            null,
            BuildToggle(_settings.ShowsSourceApp, on =>
            {
                _settings.ShowsSourceApp = on;
                _line.ShowsSourceApp = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font,
            isLast: true));

        host.Children.Add(CleanupWindow.WrapThemedSheet(null, typesRows, theme, ink, font));

        var appRows = new StackPanel();
        var historyApps = _line.History.AllEntriesWithMeta()
            .Select(e => e.AppName)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Cast<string>();
        var listedApps = SourceApp.ListRuleApps(_settings, historyApps, _frontApp);
        var frontKey = _frontApp is not null ? SourceApp.NormalizeRuleKey(_frontApp) : null;

        for (int i = 0; i < listedApps.Count; i++)
        {
            var appId = listedApps[i];
            var displayName = SourceApp.GetDisplayName(appId) ?? appId;
            bool isFront = frontKey is not null && string.Equals(SourceApp.NormalizeRuleKey(appId), frontKey, StringComparison.OrdinalIgnoreCase);

            var ruleCombo = new ComboBox { Width = 148 };
            (AppRule Rule, string Label)[] choices =
            [
                (AppRule.Normal, Localization.L("Record normally")),
                (AppRule.Hourly, Localization.L("Clear after an hour")),
                (AppRule.Skip, Localization.L("Don't record"))
            ];
            var currentRule = SourceApp.GetRule(_settings, appId);
            int sel = 0;
            for (int c = 0; c < choices.Length; c++)
            {
                ruleCombo.Items.Add(new ComboBoxItem { Content = choices[c].Label, Tag = choices[c].Rule });
                if (choices[c].Rule == currentRule) sel = c;
            }
            ruleCombo.SelectedIndex = sel;
            var capturedApp = appId;
            ruleCombo.SelectionChanged += (_, _) =>
            {
                if (ruleCombo.SelectedItem is ComboBoxItem { Tag: AppRule chosen })
                {
                    SourceApp.SetRule(_settings, capturedApp, chosen);
                    _callbacks.SaveAndApplySettings();
                }
            };

            var icon = SourceApp.GetAppIcon(appId);
            appRows.Children.Add(BuildSettingRow(
                displayName,
                isFront ? Localization.L("The app you were using before Settings") : null,
                ruleCombo,
                ink, soft, font,
                isLast: i == listedApps.Count - 1,
                leadingIcon: icon));
        }

        var byAppSheet = CleanupWindow.WrapThemedSheet(Localization.L("By app"), appRows, theme, ink, font);
        byAppSheet.Margin = new Thickness(0, 14, 0, 0);
        host.Children.Add(byAppSheet);
    }

    private void BuildScreenshotsTab(StackPanel host, LineTheme theme, Brush ink, Brush soft, FontFamily font)
    {
        var rows = new StackPanel();
        rows.Children.Add(BuildSettingRow(
            Localization.L("Handle screenshots"),
            Localization.L("Screenshots copy into EnCopy's folder while keeping the original"),
            BuildToggle(_settings.ScreenshotsEnabled, on =>
            {
                _settings.ScreenshotsEnabled = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font));

        rows.Children.Add(BuildSettingRow(
            Localization.L("Copy each screenshot"),
            Localization.L("Each new screenshot goes to the clipboard as it hangs"),
            BuildToggle(_settings.CopyEachScreenshot, on =>
            {
                _settings.CopyEachScreenshot = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font));

        var openFolderBtn = new Button
        {
            Content = Localization.L("Open screenshots folder"),
            Padding = new Thickness(12, 5, 12, 5)
        };
        openFolderBtn.Click += (_, _) => _callbacks.OpenScreenshotsFolder();
        rows.Children.Add(BuildSettingRow(null, null, openFolderBtn, ink, soft, font, isLast: true));

        host.Children.Add(CleanupWindow.WrapThemedSheet(null, rows, theme, ink, font));

        var watchRows = new StackPanel();
        watchRows.Children.Add(BuildSettingRow(
            null,
            Localization.L("New images saved to these folders hang on the line (up to 3 folders); taking one down never deletes the original file."),
            null,
            ink, soft, font));

        var watchedFolders = _settings.NormalizedWatchedFolders;
        foreach (var folder in watchedFolders)
        {
            var capturedFolder = folder;
            var folderName = System.IO.Path.GetFileName(capturedFolder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(folderName)) folderName = capturedFolder;

            var btnGroup = new StackPanel { Orientation = Orientation.Horizontal };
            var showBtn = new Button
            {
                Content = Localization.L("Show in Explorer"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0)
            };
            showBtn.Click += (_, _) =>
            {
                if (Directory.Exists(capturedFolder))
                {
                    Process.Start(new ProcessStartInfo { FileName = capturedFolder, UseShellExecute = true });
                }
            };

            var stopBtn = new Button
            {
                Content = Localization.L("Stop watching"),
                Padding = new Thickness(10, 4, 10, 4)
            };
            stopBtn.Click += (_, _) =>
            {
                if (_settings.RemoveWatchedFolder(capturedFolder))
                {
                    _callbacks.SaveAndApplySettings();
                    Rebuild();
                }
            };
            btnGroup.Children.Add(showBtn);
            btnGroup.Children.Add(stopBtn);

            watchRows.Children.Add(BuildSettingRow(
                folderName,
                capturedFolder,
                btnGroup,
                ink, soft, font));
        }

        var addFolderBtn = new Button
        {
            Content = Localization.L("Add Folder…"),
            Padding = new Thickness(12, 5, 12, 5),
            IsEnabled = watchedFolders.Count < AppSettings.MaxExtraWatchedFolders
        };
        addFolderBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Localization.L("Also watch folders"),
                Multiselect = false
            };
            if (dlg.ShowDialog(this) == true && _settings.TryAddWatchedFolder(dlg.FolderName))
            {
                _callbacks.SaveAndApplySettings();
                Rebuild();
            }
        };
        watchRows.Children.Add(BuildSettingRow(null, null, addFolderBtn, ink, soft, font, isLast: true));

        var watchSheet = CleanupWindow.WrapThemedSheet(Localization.L("Also watch folders"), watchRows, theme, ink, font);
        watchSheet.Margin = new Thickness(0, 14, 0, 0);
        host.Children.Add(watchSheet);
    }

    private void BuildLineTab(StackPanel host, LineTheme theme, Brush ink, Brush soft, FontFamily font)
    {
        // 六套主题缩略预览色板
        var swatchGrid = new UniformGrid
        {
            Columns = 3,
            Margin = new Thickness(14, 6, 14, 14)
        };
        foreach (var candidate in Enum.GetValues<LineTheme>())
        {
            var captured = candidate;
            swatchGrid.Children.Add(BuildThemeSwatch(captured, selected: captured == theme, currentTheme: theme, () =>
            {
                _settings.SetActiveTheme(captured);
                _line.Theme = captured;
                _callbacks.SaveAndApplySettings();
                Rebuild();
            }));
        }
        host.Children.Add(CleanupWindow.WrapThemedSheet(Localization.L("Theme"), swatchGrid, theme, ink, font));

        var bringRows = new StackPanel();
        bringRows.Children.Add(BuildSettingRow(
            Localization.L("When the pointer rests at the top edge"),
            null,
            BuildToggle(_settings.OpensFromTopEdge, on =>
            {
                _settings.OpensFromTopEdge = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font));

        bringRows.Children.Add(BuildSettingRow(
            Localization.L("When something new hangs"),
            null,
            BuildToggle(_settings.PeeksAtNew, on =>
            {
                _settings.PeeksAtNew = on;
                _callbacks.SaveAndApplySettings();
                Rebuild();
            }),
            ink, soft, font));

        var peekCombo = new ComboBox
        {
            Width = 128,
            IsEnabled = _settings.PeeksAtNew
        };
        int peekIdx = 0;
        for (int i = 0; i < AppSettings.PeekChoices.Length; i++)
        {
            double sec = AppSettings.PeekChoices[i];
            peekCombo.Items.Add(new ComboBoxItem { Content = Localization.Format("{0:0.#} seconds", sec), Tag = sec });
            if (Math.Abs(sec - _settings.NormalizedPeekSeconds) < 0.05) peekIdx = i;
        }
        peekCombo.SelectedIndex = peekIdx;
        peekCombo.SelectionChanged += (_, _) =>
        {
            if (peekCombo.SelectedItem is ComboBoxItem { Tag: double sec })
            {
                _settings.PeekSeconds = sec;
                _callbacks.SaveAndApplySettings();
            }
        };
        bringRows.Children.Add(BuildSettingRow(Localization.L("Show it for"), null, peekCombo, ink, soft, font));

        bringRows.Children.Add(BuildSettingRow(
            Localization.L("Over full screen apps"),
            null,
            BuildToggle(_settings.ShowOverFullScreen, on =>
            {
                _settings.ShowOverFullScreen = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font,
            isLast: true));

        var bringSheet = CleanupWindow.WrapThemedSheet(Localization.L("Bring the line down"), bringRows, theme, ink, font);
        bringSheet.Margin = new Thickness(0, 14, 0, 0);
        host.Children.Add(bringSheet);

        var keepRows = new StackPanel();
        var keepCombo = new ComboBox { Width = 148 };
        keepCombo.Items.Add(new ComboBoxItem { Content = Localization.L("As many as fit"), Tag = 0 });
        int keepSel = 0;
        for (int i = 0; i < AppSettings.KeepChoices.Length; i++)
        {
            int count = AppSettings.KeepChoices[i];
            keepCombo.Items.Add(new ComboBoxItem { Content = Localization.Format("{0} items", count), Tag = count });
            if (_settings.NormalizedKeepOnLine == count) keepSel = i + 1;
        }
        keepCombo.SelectedIndex = keepSel;
        keepCombo.SelectionChanged += (_, _) =>
        {
            if (keepCombo.SelectedItem is ComboBoxItem { Tag: int raw })
            {
                int? next = raw == 0 ? null : raw;
                _settings.KeepOnLine = next;
                _line.SetKeepOnLine(next);
                _callbacks.SaveAndApplySettings();
            }
        };
        keepRows.Children.Add(BuildSettingRow(Localization.L("Keep on line"), null, keepCombo, ink, soft, font));

        keepRows.Children.Add(BuildSettingRow(
            Localization.L("Zoom on hover"),
            null,
            BuildToggle(_settings.ZoomOnHover, on =>
            {
                _settings.ZoomOnHover = on;
                _callbacks.SaveAndApplySettings();
            }),
            ink, soft, font,
            isLast: true));

        var keepSheet = CleanupWindow.WrapThemedSheet(null, keepRows, theme, ink, font);
        keepSheet.Margin = new Thickness(0, 14, 0, 0);
        host.Children.Add(keepSheet);
    }

    private void BuildKeepingTab(StackPanel host, LineTheme theme, Brush ink, Brush soft, FontFamily font)
    {
        long cap = _settings.NormalizedOriginalsCapBytes;
        var status = _line.GetRetentionStatus(DateTime.UtcNow, cap);

        var capRows = new StackPanel();
        var capCombo = new ComboBox { Width = 128 };
        int capSel = 1;
        for (int i = 0; i < AppSettings.OriginalsCapChoices.Length; i++)
        {
            var (bytes, label) = AppSettings.OriginalsCapChoices[i];
            capCombo.Items.Add(new ComboBoxItem { Content = label, Tag = bytes });
            if (bytes == cap) capSel = i;
        }
        capCombo.SelectedIndex = capSel;
        capCombo.SelectionChanged += (_, _) =>
        {
            if (capCombo.SelectedItem is ComboBoxItem { Tag: long chosenBytes })
            {
                _settings.OriginalsCapBytes = chosenBytes;
                _callbacks.SaveAndApplySettings();
                Rebuild();
            }
        };
        capRows.Children.Add(BuildSettingRow(
            Localization.L("Originals may use up to"),
            Localization.L("Past it, the oldest originals shrink first"),
            capCombo,
            ink, soft, font));

        var bar = new ProgressBar
        {
            Width = 148,
            Height = 10,
            Minimum = 0,
            Maximum = Math.Max(1.0, status.CapBytes),
            Value = Math.Min(status.OriginalsBytes, status.CapBytes),
            Foreground = ThemePalette.Brush(ThemePalette.PickedColor(theme))
        };
        capRows.Children.Add(BuildSettingRow(
            Localization.Format("Originals use {0} of {1}", Retention.FormatBytes(status.OriginalsBytes), Retention.FormatBytes(status.CapBytes)),
            null,
            bar,
            ink, soft, font));

        var cleanupBtn = new Button
        {
            Content = Localization.L("Clean Up History…"),
            Padding = new Thickness(12, 5, 12, 5)
        };
        cleanupBtn.Click += (_, _) => _callbacks.OpenCleanup();
        capRows.Children.Add(BuildSettingRow(
            Localization.Format("Next run: {0} expire, {1} originals shrink.", status.WillExpireCount, status.WillShrinkCount),
            Localization.L("What expires waits 7 days in Recently Cleared, and can be put back"),
            cleanupBtn,
            ink, soft, font,
            isLast: true));

        host.Children.Add(CleanupWindow.WrapThemedSheet(Localization.L("Room for originals"), capRows, theme, ink, font));

        var rulesRows = new StackPanel();
        rulesRows.Children.Add(BuildSettingRow(Localization.L("Text"), Localization.L("Kept a year after its last use"), null, ink, soft, font));
        rulesRows.Children.Add(BuildSettingRow(Localization.L("Screenshots and images"), Localization.L("Originals shrink to thumbnails after 30 days; the words in them can still be found"), null, ink, soft, font));
        rulesRows.Children.Add(BuildSettingRow(Localization.L("Files from Explorer"), Localization.L("Their paths are kept a year; the files themselves are never moved"), null, ink, soft, font));
        rulesRows.Children.Add(BuildSettingRow(Localization.L("On the line and pinned"), Localization.L("Never expire"), null, ink, soft, font, isLast: true));
        var rulesSheet = CleanupWindow.WrapThemedSheet(Localization.L("What EnCopy keeps"), rulesRows, theme, ink, font);
        rulesSheet.Margin = new Thickness(0, 14, 0, 0);
        host.Children.Add(rulesSheet);

        var storageRows = new StackPanel();
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
        var toggleDetailsBtn = new Button
        {
            Content = _showsStorageDetails ? Localization.L("Hide Details") : Localization.L("Show Details"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 0)
        };
        toggleDetailsBtn.Click += (_, _) =>
        {
            _showsStorageDetails = !_showsStorageDetails;
            Rebuild();
        };
        var openDataBtn = new Button
        {
            Content = Localization.L("Open data folder"),
            Padding = new Thickness(10, 4, 10, 4)
        };
        openDataBtn.Click += (_, _) => _callbacks.OpenDataFolder();
        btnRow.Children.Add(toggleDetailsBtn);
        btnRow.Children.Add(openDataBtn);

        storageRows.Children.Add(BuildSettingRow(
            Localization.L("All on this PC"),
            Localization.L("Nothing you copy leaves this PC; files stay in your local EnCopy folder"),
            btnRow,
            ink, soft, font,
            isLast: !_showsStorageDetails));

        if (_showsStorageDetails)
        {
            storageRows.Children.Add(BuildSettingRow(
                Localization.L("Text, links and code"),
                Localization.L("Each is a small file in EnCopy's folder; copying the same thing again adds nothing"),
                null, ink, soft, font));
            storageRows.Children.Add(BuildSettingRow(
                Localization.L("Files from Explorer"),
                Localization.L("Never copied and never linked: EnCopy writes their paths in a short list, and the files stay untouched"),
                null, ink, soft, font,
                isLast: true));
        }

        var storageSheet = CleanupWindow.WrapThemedSheet(Localization.L("How EnCopy stores things"), storageRows, theme, ink, font);
        storageSheet.Margin = new Thickness(0, 14, 0, 0);
        host.Children.Add(storageSheet);
    }

    private static FrameworkElement BuildPaperTabTag(string title, string glyph, bool selected, LineTheme theme, Action onClick)
    {
        var edge = ThemePalette.CardEdge(theme, PaperKind.Receipt);
        var fg = selected
            ? Brushes.White
            : ThemePalette.Brush(ThemePalette.CardInk(theme, PaperKind.Receipt));
        var bg = selected
            ? ThemePalette.Brush(ThemePalette.PickedColor(theme))
            : ThemePalette.Brush(ThemePalette.CardFill(theme, PaperKind.Receipt));

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontSize = 11.5,
            Foreground = fg,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            FontFamily = ThemePalette.CardFont(theme, code: false),
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center
        });

        var tag = new Border
        {
            CornerRadius = new CornerRadius(Math.Max(4.0, ThemePalette.CardCorner(theme))),
            Background = bg,
            BorderBrush = selected ? null : (edge is { } e ? ThemePalette.Brush(e.Color) : ThemePalette.Brush(Color.FromArgb(0x2E, 0x3D, 0x32, 0x24))),
            BorderThickness = new Thickness(selected ? 0 : (edge?.Width ?? 0.8)),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 8, 6),
            Cursor = Cursors.Hand,
            Effect = new DropShadowEffect
            {
                BlurRadius = 3,
                ShadowDepth = 1,
                Opacity = selected ? 0.25 : 0.12,
                Color = Colors.Black
            },
            Child = row
        };
        tag.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return tag;
    }

    private static FrameworkElement BuildThemeSwatch(LineTheme swatchTheme, bool selected, LineTheme currentTheme, Action onClick)
    {
        var scene = new Canvas
        {
            Width = 152,
            Height = 68,
            ClipToBounds = true,
            Background = ThemePalette.Brush(ThemePalette.SettingsBackdropColor(swatchTheme))
        };

        var ropeFig = new PathFigure { StartPoint = new Point(0, 12), IsClosed = false };
        ropeFig.Segments.Add(new QuadraticBezierSegment(new Point(76, 22), new Point(152, 12), true));
        scene.Children.Add(new System.Windows.Shapes.Path
        {
            Data = new PathGeometry([ropeFig]),
            Stroke = ThemePalette.Brush(swatchTheme == LineTheme.Neon ? Color.FromRgb(0x33, 0xF2, 0xFF) : Color.FromRgb(0x8A, 0x6D, 0x45)),
            StrokeThickness = 2.0
        });

        var leftCard = BuildMiniCard(swatchTheme, PaperKind.Receipt, -4.0);
        Canvas.SetLeft(leftCard, 30);
        Canvas.SetTop(leftCard, 15);
        scene.Children.Add(leftCard);

        var rightCard = BuildMiniCard(swatchTheme, PaperKind.Tag, 3.5);
        Canvas.SetLeft(rightCard, 84);
        Canvas.SetTop(rightCard, 15);
        scene.Children.Add(rightCard);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderBrush = selected
                ? ThemePalette.Brush(ThemePalette.PickedColor(currentTheme))
                : ThemePalette.Brush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(selected ? 2.8 : 1.0),
            ClipToBounds = true,
            Child = scene
        };

        var stack = new StackPanel
        {
            Margin = new Thickness(5),
            Cursor = Cursors.Hand
        };
        stack.Children.Add(frame);
        stack.Children.Add(new TextBlock
        {
            Text = ThemePalette.ThemeTitle(swatchTheme),
            FontSize = 11.5,
            FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
            FontFamily = ThemePalette.CardFont(currentTheme, code: false),
            Foreground = ThemePalette.Brush(ThemePalette.CardInk(currentTheme, PaperKind.Receipt)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 2)
        });
        stack.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return stack;
    }

    private static Border BuildMiniCard(LineTheme theme, PaperKind paper, double tilt)
    {
        var edge = ThemePalette.CardEdge(theme, paper);
        return new Border
        {
            Width = 38,
            Height = 40,
            CornerRadius = new CornerRadius(Math.Max(1.5, ThemePalette.CardCorner(theme) * 0.6)),
            Background = ThemePalette.Brush(ThemePalette.CardFill(theme, paper)),
            BorderBrush = edge is { } e ? ThemePalette.Brush(e.Color) : ThemePalette.Brush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
            RenderTransformOrigin = new Point(0.5, 0.0),
            RenderTransform = new RotateTransform(tilt)
        };
    }

    private static CheckBox BuildToggle(bool isChecked, Action<bool> onToggled)
    {
        var cb = new CheckBox
        {
            IsChecked = isChecked,
            VerticalAlignment = VerticalAlignment.Center
        };
        cb.Checked += (_, _) => onToggled(true);
        cb.Unchecked += (_, _) => onToggled(false);
        return cb;
    }

    private static FrameworkElement BuildSettingRow(
        string? title,
        string? detail,
        UIElement? control,
        Brush ink,
        Brush soft,
        FontFamily font,
        bool isLast = false,
        ImageSource? leadingIcon = null)
    {
        var wrap = new StackPanel();
        var grid = new Grid { Margin = new Thickness(16, 10, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (leadingIcon is not null)
        {
            var img = new Image
            {
                Source = leadingIcon,
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(img, 0);
            grid.Children.Add(img);
        }

        var textCol = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        if (!string.IsNullOrEmpty(title))
        {
            textCol.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = font,
                Foreground = ink,
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (!string.IsNullOrEmpty(detail))
        {
            textCol.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11,
                FontFamily = font,
                Foreground = soft,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }
        Grid.SetColumn(textCol, 1);
        grid.Children.Add(textCol);

        if (control is not null)
        {
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
        }

        wrap.Children.Add(grid);
        if (!isLast)
        {
            wrap.Children.Add(new Border
            {
                Height = 0.8,
                Background = soft,
                Opacity = 0.22,
                Margin = new Thickness(16, 0, 16, 0)
            });
        }
        return wrap;
    }
}

/// <summary>
/// 首次启动引导小窗（T-061 / AGENTS.md）：
/// 说明记录范围与本地数据文件夹路径；「登录时打开」默认勾选（开发版真机走查可取消勾选再点「好」），支持一键暂停记录。
/// </summary>
public sealed class OnboardingWindow : Window
{
    private readonly CheckBox _handleScreenshotsCheck;
    private readonly CheckBox _openAtLoginCheck;

    public bool PausedChosen { get; private set; }
    public bool HandleScreenshotsChosen => _handleScreenshotsCheck.IsChecked == true;
    public bool OpenAtLoginChosen => _openAtLoginCheck.IsChecked == true;

    public OnboardingWindow(LineTheme theme, Action<bool, bool> onCompleted, Action<bool>? onScreenshotsChosen = null)
    {
        Title = Localization.L("EnCopy keeps what you copy");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, PaperKind.Receipt));
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, PaperKind.Receipt));
        var font = ThemePalette.CardFont(theme, code: false);

        var body = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        body.Children.Add(new TextBlock
        {
            Text = Localization.L("Copied text, links, and Explorer file paths hang on the line and stay in your local folder:"),
            FontSize = 12.5,
            FontFamily = font,
            Foreground = ink,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new Border
        {
            Background = ThemePalette.Brush(Color.FromArgb(0x14, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 8, 0, 8),
            Child = new TextBlock
            {
                Text = DataFolder.Root,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                Foreground = ink,
                TextWrapping = TextWrapping.Wrap
            }
        });
        body.Children.Add(new TextBlock
        {
            Text = Localization.L("Verification codes clear after 10 minutes; password managers are skipped by default."),
            FontSize = 11.5,
            FontFamily = font,
            Foreground = soft,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        _handleScreenshotsCheck = new CheckBox
        {
            Content = Localization.L("Handle screenshots"),
            IsChecked = true,
            FontSize = 12,
            FontFamily = font,
            Foreground = ink,
            Margin = new Thickness(0, 0, 0, 8)
        };
        body.Children.Add(_handleScreenshotsCheck);

        _openAtLoginCheck = new CheckBox
        {
            Content = Localization.L("Open at login"),
            IsChecked = true,
            FontSize = 12,
            FontFamily = font,
            Foreground = ink,
            Margin = new Thickness(0, 0, 0, 14)
        };
        body.Children.Add(_openAtLoginCheck);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var pauseBtn = new Button
        {
            Content = Localization.L("Pause recording"),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 0)
        };
        pauseBtn.Click += (_, _) =>
        {
            PausedChosen = true;
            onScreenshotsChosen?.Invoke(HandleScreenshotsChosen);
            onCompleted(true, OpenAtLoginChosen);
            Close();
        };

        var okBtn = new Button
        {
            Content = Localization.L("OK"),
            IsDefault = true,
            Padding = new Thickness(18, 5, 18, 5),
            FontWeight = FontWeights.SemiBold
        };
        okBtn.Click += (_, _) =>
        {
            PausedChosen = false;
            onScreenshotsChosen?.Invoke(HandleScreenshotsChosen);
            onCompleted(false, OpenAtLoginChosen);
            Close();
        };
        actions.Children.Add(pauseBtn);
        actions.Children.Add(okBtn);
        body.Children.Add(actions);

        var sheet = CleanupWindow.WrapThemedSheet(Localization.L("EnCopy keeps what you copy"), body, theme, ink, font);
        sheet.Margin = new Thickness(14);

        Content = new Border
        {
            Background = ThemePalette.Brush(ThemePalette.SettingsBackdropColor(theme)),
            Child = sheet
        };
    }
}
