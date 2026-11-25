using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace EnCopy;

/// <summary>
/// 顶部绳子与篮子面板按键路由器：
/// IME 组字态下完整让渡方向键、Enter 与 Esc 给中文输入法；
/// 非组字态支持 ←/→ 挑选、Shift+←/→ 多选、Tab/Shift+Tab 筛选种类、Ctrl+P 收藏、Ctrl+Backspace/Delete 取下、Ctrl+Z 放回、↓ 展开篮子与组跳转。
/// </summary>
public static class LineKeyRouter
{
    public static bool HandleKeyDown(
        Key key,
        ModifierKeys modifiers,
        bool isImeComposing,
        string currentQuery,
        bool hasQuerySelection,
        Line line,
        Action<bool> paste,
        Action copyPicked,
        Action dismiss,
        Action clearQuery)
    {
        if (isImeComposing || key == Key.ImeProcessed || key == Key.DeadCharProcessed)
        {
            return false;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            if (!string.IsNullOrEmpty(currentQuery))
            {
                clearQuery();
            }
            else
            {
                dismiss();
            }
            return true;
        }

        if (key is Key.Enter or Key.Return)
        {
            if (modifiers == ModifierKeys.None)
            {
                paste(false);
                return true;
            }
            if (modifiers == ModifierKeys.Shift)
            {
                paste(true);
                return true;
            }
            return false;
        }

        if (key == Key.Tab && (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift))
        {
            line.CycleKindFilter(modifiers == ModifierKeys.Shift ? -1 : 1);
            return true;
        }

        if (modifiers == ModifierKeys.Control && key == Key.P)
        {
            line.TogglePinPicked();
            return true;
        }

        if (modifiers == ModifierKeys.Control && key == Key.Back)
        {
            line.TakeDownPicked();
            return true;
        }

        if (modifiers == ModifierKeys.None && key == Key.Delete && string.IsNullOrEmpty(currentQuery))
        {
            line.TakeDownPicked();
            return true;
        }

        if (modifiers == ModifierKeys.Control && key == Key.Z && string.IsNullOrEmpty(currentQuery) && line.CanPutBack)
        {
            line.PutBack();
            return true;
        }

        if (modifiers == ModifierKeys.Control && key == Key.C && !hasQuerySelection)
        {
            copyPicked();
            return true;
        }

        if (line.BasketOpen)
        {
            if (modifiers == ModifierKeys.None)
            {
                switch (key)
                {
                    case Key.Down:
                        line.VerticalArrow(1);
                        return true;
                    case Key.Up:
                        line.VerticalArrow(-1);
                        return true;
                    case Key.Left:
                        line.MoveBasketGroup(-1);
                        return true;
                    case Key.Right:
                        line.MoveBasketGroup(1);
                        return true;
                }
            }
            return false;
        }

        if (modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            bool extend = modifiers == ModifierKeys.Shift;
            if (key == Key.Left)
            {
                line.MovePick(-1, extendSelection: extend);
                return true;
            }
            if (key == Key.Right)
            {
                line.MovePick(1, extendSelection: extend);
                return true;
            }
            if (!extend && key == Key.Down)
            {
                line.VerticalArrow(1);
                return true;
            }
        }

        return false;
    }
}

public partial class LinePanel : Window
{
    private const int GcsCompStr = 0x0008;
    private const double CollapsedPanelHeight = 344.0;

    private readonly Line _line;
    private readonly Func<IReadOnlyList<Note>, IntPtr, bool, bool> _onPasteNotes;
    private readonly Func<IReadOnlyList<Note>, bool> _onCopyNotes;
    private readonly Action _onVisibilityChanged;
    private readonly Action _onRequestChangeShortcut;
    private readonly DispatcherTimer _copiedResetTimer;
    private readonly DispatcherTimer _singleClickTimer;
    private readonly CardClickCoordinator _clickCoordinator;

    private IntPtr _previousForegroundHwnd;
    private bool _isDismissing;
    private DateTime _lastDeactivatedUtc = DateTime.MinValue;
    private string? _conflictNotice;
    private double _maxPanelHeight = 680.0;
    private bool _scrollBasketToPicked = true;
    private Border? _pickedBasketRowBorder;

    public bool IsSummoned => IsVisible;

    public LinePanel(
        Line line,
        Func<IReadOnlyList<Note>, IntPtr, bool, bool> onPasteNotes,
        Func<IReadOnlyList<Note>, bool> onCopyNotes,
        Action onVisibilityChanged,
        Action onRequestChangeShortcut)
    {
        InitializeComponent();
        _line = line;
        _onPasteNotes = onPasteNotes;
        _onCopyNotes = onCopyNotes;
        _onVisibilityChanged = onVisibilityChanged;
        _onRequestChangeShortcut = onRequestChangeShortcut;

        _copiedResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _copiedResetTimer.Tick += (_, _) =>
        {
            _copiedResetTimer.Stop();
            _line.CopiedFeedbackPath = null;
        };

        int dblMs = Math.Clamp(WF.SystemInformation.DoubleClickTime, 180, 420);
        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(dblMs) };
        _clickCoordinator = new CardClickCoordinator(
            onSingleClick: (note, extend) =>
            {
                if (extend)
                {
                    _line.PickCard(note, extendSelection: true);
                }
                else
                {
                    _line.PickCard(note, extendSelection: false);
                    if (_onCopyNotes([note]))
                    {
                        _line.Use(note.FilePath);
                        FlashCopiedFeedback(note.FilePath);
                    }
                }
                QueryBox.Focus();
            },
            onDoubleClick: note =>
            {
                _line.PickCard(note, extendSelection: false);
                _onPasteNotes([note], _previousForegroundHwnd, false);
            },
            scheduleTimer: () =>
            {
                _singleClickTimer.Stop();
                _singleClickTimer.Start();
            },
            cancelTimer: () => _singleClickTimer.Stop());
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            _clickCoordinator.FlushPendingSingleClick();
        };

        _line.PropertyChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RenderAll();
            }
        };

        QueryBox.PreviewKeyDown += QueryBox_PreviewKeyDown;
        PreviewKeyDown += LinePanel_PreviewKeyDown;
        PreviewMouseWheel += LinePanel_PreviewMouseWheel;
        Deactivated += (_, _) =>
        {
            _lastDeactivatedUtc = DateTime.UtcNow;
            if (IsVisible)
            {
                Dismiss(restoreForeground: false);
            }
        };
    }

    public void SetConflictNotice(string? message)
    {
        _conflictNotice = message;
        if (IsVisible) RenderAll();
    }

    public void SummonOrToggle()
    {
        if (IsVisible)
        {
            Dismiss(restoreForeground: true);
            return;
        }
        Summon();
    }

    public void SummonOrToggleFromTray()
    {
        if (IsVisible)
        {
            Dismiss(restoreForeground: true);
            return;
        }
        if ((DateTime.UtcNow - _lastDeactivatedUtc).TotalMilliseconds < 250)
        {
            return;
        }
        Summon();
    }

    public void Summon()
    {
        var selfHwnd = new WindowInteropHelper(this).Handle;
        var fg = WindowInteropHelperEx.CurrentForegroundWindow();
        if (fg != IntPtr.Zero && fg != selfHwnd && !IsTaskbarWindow(fg))
        {
            _previousForegroundHwnd = fg;
        }

        _clickCoordinator.Cancel();
        _scrollBasketToPicked = true;
        _line.CloseBasket();
        _line.KindFilter = KindFilter.All;
        _line.Query = string.Empty;
        if (!string.Equals(QueryBox.Text, string.Empty, StringComparison.Ordinal))
        {
            QueryBox.Text = string.Empty;
        }

        var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
        var monitors = ScreenPlacement.EnumerateMonitors();
        var expandedRect = ScreenPlacement.TopLineStrip(cx, cy, monitors, 680.0);
        _maxPanelHeight = expandedRect.Height;
        var rect = ScreenPlacement.TopLineStrip(cx, cy, monitors, CollapsedPanelHeight);
        _line.SetVisibleCapacity(ScreenPlacement.ComputeVisibleCapacity(rect.Width));
        _line.ReloadFromHistory();
        _line.PickDefault();

        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;

        RenderAll();
        Show();
        Activate();
        QueryBox.Focus();
        _onVisibilityChanged();
    }

    public void Dismiss(bool restoreForeground, IntPtr? targetOverride = null)
    {
        if (!IsVisible || _isDismissing) return;
        _isDismissing = true;
        try
        {
            var target = targetOverride ?? _previousForegroundHwnd;
            _clickCoordinator.Cancel();
            _copiedResetTimer.Stop();
            _line.CopiedFeedbackPath = null;
            _line.CloseBasket();
            _line.KindFilter = KindFilter.All;
            _line.Query = string.Empty;
            if (!string.Equals(QueryBox.Text, string.Empty, StringComparison.Ordinal))
            {
                QueryBox.Text = string.Empty;
            }
            // 先趁面板仍处于前台窗口时把焦点归还原窗口，再 Hide() 避免失去 Win32 切焦点资格
            if (restoreForeground && target != IntPtr.Zero)
            {
                WindowInteropHelperEx.RestoreForeground(target);
            }
            Hide();
            _onVisibilityChanged();
        }
        finally
        {
            _isDismissing = false;
        }
    }

    public void RenderOffscreen(double width, double height, bool? darkOverride = null)
    {
        ThemePalette.SetDarkOverride(darkOverride);
        try
        {
            Width = width;
            Height = height;
            _maxPanelHeight = height;
            _line.SetVisibleCapacity(ScreenPlacement.ComputeVisibleCapacity(width));
            RenderAll(overrideCanvasWidth: width);
            RootGrid.Measure(new Size(width, height));
            RootGrid.Arrange(new Rect(0, 0, width, height));
            RootGrid.UpdateLayout();
        }
        finally
        {
            if (darkOverride is not null)
            {
                ThemePalette.SetDarkOverride(null);
            }
        }
    }

    private void RenderAll(double? overrideCanvasWidth = null)
    {
        bool isDark = ThemePalette.IsDarkMode();
        double stripWidth = overrideCanvasWidth ?? (Width > 100 ? Width : 1280);

        if (IsVisible && overrideCanvasWidth is null)
        {
            double targetHeight = _line.BasketOpen ? _maxPanelHeight : CollapsedPanelHeight;
            if (Math.Abs(Height - targetHeight) > 1.0)
            {
                Height = targetHeight;
            }
        }

        // 外壳主题自适应（纸面本身永远保持浅色）
        SearchShell.Width = _line.BasketOpen ? Math.Min(640.0, Math.Max(420.0, stripWidth - 40.0)) : 440.0;
        SearchShell.Background = ThemePalette.Brush(ThemePalette.ChromeFill(isDark));
        SearchShell.BorderBrush = ThemePalette.Brush(ThemePalette.ChromeBorder(isDark));
        SearchGlyph.Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(isDark));
        QueryBox.Foreground = ThemePalette.Brush(ThemePalette.ChromeText(isDark));
        QueryBox.CaretBrush = ThemePalette.Brush(ThemePalette.ChromeText(isDark));
        PlaceholderText.Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(isDark));
        PlaceholderText.Text = _line.BasketOpen
            ? Localization.L("Type to narrow the basket")
            : Localization.L("Search all history");
        PlaceholderText.Visibility = string.IsNullOrEmpty(QueryBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        CountText.Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(isDark));
        CountText.Text = _line.IsSearching && !_line.BasketOpen
            ? Localization.Format("{0} of {1}", _line.MatchCount, _line.TotalCount)
            : string.Empty;

        FooterHintText.Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(isDark));
        FooterHintText.Text = _line.BasketOpen
            ? Localization.L("Up/Down pick · Left/Right group · Enter paste · Ctrl+P pin · Esc close")
            : Localization.L("Left/Right pick · Tab filter · Ctrl+P pin · Down basket · Enter paste");

        UpdateActiveFilterPill();
        UpdateConflictNotice(isDark);

        PaperCardView.BuildRopeAndCards(
            LineCanvas,
            stripWidth,
            _line,
            isDark,
            onCardMouseClick: (note, clicks, extend) => _clickCoordinator.HandleClick(note, clicks, extend),
            onBasketHandleClick: () =>
            {
                _scrollBasketToPicked = true;
                if (_line.BasketOpen) _line.CloseBasket();
                else _line.OpenBasket();
                QueryBox.Focus();
            });

        RenderBasketSection(isDark);
    }

    private void RenderBasketSection(bool isDark)
    {
        if (!_line.BasketOpen)
        {
            BasketSection.Visibility = Visibility.Collapsed;
            return;
        }

        BasketSection.Visibility = Visibility.Visible;
        double noticeOverhead = NoticeBanner.Visibility == Visibility.Visible ? 168.0 : 124.0;
        BasketScroller.MaxHeight = Math.Max(140.0, _maxPanelHeight - ScreenPlacement.LineBandHeight - noticeOverhead);
        BasketDivider.Background = ThemePalette.Brush(ThemePalette.DividerColor(isDark));

        // 种类筛选胶囊栏
        BasketChipsBar.Children.Clear();
        AddKindChip(KindFilter.All, Localization.L("All"), _line.BasketMatchCount, ThemePalette.ChromeSubText(isDark), isDark);
        AddKindChip(KindFilter.Text, Localization.L("Text"), _line.BasketKindCounts.GetValueOrDefault(KindFilter.Text), ThemePalette.TagColor(ItemKind.Text), isDark);
        AddKindChip(KindFilter.Link, Localization.L("Links"), _line.BasketKindCounts.GetValueOrDefault(KindFilter.Link), ThemePalette.TagColor(ItemKind.Link), isDark);
        AddKindChip(KindFilter.Image, Localization.L("Images"), _line.BasketKindCounts.GetValueOrDefault(KindFilter.Image), ThemePalette.TagColor(ItemKind.Image), isDark);
        AddKindChip(KindFilter.File, Localization.L("Files"), _line.BasketKindCounts.GetValueOrDefault(KindFilter.File), ThemePalette.TagColor(ItemKind.File), isDark);

        // 按天/按月分组列表
        BasketGroupsHost.Children.Clear();
        _pickedBasketRowBorder = null;

        if (_line.BasketGroups.Count == 0)
        {
            BasketGroupsHost.Children.Add(new TextBlock
            {
                Text = _line.IsSearching ? Localization.L("Nothing in the basket matches") : Localization.L("The basket is empty"),
                FontSize = 13,
                Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(isDark)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 26, 0, 26)
            });
            return;
        }

        foreach (var group in _line.BasketGroups)
        {
            var headerGrid = new Grid
            {
                Background = ThemePalette.Brush(ThemePalette.GroupHeaderFill(isDark)),
                Margin = new Thickness(0, 4, 0, 2)
            };
            var innerHeader = new Grid { Margin = new Thickness(14, 5, 14, 5) };
            innerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            innerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = group.Title,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(isDark))
            };
            Grid.SetColumn(title, 0);
            innerHeader.Children.Add(title);

            var countText = new TextBlock
            {
                Text = group.Rows.Count == 1
                    ? Localization.L("1 item")
                    : Localization.Format("{0} items", group.Rows.Count),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(isDark))
            };
            Grid.SetColumn(countText, 1);
            innerHeader.Children.Add(countText);
            headerGrid.Children.Add(innerHeader);
            BasketGroupsHost.Children.Add(headerGrid);

            foreach (var row in group.Rows)
            {
                bool picked = _line.BasketPicked is not null &&
                    string.Equals(_line.BasketPicked.FilePath, row.Note.FilePath, StringComparison.OrdinalIgnoreCase);
                var rowUi = PaperCardView.BuildBasketRow(
                    row,
                    picked,
                    isDark,
                    onSelect: r =>
                    {
                        // 鼠标选中不自动跳转滚动条，避免双击窗口内微偏而错失第二次点击
                        _scrollBasketToPicked = false;
                        _line.BasketPicked = r.Note;
                        QueryBox.Focus();
                    },
                    onActivate: r =>
                    {
                        _line.BasketPicked = r.Note;
                        _onPasteNotes([r.Note], _previousForegroundHwnd, false);
                    });

                var wrapper = new Border { Child = rowUi };
                if (picked)
                {
                    _pickedBasketRowBorder = wrapper;
                }
                BasketGroupsHost.Children.Add(wrapper);
            }
        }

        if (_scrollBasketToPicked && _pickedBasketRowBorder is { } rowToScroll && IsVisible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => rowToScroll.BringIntoView());
        }
    }

    private void AddKindChip(KindFilter kind, string label, int count, Color dotColor, bool isDark)
    {
        bool active = _line.KindFilter == kind;
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (kind != KindFilter.All)
        {
            panel.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = ThemePalette.Brush(active ? Colors.White : dotColor),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemePalette.Brush(active ? Colors.White : ThemePalette.ChromeText(isDark))
        });
        panel.Children.Add(new TextBlock
        {
            Text = $" {count}",
            FontSize = 11,
            Foreground = ThemePalette.Brush(active ? Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF) : ThemePalette.ChromeSubText(isDark))
        });

        var chip = new Border
        {
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(9, 4, 9, 4),
            CornerRadius = new CornerRadius(12),
            Cursor = Cursors.Hand,
            Opacity = count == 0 && !active ? 0.45 : 1.0,
            Background = ThemePalette.Brush(active ? ThemePalette.ChipActiveFill(isDark) : ThemePalette.ChipIdleFill(isDark)),
            Child = panel
        };
        chip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _scrollBasketToPicked = true;
            _line.KindFilter = kind;
            QueryBox.Focus();
        };
        BasketChipsBar.Children.Add(chip);
    }

    private void UpdateActiveFilterPill()
    {
        if (_line.BasketOpen || _line.KindFilter == KindFilter.All)
        {
            ActiveFilterPill.Visibility = Visibility.Collapsed;
            return;
        }
        ActiveFilterPill.Visibility = Visibility.Visible;
        var (text, color) = _line.KindFilter switch
        {
            KindFilter.Text => (Localization.L("Text"), ThemePalette.TagColor(ItemKind.Text)),
            KindFilter.Link => (Localization.L("Links"), ThemePalette.TagColor(ItemKind.Link)),
            KindFilter.Image => (Localization.L("Images"), ThemePalette.TagColor(ItemKind.Image)),
            KindFilter.File => (Localization.L("Files"), ThemePalette.TagColor(ItemKind.File)),
            _ => (Localization.L("All"), ThemePalette.TagColor(ItemKind.Text))
        };
        ActiveFilterPill.Background = ThemePalette.Brush(color);
        ActiveFilterLabel.Foreground = Brushes.White;
        ActiveFilterLabel.Text = text;
    }

    private void UpdateConflictNotice(bool isDark)
    {
        if (string.IsNullOrWhiteSpace(_conflictNotice))
        {
            NoticeBanner.Visibility = Visibility.Collapsed;
            NoticeText.Text = string.Empty;
            return;
        }
        NoticeBanner.Background = ThemePalette.Brush(ThemePalette.NoticeBg(isDark));
        NoticeBanner.BorderBrush = ThemePalette.Brush(ThemePalette.NoticeBorder(isDark));
        NoticeText.Foreground = ThemePalette.Brush(ThemePalette.NoticeFg(isDark));
        NoticeText.Text = _conflictNotice;
        NoticeBanner.Visibility = Visibility.Visible;
    }

    private void FlashCopiedFeedback(string filePath)
    {
        _line.CopiedFeedbackPath = filePath;
        _copiedResetTimer.Stop();
        _copiedResetTimer.Start();
    }

    private void LinePanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!QueryBox.IsKeyboardFocusWithin)
        {
            QueryBox.Focus();
            HandleRouting(e);
        }
    }

    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e) => HandleRouting(e);

    private void HandleRouting(KeyEventArgs e)
    {
        _scrollBasketToPicked = true;
        bool composing = e.Key == Key.ImeProcessed || e.Key == Key.DeadCharProcessed || HasActiveImeCompositionString();
        bool handled = LineKeyRouter.HandleKeyDown(
            e.Key,
            Keyboard.Modifiers,
            composing,
            QueryBox.Text,
            QueryBox.SelectionLength > 0,
            _line,
            paste: plain => PasteSelected(plain),
            copyPicked: CopySelected,
            dismiss: () => Dismiss(restoreForeground: true),
            clearQuery: () =>
            {
                QueryBox.Text = string.Empty;
                _line.Query = string.Empty;
            });

        if (handled)
        {
            e.Handled = true;
        }
    }

    private void LinePanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_line.BasketOpen) return;
        if (e.GetPosition(RootGrid).Y > ScreenPlacement.LineBandHeight) return;
        if (_line.MaxScrollOffset() <= 0) return;

        double deltaDip = e.Delta > 0 ? -ScreenPlacement.CardSpacing : ScreenPlacement.CardSpacing;
        _line.ScrollBy(deltaDip);
        e.Handled = true;
    }

    private void PasteSelected(bool plain)
    {
        var selected = _line.GetSelectedNotesInLineOrder();
        if (selected.Count == 0) return;
        _onPasteNotes(selected, _previousForegroundHwnd, plain);
    }

    private void CopySelected()
    {
        var selected = _line.GetSelectedNotesInLineOrder();
        if (selected.Count == 0) return;
        if (_onCopyNotes(selected))
        {
            var stamp = DateTime.UtcNow;
            for (int i = selected.Count - 1; i >= 0; i--)
            {
                _line.Use(selected[i].FilePath, stamp.AddMilliseconds(selected.Count - 1 - i));
            }
            FlashCopiedFeedback(selected[0].FilePath);
        }
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = string.IsNullOrEmpty(QueryBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _line.Query = QueryBox.Text;
    }

    private void NoticeBanner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _onRequestChangeShortcut();
    }

    private bool HasActiveImeCompositionString()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        IntPtr hImc = ImmGetContext(hwnd);
        if (hImc == IntPtr.Zero) return false;
        try
        {
            int len = ImmGetCompositionString(hImc, GcsCompStr, IntPtr.Zero, 0);
            return len > 0;
        }
        finally
        {
            ImmReleaseContext(hwnd, hImc);
        }
    }

    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        if (len <= 0) return false;
        var cls = sb.ToString();
        return string.Equals(cls, "Shell_TrayWnd", StringComparison.Ordinal) ||
               string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.Ordinal);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int ImmGetCompositionString(IntPtr hIMC, int dwIndex, IntPtr lpBuf, int dwBufLen);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
}
