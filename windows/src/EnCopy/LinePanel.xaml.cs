using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace EnCopy;

/// <summary>
/// 顶部绳子与篮子面板按键路由器：
/// IME 组字态下完整让渡方向键、Enter 与 Esc 给中文输入法；
/// 搜索框有文字且光标不在末尾时（T-070 / CONTEXT.md），←/→ 归输入框移动光标；
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
        Action clearQuery,
        bool? canUndoQuery = null,
        bool caretAtEnd = true)
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

        bool queryBlocksPutBack = canUndoQuery ?? !string.IsNullOrEmpty(currentQuery);
        if (modifiers == ModifierKeys.Control && key == Key.Z && !queryBlocksPutBack && line.CanPutBack)
        {
            line.PutBack();
            return true;
        }

        if (modifiers == ModifierKeys.Control && key == Key.C && !hasQuerySelection)
        {
            copyPicked();
            return true;
        }

        bool caretInsideQuery = !string.IsNullOrEmpty(currentQuery) && (!caretAtEnd || hasQuerySelection);

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
                    case Key.Left when !caretInsideQuery:
                        line.MoveBasketGroup(-1);
                        return true;
                    case Key.Right when !caretInsideQuery:
                        line.MoveBasketGroup(1);
                        return true;
                }
            }
            return false;
        }

        if (modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            bool extend = modifiers == ModifierKeys.Shift;
            if ((key is Key.Left or Key.Right) && (caretInsideQuery || (extend && !string.IsNullOrEmpty(currentQuery))))
            {
                return false;
            }
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
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const double CollapsedPanelHeight = 344.0;

    private readonly Line _line;
    private readonly Func<IReadOnlyList<Note>, IntPtr, bool, bool> _onPasteNotes;
    private readonly Func<IReadOnlyList<Note>, bool> _onCopyNotes;
    private readonly Action _onVisibilityChanged;
    private readonly Action _onRequestChangeShortcut;
    private readonly HeldWindowManager? _heldManager;
    private readonly WindowsShareService? _shareService;
    private readonly Func<bool>? _getZoomOnHover;

    private readonly MotionEngine _motion = new();
    private readonly HoverZoomController _zoomController = new();
    private readonly DispatcherTimer _copiedResetTimer;
    private readonly DispatcherTimer _singleClickTimer;
    private readonly DispatcherTimer _breezeTimer;
    private readonly DispatcherTimer _peekTimer;
    private readonly DispatcherTimer _hoverZoomPollTimer;
    private readonly CardClickCoordinator _clickCoordinator;

    private IntPtr _previousForegroundHwnd;
    private DisplayMonitor _activeMonitor;
    private Note? _hoveredCard;
    private bool _isDismissing;
    private bool _isPinnedBySummon;
    private bool _showBasketHandle = true;
    private bool _isDraggingOut;
    private int _openContextMenus;
    private DateTime _lastKeyUtc = DateTime.MinValue;
    private DateTime _lastDeactivatedUtc = DateTime.MinValue;
    private string? _conflictNotice;
    private double _maxPanelHeight = 680.0;
    private bool _scrollBasketToPicked = true;
    private Border? _pickedBasketRowBorder;

    public bool IsSummoned => IsVisible;
    public bool IsPinnedBySummon => IsVisible && _isPinnedBySummon;
    public bool IsInteractionHoldActive => _isDraggingOut || _openContextMenus > 0 || (_shareService?.IsOpen ?? false);
    public DisplayMonitor ActiveMonitor => _activeMonitor;

    public int PanelBottomPhysicalY
    {
        get
        {
            double scale = _activeMonitor.DpiScale > 0 ? _activeMonitor.DpiScale : 1.0;
            return (int)Math.Round((Top + ActualHeight) * scale);
        }
    }

    public LinePanel(
        Line line,
        Func<IReadOnlyList<Note>, IntPtr, bool, bool> onPasteNotes,
        Func<IReadOnlyList<Note>, bool> onCopyNotes,
        Action onVisibilityChanged,
        Action onRequestChangeShortcut,
        HeldWindowManager? heldManager = null,
        WindowsShareService? shareService = null,
        Func<bool>? getZoomOnHover = null)
    {
        InitializeComponent();
        _line = line;
        _onPasteNotes = onPasteNotes;
        _onCopyNotes = onCopyNotes;
        _onVisibilityChanged = onVisibilityChanged;
        _onRequestChangeShortcut = onRequestChangeShortcut;
        _heldManager = heldManager;
        _shareService = shareService;
        _getZoomOnHover = getZoomOnHover;

        _motion.FrameUpdated += ApplyMotionFrame;

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
                        _motion.TriggerHop(note.FilePath);
                        FlashCopiedFeedback(note.FilePath);
                    }
                }
                if (_isPinnedBySummon) QueryBox.Focus();
            },
            onDoubleClick: note =>
            {
                _line.PickCard(note, extendSelection: false);
                LaunchFlyToCursorFor(note);
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

        _breezeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _breezeTimer.Tick += (_, _) =>
        {
            ScheduleNextBreeze();
            if (IsVisible && !IsInteractionHoldActive && Motion.CanBreeze(DateTime.UtcNow, _lastKeyUtc))
            {
                _motion.TriggerGust(_line.ShownItems, summoned: _isPinnedBySummon);
            }
        };

        _peekTimer = new DispatcherTimer();
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            if (IsVisible && !_isPinnedBySummon && !IsInteractionHoldActive)
            {
                var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
                if (cy > PanelBottomPhysicalY + 16)
                {
                    Dismiss(restoreForeground: false, animateTuck: true);
                }
            }
        };

        _hoverZoomPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _hoverZoomPollTimer.Tick += (_, _) =>
        {
            if (!IsVisible || (_getZoomOnHover is not null && !_getZoomOnHover()) || IsInteractionHoldActive || _line.BasketOpen)
            {
                _zoomController.Hide();
                return;
            }
            _zoomController.Update(_hoveredCard, _activeMonitor, ThemePalette.IsDarkMode(), _line.Theme);
        };

        _line.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(Line.BasketOpen), StringComparison.Ordinal) && _line.BasketOpen && IsVisible)
            {
                _motion.BounceRope(Motion.BasketTaut, bySummon: true);
            }
            if (IsVisible)
            {
                RenderAll();
            }
        };

        SearchShell.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (!_isPinnedBySummon)
            {
                PromoteToInteractiveSummon();
            }
        };

        QueryBox.PreviewKeyDown += QueryBox_PreviewKeyDown;
        PreviewKeyDown += LinePanel_PreviewKeyDown;
        PreviewMouseWheel += LinePanel_PreviewMouseWheel;
        Deactivated += (_, _) =>
        {
            _lastDeactivatedUtc = DateTime.UtcNow;
            if (IsVisible && _isPinnedBySummon && !IsInteractionHoldActive)
            {
                Dismiss(restoreForeground: false);
            }
        };
        Closed += (_, _) =>
        {
            _breezeTimer.Stop();
            _peekTimer.Stop();
            _hoverZoomPollTimer.Stop();
            _zoomController.Dispose();
            _motion.Dispose();
        };
    }

    public void SetConflictNotice(string? message)
    {
        _conflictNotice = message;
        if (IsVisible) RenderAll();
    }

    public void NotifyNewCardHung(string filePath)
    {
        if (!IsVisible) return;
        var note = _line.ShownItems.FirstOrDefault(n => string.Equals(n.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (note is not null)
        {
            _motion.TriggerNewCard(note.FilePath, note.Paper);
        }
    }

    public void AnimateHeldReturn(string filePath, Rect fromDip, BitmapSource? snapshot)
    {
        if (!IsVisible || snapshot is null) return;
        if (PaperCardView.TryFindCardVisual(LineCanvas, filePath, out var cardEl) && cardEl is not null)
        {
            var pt = cardEl.TranslatePoint(new Point(0, 0), this);
            var targetDip = new Rect(Left + pt.X, Top + pt.Y, cardEl.ActualWidth > 0 ? cardEl.ActualWidth : ScreenPlacement.CardWidth, ScreenPlacement.CardHeight);
            FlightOverlay.FlyBackToLine(snapshot, fromDip, targetDip);
        }
    }

    public void SummonOrToggle()
    {
        if (IsVisible && _isPinnedBySummon)
        {
            Dismiss(restoreForeground: true);
            return;
        }
        Summon();
    }

    public void SummonOrToggleFromTray()
    {
        if (IsVisible && _isPinnedBySummon)
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
        _peekTimer.Stop();
        RecordPreviousForeground();

        _isPinnedBySummon = true;
        _showBasketHandle = true;
        PrepareAndPlaceOnCursorScreen();

        SetNoActivateStyle(noActivate: false);
        ShowActivated = true;
        RenderAll();
        _motion.BeginReveal(bySummon: true, CollapsedPanelHeight, _line.ShownItems);
        Show();
        Activate();
        QueryBox.Focus();
        StartActiveTimers();
        _onVisibilityChanged();
    }

    public void RevealByHover()
    {
        if (IsVisible) return;
        _peekTimer.Stop();
        RecordPreviousForeground();

        _isPinnedBySummon = false;
        _showBasketHandle = true;
        PrepareAndPlaceOnCursorScreen();

        ShowActivated = false;
        SetNoActivateStyle(noActivate: true);
        RenderAll();
        _motion.BeginReveal(bySummon: false, CollapsedPanelHeight, _line.ShownItems);
        Show();
        StartActiveTimers();
        _onVisibilityChanged();
    }

    public void PeekNewItem(double durationSeconds, string? newFilePath = null)
    {
        if (!IsVisible)
        {
            RecordPreviousForeground();
            _isPinnedBySummon = false;
            _showBasketHandle = false;
            PrepareAndPlaceOnCursorScreen();

            ShowActivated = false;
            SetNoActivateStyle(noActivate: true);
            RenderAll();
            _motion.BeginReveal(bySummon: false, CollapsedPanelHeight, _line.ShownItems);
            Show();
            StartActiveTimers();
            _onVisibilityChanged();
        }

        if (newFilePath is not null)
        {
            NotifyNewCardHung(newFilePath);
        }

        if (!_isPinnedBySummon)
        {
            _peekTimer.Stop();
            _peekTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 0.5, 10.0));
            _peekTimer.Start();
        }
    }

    public void Dismiss(bool restoreForeground, IntPtr? targetOverride = null, bool animateTuck = false)
    {
        if (!IsVisible || _isDismissing) return;
        _isDismissing = true;
        try
        {
            _peekTimer.Stop();
            _breezeTimer.Stop();
            _hoverZoomPollTimer.Stop();
            _zoomController.Hide();
            _hoveredCard = null;

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

            if (restoreForeground && target != IntPtr.Zero)
            {
                WindowInteropHelperEx.RestoreForeground(target);
            }

            _isPinnedBySummon = false;
            if (animateTuck && !IsInChecksRunner())
            {
                _motion.BeginTuck(CollapsedPanelHeight, () =>
                {
                    Hide();
                    _motion.SnapAllToRest();
                    ApplyMotionFrame();
                    _onVisibilityChanged();
                });
            }
            else
            {
                _motion.SnapAllToRest();
                ApplyMotionFrame();
                Hide();
                _onVisibilityChanged();
            }
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
            _motion.SnapAllToRest();
            ApplyMotionFrame();
            _showBasketHandle = true;
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

    private void PromoteToInteractiveSummon()
    {
        _peekTimer.Stop();
        RecordPreviousForeground();
        _isPinnedBySummon = true;
        _showBasketHandle = true;
        SetNoActivateStyle(noActivate: false);
        Activate();
        QueryBox.Focus();
        RenderAll();
        _onVisibilityChanged();
    }

    private void RecordPreviousForeground()
    {
        var selfHwnd = new WindowInteropHelper(this).Handle;
        var fg = WindowInteropHelperEx.CurrentForegroundWindow();
        if (fg != IntPtr.Zero && fg != selfHwnd && !IsTaskbarWindow(fg))
        {
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid != (uint)Environment.ProcessId)
            {
                _previousForegroundHwnd = fg;
            }
        }
    }

    private void PrepareAndPlaceOnCursorScreen()
    {
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
        _activeMonitor = ScreenPlacement.PickMonitor(cx, cy, monitors);
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
    }

    private void StartActiveTimers()
    {
        ScheduleNextBreeze();
        _breezeTimer.Start();
        _hoverZoomPollTimer.Start();
    }

    private void ScheduleNextBreeze()
    {
        _breezeTimer.Interval = TimeSpan.FromSeconds(7.0 + Random.Shared.NextDouble() * 9.0);
    }

    private void ApplyMotionFrame()
    {
        PanelSlideTransform.Y = _motion.PanelSlideY;
        RootGrid.Opacity = _motion.PanelOpacity;
        if (IsVisible && _motion.IsActive)
        {
            BuildRopeCanvasOnly();
        }
    }

    private void BuildRopeCanvasOnly(double? overrideCanvasWidth = null)
    {
        bool isDark = ThemePalette.IsDarkMode();
        double stripWidth = overrideCanvasWidth ?? (Width > 100 ? Width : 1280);
        var callbacks = new CardInteractionCallbacks(
            OnMouseClick: (note, clicks, extend) => _clickCoordinator.HandleClick(note, clicks, extend),
            OnHoverChanged: note => _hoveredCard = note,
            OnStartDrag: StartCardDragOut,
            OnDiscardCross: DiscardSingleCardWithFall,
            OnHangBackHeld: note => _heldManager?.HangBack(note.FilePath),
            BuildContextMenu: BuildCardContextMenu);

        PaperCardView.BuildRopeAndCards(
            LineCanvas,
            stripWidth,
            _line,
            isDark,
            callbacks,
            onBasketHandleClick: () =>
            {
                if (!_isPinnedBySummon) PromoteToInteractiveSummon();
                _scrollBasketToPicked = true;
                if (_line.BasketOpen) _line.CloseBasket();
                else _line.OpenBasket();
                QueryBox.Focus();
            },
            motion: _motion,
            showBasketHandle: _showBasketHandle);
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

        SearchShell.Visibility = _showBasketHandle ? Visibility.Visible : Visibility.Collapsed;
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
        BuildRopeCanvasOnly(overrideCanvasWidth);
        RenderBasketSection(isDark);
    }

    private void StartCardDragOut(Note note, UIElement cardVisual)
    {
        _clickCoordinator.Cancel();
        _zoomController.Hide();

        var full = Path.GetFullPath(note.FilePath);
        var group = _line.SelectedPaths.Contains(full) && _line.SelectedPaths.Count > 1
            ? _line.GetSelectedNotesInLineOrder()
            : [note];

        var data = new DataObject();
        var dropFiles = new StringCollection();
        var texts = new List<string>();

        foreach (var item in group)
        {
            if (item.FileTargets.Count > 0)
            {
                foreach (var target in item.FileTargets.Where(p => File.Exists(p) || Directory.Exists(p)))
                {
                    dropFiles.Add(Path.GetFullPath(target));
                }
            }
            else if (File.Exists(item.FilePath))
            {
                dropFiles.Add(Path.GetFullPath(item.FilePath));
            }

            if (item.Kind is ItemKind.Text or ItemKind.Code or ItemKind.Link)
            {
                texts.Add(item.FullText);
                if (group.Count == 1 && string.Equals(Path.GetExtension(item.FilePath), ".rtf", StringComparison.OrdinalIgnoreCase) && File.Exists(item.FilePath))
                {
                    data.SetData(DataFormats.Rtf, File.ReadAllText(item.FilePath, Encoding.UTF8));
                }
            }
        }

        if (texts.Count > 0)
        {
            data.SetData(DataFormats.UnicodeText, string.Join(Environment.NewLine, texts), autoConvert: true);
        }
        if (dropFiles.Count > 0)
        {
            data.SetFileDropList(dropFiles);
        }
        data.SetData(ClipboardWatcher.SelfCopyFormat, new byte[] { 1 });

        bool allMovablePrivate = group.All(n => !n.Pinned && DataFolder.Contains(n.FilePath) && n.FileTargets.Count == 0);
        var allowed = allMovablePrivate
            ? (DragDropEffects.Copy | DragDropEffects.Move)
            : DragDropEffects.Copy;

        _isDraggingOut = true;
        try
        {
            var result = DragDrop.DoDragDrop(cardVisual, data, allowed);
            if ((result & DragDropEffects.Move) != 0)
            {
                _line.PruneMissingFromLine();
            }
            else if (result != DragDropEffects.None)
            {
                var stamp = DateTime.UtcNow;
                for (int i = group.Count - 1; i >= 0; i--)
                {
                    _line.Use(group[i].FilePath, stamp.AddMilliseconds(group.Count - 1 - i));
                }
            }
        }
        finally
        {
            _isDraggingOut = false;
        }
    }

    private ContextMenu BuildCardContextMenu(Note note, UIElement cardVisual)
    {
        _clickCoordinator.Cancel();
        _zoomController.Hide();

        var full = Path.GetFullPath(note.FilePath);
        bool multi = _line.SelectedPaths.Contains(full) && _line.SelectedPaths.Count > 1;
        var targets = multi ? _line.GetSelectedNotesInLineOrder() : [note];

        var menu = new ContextMenu();
        menu.Opened += (_, _) => _openContextMenus++;
        menu.Closed += (_, _) => _openContextMenus = Math.Max(0, _openContextMenus - 1);

        if (multi)
        {
            menu.Items.Add(new MenuItem
            {
                Header = Localization.Format("{0} selected", targets.Count),
                IsEnabled = false
            });
            menu.Items.Add(new Separator());
        }

        AddMenuItem(menu, Localization.L("Copy"), () =>
        {
            if (_onCopyNotes(targets))
            {
                foreach (var t in targets) _line.Use(t.FilePath);
                FlashCopiedFeedback(targets[0].FilePath);
            }
        });

        AddMenuItem(menu, Localization.L("Copy Path"), () =>
        {
            var pathsText = string.Join(Environment.NewLine, targets.SelectMany(t => t.FileTargets.Count > 0 ? t.FileTargets : [t.FilePath]));
            var pathNote = Note.Parse(targets[0].FilePath, targets[0].RelativePath, pathsText, DateTime.Now);
            _onCopyNotes([pathNote]);
        });

        AddMenuItem(menu, Localization.L("Open"), () =>
        {
            foreach (var t in targets)
            {
                var openPath = t.FileTargets.FirstOrDefault() ?? t.FilePath;
                if (File.Exists(openPath) || Directory.Exists(openPath))
                {
                    Process.Start(new ProcessStartInfo { FileName = openPath, UseShellExecute = true });
                }
            }
        });

        if (!multi && note.Link is { } uri)
        {
            AddMenuItem(menu, Localization.L("Open Link"), () =>
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true }));
        }

        AddMenuItem(menu, Localization.L("Show in Explorer"), () =>
        {
            var revealPath = (targets[0].FileTargets.FirstOrDefault() ?? targets[0].FilePath);
            if (File.Exists(revealPath) || Directory.Exists(revealPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{Path.GetFullPath(revealPath)}\"",
                    UseShellExecute = true
                });
            }
        });

        if (!multi && _heldManager is not null && note.Kind != ItemKind.Video)
        {
            AddMenuItem(menu, Localization.L("Hold"), () =>
            {
                _heldManager.Hold(
                    note,
                    _line,
                    _onCopyNotes,
                    onHangBack: (p, fromRect, snap) => AnimateHeldReturn(p, fromRect, snap));
            });
        }

        if (_shareService is not null)
        {
            menu.Items.Add(new Separator());
            AddMenuItem(menu, Localization.L("Share…"), () =>
            {
                var cardDip = GetVisualScreenDip(cardVisual);
                _shareService.ShowForNotes(targets, cardDip);
            });
        }

        menu.Items.Add(new Separator());
        if (targets.Any(t => !t.Pinned))
        {
            AddMenuItem(menu, Localization.L("Pin"), () =>
            {
                foreach (var t in targets.Where(x => !x.Pinned)) _line.TogglePinByPath(t.FilePath);
            });
        }
        if (targets.Any(t => t.Pinned))
        {
            AddMenuItem(menu, Localization.L("Unpin"), () =>
            {
                foreach (var t in targets.Where(x => x.Pinned)) _line.TogglePinByPath(t.FilePath);
            });
        }

        AddMenuItem(menu, Localization.L("Save to Desktop"), () => _line.SaveToDesktop(targets));

        menu.Items.Add(new Separator());
        AddMenuItem(menu, Localization.L("Take down"), () =>
        {
            if (targets.Count == 1) DiscardSingleCardWithFall(targets[0], cardVisual);
            else _line.TakeDownNotes(targets);
        });

        if (!multi && !note.Pinned)
        {
            AddMenuItem(menu, Localization.L("Move to Recycle Bin"), () =>
            {
                var rect = GetVisualScreenDip(cardVisual);
                FlightOverlay.FallCard(cardVisual, rect, Line.TiltFor(note.FilePath));
                _line.TakeDownNotes([note], sendToRecycleBin: true);
            });

            var leftNeighbours = _line.Neighbours(note.FilePath, toTheLeft: true);
            AddMenuItem(menu, Localization.L("Take down everything to the left"),
                () => _line.TakeDownNeighbours(note.FilePath, toTheLeft: true),
                enabled: leftNeighbours.Count > 0);

            var rightNeighbours = _line.Neighbours(note.FilePath, toTheLeft: false);
            AddMenuItem(menu, Localization.L("Take down everything to the right"),
                () => _line.TakeDownNeighbours(note.FilePath, toTheLeft: false),
                enabled: rightNeighbours.Count > 0);
        }

        return menu;
    }

    private static void AddMenuItem(ContextMenu menu, string header, Action onClick, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    private void DiscardSingleCardWithFall(Note note, UIElement cardVisual)
    {
        var rect = GetVisualScreenDip(cardVisual);
        FlightOverlay.FallCard(cardVisual, rect, Line.TiltFor(note.FilePath));
        _line.TakeDownNotes([note]);
    }

    private void LaunchFlyToCursorFor(Note note)
    {
        if (!IsVisible || _line.BasketOpen) return;
        if (!PaperCardView.TryFindCardVisual(LineCanvas, note.FilePath, out var cardEl) || cardEl is null) return;
        var fromDip = GetVisualScreenDip(cardEl);
        var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
        double scale = _activeMonitor.DpiScale > 0 ? _activeMonitor.DpiScale : 1.0;
        FlightOverlay.FlyToCursor(cardEl, fromDip, new Point(cx / scale, cy / scale));
    }

    private Rect GetVisualScreenDip(UIElement visual)
    {
        var rel = visual.TranslatePoint(new Point(0, 0), this);
        double w = visual is FrameworkElement fe && fe.ActualWidth > 0 ? fe.ActualWidth : ScreenPlacement.CardWidth;
        double h = visual is FrameworkElement fe2 && fe2.ActualHeight > 0 ? fe2.ActualHeight : ScreenPlacement.CardHeight;
        return new Rect(Left + rel.X, Top + rel.Y, w, h);
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

        BasketChipsBar.Children.Clear();
        AddKindChip(KindFilter.All, Localization.L("All"), _line.BasketMatchCount, ThemePalette.ChromeSubText(isDark), isDark);
        AddKindChip(KindFilter.Text, Localization.L("Text"), _line.BasketKindCounts.GetValueOrDefault(KindFilter.Text), ThemePalette.TagColor(ItemKind.Text), isDark);
        AddKindChip(KindFilter.Link, Localization.L("Links"), _line.BasketKindCounts.GetValueOrDefault(KindFilter.Link), ThemePalette.TagColor(ItemKind.Link), isDark);
        AddKindChip(KindFilter.Image, Localization.L("Images"), _line.BasketKindCounts.GetValueOrDefault(KindFilter.Image), ThemePalette.TagColor(ItemKind.Image), isDark);
        AddKindChip(KindFilter.File, Localization.L("Files"), _line.BasketKindCounts.GetValueOrDefault(KindFilter.File), ThemePalette.TagColor(ItemKind.File), isDark);

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
                    _line.Theme,
                    picked,
                    isDark,
                    onSelect: r =>
                    {
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
            Background = ThemePalette.Brush(active ? ThemePalette.PickedColor(_line.Theme) : ThemePalette.ChipIdleFill(isDark)),
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
        _lastKeyUtc = DateTime.UtcNow;
        _zoomController.Hide();
        _scrollBasketToPicked = true;

        // 捕获即将被 Ctrl+Backspace / Delete 取下的卡片视觉快照，以便在数据移除前先启动下坠动画
        bool isTakeDownKey =
            (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Back) ||
            (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Delete && string.IsNullOrEmpty(QueryBox.Text));
        if (isTakeDownKey && !_line.BasketOpen && _line.Picked is { } aboutToFall &&
            PaperCardView.TryFindCardVisual(LineCanvas, aboutToFall.FilePath, out var fallEl) && fallEl is not null)
        {
            FlightOverlay.FallCard(fallEl, GetVisualScreenDip(fallEl), Line.TiltFor(aboutToFall.FilePath));
        }

        bool composing = e.Key == Key.ImeProcessed || e.Key == Key.DeadCharProcessed || HasActiveImeCompositionString();
        bool caretAtEnd = QueryBox.SelectionLength == 0 && QueryBox.CaretIndex == QueryBox.Text.Length;

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
            },
            canUndoQuery: !string.IsNullOrEmpty(QueryBox.Text) && QueryBox.CanUndo,
            caretAtEnd: caretAtEnd);

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
        // 飞向指针动画与立即粘贴并行触发：粘贴在第 0 帧立刻执行，绝不等动画
        LaunchFlyToCursorFor(selected[0]);
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
                _line.Use(selected[i].FilePath, stamp.AddMilliseconds(groupOffset: selected.Count - 1 - i));
            }
            _motion.TriggerHop(selected[0].FilePath);
            FlashCopiedFeedback(selected[0].FilePath);
        }
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _lastKeyUtc = DateTime.UtcNow;
        PlaceholderText.Visibility = string.IsNullOrEmpty(QueryBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _line.Query = QueryBox.Text;
    }

    private void NoticeBanner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _onRequestChangeShortcut();
    }

    private void SetNoActivateStyle(bool noActivate)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GwlExStyle);
        int next = noActivate
            ? (ex | WsExNoActivate | WsExToolWindow)
            : ((ex & ~WsExNoActivate) | WsExToolWindow);
        if (next != ex)
        {
            SetWindowLong(hwnd, GwlExStyle, next);
        }
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
        var sb = new StringBuilder(64);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        if (len <= 0) return false;
        var cls = sb.ToString();
        return string.Equals(cls, "Shell_TrayWnd", StringComparison.Ordinal) ||
               string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.Ordinal);
    }

    private static bool IsInChecksRunner() =>
        string.Equals(Environment.GetEnvironmentVariable(DataFolder.ChecksRunnerEnv), "1", StringComparison.Ordinal);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionString(IntPtr hIMC, int dwIndex, IntPtr lpBuf, int dwBufLen);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
}

file static class DateTimeExtensions
{
    public static DateTime AddMilliseconds(this DateTime dt, int groupOffset) =>
        dt.AddMilliseconds((double)groupOffset);
}
