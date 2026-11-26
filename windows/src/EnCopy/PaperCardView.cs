using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;

namespace EnCopy;

/// <summary>
/// 延迟区分鼠标单击复制与双击贴回，防止 Click1 立即挪回绳头重绘绳子而导致 Click2 落空（P1 竞态防护）。
/// </summary>
public sealed class CardClickCoordinator
{
    private readonly Action<Note, bool> _onSingleClick;
    private readonly Action<Note> _onDoubleClick;
    private readonly Action _scheduleTimer;
    private readonly Action _cancelTimer;
    private Note? _pendingNote;

    public CardClickCoordinator(
        Action<Note, bool> onSingleClick,
        Action<Note> onDoubleClick,
        Action scheduleTimer,
        Action cancelTimer)
    {
        _onSingleClick = onSingleClick;
        _onDoubleClick = onDoubleClick;
        _scheduleTimer = scheduleTimer;
        _cancelTimer = cancelTimer;
    }

    public void HandleClick(Note note, int clickCount, bool extendSelection)
    {
        if (extendSelection)
        {
            Cancel();
            _onSingleClick(note, true);
            return;
        }

        if (clickCount >= 2)
        {
            var target = _pendingNote is not null &&
                string.Equals(_pendingNote.FilePath, note.FilePath, StringComparison.OrdinalIgnoreCase)
                ? _pendingNote
                : note;
            Cancel();
            _onDoubleClick(target);
            return;
        }

        Cancel();
        _pendingNote = note;
        _scheduleTimer();
    }

    public void FlushPendingSingleClick()
    {
        var pending = _pendingNote;
        _pendingNote = null;
        if (pending is not null)
        {
            _onSingleClick(pending, false);
        }
    }

    public void Cancel()
    {
        _cancelTimer();
        _pendingNote = null;
    }
}

public sealed record CardInteractionCallbacks(
    Action<Note, int, bool> OnMouseClick,
    Action<Note?>? OnHoverChanged = null,
    Action<Note, UIElement>? OnStartDrag = null,
    Action<Note, UIElement>? OnDiscardCross = null,
    Action<Note>? OnHangBackHeld = null,
    Func<Note, UIElement, ContextMenu>? BuildContextMenu = null);

/// <summary>
/// 渲染六套主题下的五种纸张卡片、六色种类标签、主题夹子、绳索、号码贴纸与篮子长条行（PaperStrip）。
/// </summary>
public static class PaperCardView
{
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Courier New");
    private static readonly FontFamily SansFont = new("Microsoft YaHei UI, Segoe UI");
    private static readonly Dictionary<string, BitmapImage?> ThumbCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CardMotionHandle(
        string FilePath,
        double BaseTilt,
        double CenterX,
        FrameworkElement Host,
        RotateTransform Rotate,
        TranslateTransform HopTranslate,
        ScaleTransform PegScale);

    private sealed record RopeMotionHandles(
        QuadraticBezierSegment RopeCurve,
        UIElement? EmptyHint,
        UIElement? Knot,
        double? KnotX,
        System.Windows.Shapes.Line? BasketCord,
        double BasketHandleX,
        IReadOnlyList<CardMotionHandle> Cards);

    public static void ApplyMotionTransforms(Canvas canvas, double canvasWidth, MotionEngine motion)
    {
        if (canvas.Tag is not RopeMotionHandles handles) return;
        double sagScale = motion.RopeSagScale;
        double top = ScreenPlacement.RopeTop;
        handles.RopeCurve.Point1 = new Point(canvasWidth / 2.0, top + 2.0 * ScreenPlacement.RopeSag(canvasWidth) * sagScale);

        if (handles.EmptyHint is not null)
        {
            Canvas.SetTop(handles.EmptyHint, ScreenPlacement.RopeY(canvasWidth / 2.0, canvasWidth, sagScale) + 28);
        }

        if (handles.Knot is not null && handles.KnotX is { } kx)
        {
            Canvas.SetTop(handles.Knot, ScreenPlacement.RopeY(kx, canvasWidth, sagScale) - 4);
        }

        if (handles.BasketCord is not null)
        {
            handles.BasketCord.Y1 = ScreenPlacement.RopeY(handles.BasketHandleX, canvasWidth, sagScale);
        }

        foreach (var card in handles.Cards)
        {
            double ropeY = ScreenPlacement.RopeY(card.CenterX, canvasWidth, sagScale);
            Canvas.SetTop(card.Host, Math.Max(0.0, ropeY - 11.0));
            card.Rotate.Angle = card.BaseTilt + motion.GetCardSway(card.FilePath);
            card.HopTranslate.Y = motion.GetCardHop(card.FilePath);
            double ps = motion.GetPegScale(card.FilePath);
            card.PegScale.ScaleX = ps;
            card.PegScale.ScaleY = ps;
        }
    }

    public static UIElement BuildRopeAndCards(
        Canvas canvas,
        double canvasWidth,
        Line line,
        bool isDark,
        CardInteractionCallbacks callbacks,
        Action onBasketHandleClick,
        MotionEngine? motion = null,
        bool showBasketHandle = true)
    {
        canvas.Children.Clear();
        canvas.Width = canvasWidth;
        canvas.Height = ScreenPlacement.LineBandHeight;

        var theme = line.Theme;
        double sagScale = motion?.RopeSagScale ?? 1.0;
        var ropeCurve = DrawRope(canvas, canvasWidth, isDark, theme, sagScale);

        UIElement? emptyHint = null;
        UIElement? knotElement = null;
        double? recordedKnotX = null;
        System.Windows.Shapes.Line? basketCord = null;
        double basketHandleX = canvasWidth - 96.0;
        var cardHandles = new List<CardMotionHandle>();

        var shown = line.ShownItems;
        int count = shown.Count;
        int pinnedInShown = line.IsSearching || line.KindFilter != KindFilter.All
            ? 0
            : shown.Count(n => n.Pinned);

        if (count == 0)
        {
            var hint = BuildEmptyHint(line.IsSearching || line.KindFilter != KindFilter.All, isDark);
            hint.Measure(new Size(400, 60));
            Canvas.SetLeft(hint, (canvasWidth - hint.DesiredSize.Width) / 2.0);
            Canvas.SetTop(hint, ScreenPlacement.RopeY(canvasWidth / 2.0, canvasWidth, sagScale) + 28);
            canvas.Children.Add(hint);
            emptyHint = hint;
        }
        else
        {
            bool scrolling = line.KeepOnLine is not null && count > line.VisibleCapacity;
            bool anchorPinned = scrolling && pinnedInShown > 0 && pinnedInShown < line.VisibleCapacity - 1;
            double firstSlotX = scrolling
                ? canvasWidth / 2.0 - (line.VisibleCapacity - 1) * ScreenPlacement.CardSpacing / 2.0
                : 0.0;
            int gaps = Math.Max(count - 1, 0);
            double fittedStep = gaps > 0 ? Math.Min(ScreenPlacement.CardSpacing, Math.Max(110.0, (canvasWidth - 220.0) / gaps)) : ScreenPlacement.CardSpacing;

            double CardCenterX(int index)
            {
                if (scrolling)
                {
                    bool stays = anchorPinned && index < pinnedInShown;
                    return firstSlotX + index * ScreenPlacement.CardSpacing - (stays ? 0.0 : line.ScrollOffset);
                }
                return canvasWidth / 2.0 - gaps * fittedStep / 2.0 + index * fittedStep;
            }

            double? knotX = null;
            if (pinnedInShown > 0)
            {
                if (anchorPinned)
                {
                    knotX = firstSlotX + (pinnedInShown - 0.5) * ScreenPlacement.CardSpacing;
                }
                else if (pinnedInShown < count)
                {
                    knotX = (CardCenterX(pinnedInShown - 1) + CardCenterX(pinnedInShown)) / 2.0;
                }
                else
                {
                    knotX = CardCenterX(pinnedInShown - 1) + (scrolling ? ScreenPlacement.CardSpacing : fittedStep) / 2.0;
                }
            }

            if (knotX is { } kx && kx > 10 && kx < canvasWidth - 10)
            {
                var knot = BuildKnot(isDark, theme);
                Canvas.SetLeft(knot, kx - 6);
                Canvas.SetTop(knot, ScreenPlacement.RopeY(kx, canvasWidth, sagScale) - 4);
                Panel.SetZIndex(knot, 28);
                canvas.Children.Add(knot);
                knotElement = knot;
                recordedKnotX = kx;
            }

            for (int i = 0; i < count; i++)
            {
                var note = shown[i];
                double cx = CardCenterX(i);
                if (cx < -ScreenPlacement.CardWidth || cx > canvasWidth + ScreenPlacement.CardWidth)
                {
                    continue;
                }
                if (anchorPinned && i >= pinnedInShown && knotX is { } kxBound && cx < kxBound + ScreenPlacement.CardWidth * 0.45)
                {
                    continue;
                }

                var fullPath = System.IO.Path.GetFullPath(note.FilePath);
                double ropeY = ScreenPlacement.RopeY(cx, canvasWidth, sagScale);
                bool isPicked = line.Picked is not null &&
                    string.Equals(line.Picked.FilePath, note.FilePath, StringComparison.OrdinalIgnoreCase);
                bool isSelected = isPicked || line.SelectedPaths.Contains(fullPath);
                bool isCopied = string.Equals(line.CopiedFeedbackPath, note.FilePath, StringComparison.OrdinalIgnoreCase);
                bool isHeld = line.HeldPaths.Contains(fullPath);
                int? stickerNumber = i < 9 ? i + 1 : null;

                double extraSway = motion?.GetCardSway(note.FilePath) ?? 0.0;
                double hopOffset = motion?.GetCardHop(note.FilePath) ?? 0.0;
                double pegScale = motion?.GetPegScale(note.FilePath) ?? 1.0;

                var (cardVisual, rotateTx, hopTx, pegScaleTx) = BuildPeggedCard(
                    note,
                    theme,
                    isSelected,
                    isPicked,
                    isCopied,
                    isHeld,
                    isDark,
                    line.ShowsSourceApp,
                    stickerNumber,
                    extraSway,
                    hopOffset,
                    pegScale,
                    callbacks);

                cardVisual.Tag = fullPath;

                const double hostWidth = ScreenPlacement.CardWidth + 16;
                Canvas.SetLeft(cardVisual, cx - hostWidth / 2.0);
                Canvas.SetTop(cardVisual, Math.Max(0.0, ropeY - 11.0));
                Panel.SetZIndex(cardVisual, i < pinnedInShown ? 25 : (isPicked ? 20 : 2));
                canvas.Children.Add(cardVisual);
                cardHandles.Add(new CardMotionHandle(note.FilePath, Line.TiltFor(note.FilePath), cx, cardVisual, rotateTx, hopTx, pegScaleTx));
            }
        }

        if (showBasketHandle && (line.BasketCount > 0 || line.BasketOpen))
        {
            double hx = basketHandleX;
            double topY = ScreenPlacement.RopeY(hx, canvasWidth, sagScale);
            double hy = ScreenPlacement.LineBandHeight - 36.0;

            var cord = new System.Windows.Shapes.Line
            {
                X1 = hx,
                Y1 = topY,
                X2 = hx,
                Y2 = hy,
                Stroke = ThemePalette.Brush(RopeMainForTheme(theme, isDark)),
                StrokeThickness = 1.3,
                StrokeDashArray = [3, 2],
                Opacity = 0.78,
                IsHitTestVisible = false
            };
            Canvas.SetZIndex(cord, 4);
            canvas.Children.Add(cord);
            basketCord = cord;

            var handle = BuildBasketHandle(line.BasketCount, line.BasketOpen, isDark, theme, onBasketHandleClick);
            handle.Measure(new Size(200, 40));
            Canvas.SetLeft(handle, hx - handle.DesiredSize.Width / 2.0);
            Canvas.SetTop(handle, hy);
            Panel.SetZIndex(handle, 25);
            canvas.Children.Add(handle);
        }

        canvas.Tag = new RopeMotionHandles(ropeCurve, emptyHint, knotElement, recordedKnotX, basketCord, basketHandleX, cardHandles);
        return canvas;
    }

    public static bool TryFindCardVisual(Canvas canvas, string filePath, out FrameworkElement? cardElement)
    {
        var full = System.IO.Path.GetFullPath(filePath);
        foreach (UIElement child in canvas.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is string tag &&
                string.Equals(tag, full, StringComparison.OrdinalIgnoreCase))
            {
                cardElement = fe;
                return true;
            }
        }
        cardElement = null;
        return false;
    }

    public static UIElement BuildKindTag(ItemKind kind, double scale = 1.0)
    {
        var label = kind switch
        {
            ItemKind.Code => Localization.L("Code"),
            ItemKind.Link => Localization.L("Link"),
            ItemKind.Image => Localization.L("Image"),
            ItemKind.Video => Localization.L("Video"),
            ItemKind.File => Localization.L("File"),
            _ => Localization.L("Text")
        };

        var glyph = kind switch
        {
            ItemKind.Code => "</>",
            ItemKind.Link => "↗",
            ItemKind.Image => "▣",
            ItemKind.Video => "▶",
            ItemKind.File => "▤",
            _ => "≡"
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = glyph,
            FontSize = 8.5 * scale,
            FontWeight = FontWeights.Bold,
            FontFamily = MonoFont,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 3 * scale, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9.5 * scale,
            FontWeight = FontWeights.SemiBold,
            FontFamily = SansFont,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });

        return new Border
        {
            Background = ThemePalette.Brush(ThemePalette.TagColor(kind)),
            CornerRadius = new CornerRadius(9 * scale),
            Padding = new Thickness(6 * scale, 2 * scale, 6 * scale, 2 * scale),
            Child = stack,
            IsHitTestVisible = false
        };
    }

    public static UIElement BuildBasketRow(
        BasketRow row,
        LineTheme theme,
        bool isPicked,
        bool isDark,
        Action<BasketRow> onSelect,
        Action<BasketRow> onActivate,
        bool showSourceApp = true,
        IReadOnlyList<string>? stackFiles = null,
        bool isStackExpanded = false,
        Action? onToggleStack = null)
    {
        _ = isDark;
        var note = row.Note;
        var cardBg = BuildPaperStrip(note.Paper, theme);
        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, note.Paper));
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, note.Paper));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tagHolder = new ContentControl
        {
            Content = BuildKindTag(note.Kind, scale: 0.96),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(tagHolder, 0);
        grid.Children.Add(tagHolder);

        var leading = BuildBasketLeadingPreview(note);
        if (leading is not null)
        {
            Grid.SetColumn(leading, 1);
            grid.Children.Add(leading);
        }

        var textPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0)
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(note.SummaryLine) ? note.FilePath : note.SummaryLine,
            FontSize = 12.5,
            FontWeight = note.IsCode ? FontWeights.Normal : FontWeights.SemiBold,
            FontFamily = ThemePalette.CardFont(theme, code: note.IsCode || note.Kind == ItemKind.Link),
            Foreground = ink,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var detail = BasketDetailText(note);
        if (!string.IsNullOrEmpty(detail))
        {
            textPanel.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11,
                FontFamily = ThemePalette.CardFont(theme, code: note.Kind != ItemKind.Text),
                Foreground = soft,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }
        Grid.SetColumn(textPanel, 2);
        grid.Children.Add(textPanel);

        var rightPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (stackFiles is { Count: > 1 } && onToggleStack is not null)
        {
            var stackBtn = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemePalette.Brush(Color.FromArgb(0x1C, 0x00, 0x00, 0x00)),
                Padding = new Thickness(7, 2.5, 7, 2.5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                ToolTip = Localization.L("Show the files"),
                Child = new TextBlock
                {
                    Text = $"{stackFiles.Count} {(isStackExpanded ? "▴" : "▾")}",
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = MonoFont,
                    Foreground = ink
                }
            };
            stackBtn.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                onToggleStack();
            };
            rightPanel.Children.Add(stackBtn);
        }

        if (note.IsMovedAway)
        {
            rightPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = ThemePalette.Brush(Color.FromArgb(0x28, 0x6A, 0x64, 0x5C)),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Localization.L("Moved away"),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = soft
                }
            });
        }

        if (showSourceApp && !string.IsNullOrWhiteSpace(note.DisplayAppName))
        {
            if (SourceApp.GetAppIcon(note.AppName) is { } appIcon)
            {
                rightPanel.Children.Add(new Image
                {
                    Source = appIcon,
                    Width = 15,
                    Height = 15,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            rightPanel.Children.Add(new TextBlock
            {
                Text = note.DisplayAppName,
                FontSize = 10.5,
                Foreground = soft,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        rightPanel.Children.Add(new TextBlock
        {
            Text = FormatBasketRowTime(row.UsedLocal),
            FontSize = 10.5,
            FontFamily = MonoFont,
            Foreground = soft,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(rightPanel, 3);
        grid.Children.Add(rightPanel);

        var inner = new Grid { Margin = new Thickness(12, 7, 12, 7) };
        inner.Children.Add(grid);

        double corner = Math.Max(3.0, ThemePalette.CardCorner(theme));
        double baseOpacity = note.IsMovedAway
            ? (isPicked ? 0.62 : 0.46)
            : (isPicked ? 1.0 : 0.88);
        var container = new Grid
        {
            Margin = new Thickness(10, 3, 10, 3),
            Cursor = note.IsMovedAway ? Cursors.Arrow : Cursors.Hand,
            Opacity = baseOpacity,
            RenderTransform = new TranslateTransform(isPicked ? 4.0 : 0.0, 0.0),
            Effect = isPicked
                ? new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.28, Color = Colors.Black }
                : new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.14, Color = Colors.Black }
        };
        container.Children.Add(cardBg);
        container.Children.Add(inner);

        if (isPicked)
        {
            container.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(corner),
                BorderBrush = ThemePalette.Brush(ThemePalette.PickedColor(theme)),
                BorderThickness = new Thickness(ThemePalette.PickedWidth(theme)),
                IsHitTestVisible = false
            });
        }

        container.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount >= 2)
            {
                onActivate(row);
            }
            else
            {
                onSelect(row);
            }
        };

        return container;
    }

    public static UIElement BuildStackFileRow(
        string filePath,
        LineTheme theme,
        bool isPicked,
        bool isDark,
        Action onSelect,
        Action onActivate)
    {
        var icon = SourceApp.GetFileIcon(filePath, large: false);
        var fileName = FileList.NameOf(filePath);
        var dirPath = System.IO.Path.GetDirectoryName(filePath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) ?? string.Empty;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (icon is not null)
        {
            var img = new Image
            {
                Source = icon,
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(img, 0);
            grid.Children.Add(img);
        }

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = fileName,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemePalette.Brush(ThemePalette.ChromeText(isDark)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(dirPath))
        {
            stack.Children.Add(new TextBlock
            {
                Text = dirPath,
                FontSize = 10,
                FontFamily = MonoFont,
                Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(isDark)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        var border = new Border
        {
            Margin = new Thickness(74, 2, 14, 2),
            Padding = new Thickness(12, 5, 12, 5),
            CornerRadius = new CornerRadius(6),
            Background = ThemePalette.Brush(isPicked
                ? (isDark ? Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00))
                : (isDark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0x00, 0x00, 0x00))),
            BorderBrush = isPicked ? ThemePalette.Brush(ThemePalette.PickedColor(theme)) : null,
            BorderThickness = new Thickness(isPicked ? 2.0 : 0.0),
            Cursor = Cursors.Hand,
            Child = grid
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount >= 2) onActivate();
            else onSelect();
        };
        return border;
    }

    private static (FrameworkElement Host, RotateTransform Rotate, TranslateTransform Hop, ScaleTransform PegScale) BuildPeggedCard(
        Note note,
        LineTheme theme,
        bool isSelected,
        bool isPicked,
        bool isCopied,
        bool isHeld,
        bool isDark,
        bool showSourceApp,
        int? stickerNumber,
        double extraSway,
        double hopOffset,
        double pegScale,
        CardInteractionCallbacks callbacks)
    {
        double tilt = Line.TiltFor(note.FilePath) + extraSway;
        double scale = isPicked ? 1.04 : 1.0;

        var rotateTx = new RotateTransform(tilt);
        var scaleTx = new ScaleTransform(scale, scale);
        var hopTx = new TranslateTransform(0.0, hopOffset);

        var host = new Grid
        {
            Width = ScreenPlacement.CardWidth + 16,
            Background = null,
            RenderTransformOrigin = new Point(0.5, 0.08),
            RenderTransform = new TransformGroup
            {
                Children =
                {
                    rotateTx,
                    scaleTx,
                    hopTx
                }
            }
        };

        var cardSurface = BuildPaperCardBody(note, theme, isSelected, isDark, showSourceApp);
        cardSurface.Margin = new Thickness(8, 16, 8, 4);
        cardSurface.Cursor = note.IsMovedAway ? Cursors.Arrow : Cursors.Hand;
        cardSurface.Opacity = isHeld ? 0.28 : (note.IsMovedAway ? 0.56 : 1.0);
        if (isPicked)
        {
            cardSurface.Margin = new Thickness(8, 20, 8, 0);
        }

        Point? dragOrigin = null;
        bool dragInitiated = false;

        cardSurface.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (isHeld)
            {
                callbacks.OnHangBackHeld?.Invoke(note);
                return;
            }
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (shift || e.ClickCount >= 2)
            {
                dragOrigin = null;
                callbacks.OnMouseClick(note, e.ClickCount, shift);
                return;
            }
            dragOrigin = e.GetPosition(host);
            dragInitiated = false;
            callbacks.OnMouseClick(note, 1, false);
        };

        cardSurface.MouseMove += (_, e) =>
        {
            if (isHeld || dragOrigin is not { } start || dragInitiated) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                dragOrigin = null;
                return;
            }
            var cur = e.GetPosition(host);
            double dx = cur.X - start.X;
            double dy = cur.Y - start.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > 4.5)
            {
                dragInitiated = true;
                dragOrigin = null;
                callbacks.OnStartDrag?.Invoke(note, host);
            }
        };

        cardSurface.MouseLeftButtonUp += (_, _) =>
        {
            dragOrigin = null;
            dragInitiated = false;
        };

        if (!isHeld && callbacks.BuildContextMenu is not null)
        {
            cardSurface.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var menu = callbacks.BuildContextMenu(note, host);
                menu.PlacementTarget = cardSurface;
                menu.IsOpen = true;
            };
        }

        host.Children.Add(cardSurface);

        // 左上角种类标签
        var kindTag = BuildKindTag(note.Kind, scale: 0.90);
        if (kindTag is FrameworkElement tagEl)
        {
            tagEl.HorizontalAlignment = HorizontalAlignment.Left;
            tagEl.VerticalAlignment = VerticalAlignment.Top;
            tagEl.Margin = new Thickness(12, isPicked ? 11 : 7, 0, 0);
            tagEl.Opacity = isHeld ? 0.35 : 1.0;
            Panel.SetZIndex(tagEl, 6);
            host.Children.Add(tagEl);
        }

        // 主题夹子（居中压在绳线上，支持新挂卡片张开夹紧缩放）
        var peg = BuildThemePeg(theme, note.Pinned);
        peg.HorizontalAlignment = HorizontalAlignment.Center;
        peg.VerticalAlignment = VerticalAlignment.Top;
        var pegScaleTx = new ScaleTransform(pegScale, pegScale);
        peg.RenderTransformOrigin = new Point(0.5, 0.25);
        peg.RenderTransform = pegScaleTx;
        Panel.SetZIndex(peg, 8);
        host.Children.Add(peg);

        // 右上角号码贴纸（1-9）
        Border? sticker = null;
        if (stickerNumber is { } num && !isHeld)
        {
            var pickedCol = ThemePalette.PickedColor(theme);
            sticker = new Border
            {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(8.5),
                Background = ThemePalette.Brush(isSelected ? pickedCol : Color.FromArgb(0xE8, 0xFF, 0xFD, 0xF7)),
                BorderBrush = ThemePalette.Brush(isSelected ? pickedCol : Color.FromArgb(0x55, 0x62, 0x52, 0x3D)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, isPicked ? 11 : 7, 12, 0),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = num.ToString(),
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    FontFamily = MonoFont,
                    Foreground = isSelected ? Brushes.White : ThemePalette.Brush(ThemePalette.PaperInk),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Panel.SetZIndex(sticker, 7);
            host.Children.Add(sticker);
        }

        // 右上角悬停取下 × 按钮（非收藏且未拿在手上时悬停露出）
        Border? discardCross = null;
        if (!note.Pinned && !isHeld && callbacks.OnDiscardCross is not null)
        {
            discardCross = new Border
            {
                Width = 19,
                Height = 19,
                CornerRadius = new CornerRadius(9.5),
                Background = ThemePalette.Brush(Color.FromArgb(0xE4, 0x2B, 0x24, 0x20)),
                BorderBrush = ThemePalette.Brush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0.8),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, isPicked ? 10 : 6, 11, 0),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed,
                ToolTip = Localization.L("Take down"),
                Child = new TextBlock
                {
                    Text = "×",
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                }
            };
            discardCross.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                callbacks.OnDiscardCross(note, host);
            };
            Panel.SetZIndex(discardCross, 12);
            host.Children.Add(discardCross);
        }

        host.MouseEnter += (_, _) =>
        {
            if (discardCross is not null)
            {
                discardCross.Visibility = Visibility.Visible;
                if (sticker is not null) sticker.Visibility = Visibility.Collapsed;
            }
            if (!isHeld) callbacks.OnHoverChanged?.Invoke(note);
        };
        host.MouseLeave += (_, _) =>
        {
            if (discardCross is not null)
            {
                discardCross.Visibility = Visibility.Collapsed;
                if (sticker is not null) sticker.Visibility = Visibility.Visible;
            }
            callbacks.OnHoverChanged?.Invoke(null);
        };

        // 单击复制反馈胶囊
        if (isCopied)
        {
            var copiedPill = new Border
            {
                Background = ThemePalette.Brush(ThemePalette.ChromeFill(isDark)),
                BorderBrush = ThemePalette.Brush(ThemePalette.PickedColor(theme)),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(9, 3, 9, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, -10),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "✓ " + Localization.L("Copied"),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemePalette.Brush(ThemePalette.ChromeText(isDark))
                }
            };
            Panel.SetZIndex(copiedPill, 15);
            host.Children.Add(copiedPill);
        }

        return (host, rotateTx, hopTx, pegScaleTx);
    }

    private static Grid BuildPaperCardBody(Note note, LineTheme theme, bool isSelected, bool isDark, bool showSourceApp = true)
    {
        double w = ScreenPlacement.CardWidth;
        double h = ScreenPlacement.CardHeight;

        Effect shadow = theme switch
        {
            LineTheme.Pixel => new DropShadowEffect
            {
                BlurRadius = 0,
                ShadowDepth = isSelected ? 5 : 4,
                Direction = 315,
                Opacity = 0.85,
                Color = Colors.Black
            },
            LineTheme.Neon => new DropShadowEffect
            {
                BlurRadius = isSelected ? 12 : 7,
                ShadowDepth = 0,
                Opacity = isSelected ? 0.55 : 0.34,
                Color = note.Paper == PaperKind.Tag ? Color.FromRgb(0xFF, 0x4D, 0xCC) : Color.FromRgb(0x33, 0xF2, 0xFF)
            },
            LineTheme.Minimal => new DropShadowEffect
            {
                BlurRadius = isSelected ? 12 : 8,
                ShadowDepth = isSelected ? 4 : 2.5,
                Opacity = isSelected ? 0.16 : 0.09,
                Color = Colors.Black
            },
            _ => new DropShadowEffect
            {
                BlurRadius = isSelected ? 14 : 10,
                ShadowDepth = isSelected ? 6 : 4,
                Opacity = isSelected ? 0.30 : 0.20,
                Color = Colors.Black
            }
        };

        var root = new Grid
        {
            Width = w,
            Height = h,
            Effect = shadow
        };

        if (theme == LineTheme.Paper)
        {
            switch (note.Paper)
            {
                case PaperKind.Tractor:
                    root.Children.Add(BuildTractorCardSurface(w, h));
                    root.Children.Add(BuildTractorContent(note, theme, showSourceApp));
                    break;

                case PaperKind.Tag:
                    root.Children.Add(BuildTagCardSurface(w, h));
                    root.Children.Add(BuildTagContent(note, theme, showSourceApp));
                    break;

                case PaperKind.Polaroid:
                    root.Children.Add(BuildPolaroidContent(note, theme, w, h, showSourceApp));
                    break;

                case PaperKind.Folder:
                    root.Children.Add(BuildManilaCardSurface(w, h));
                    root.Children.Add(BuildManilaContent(note, theme, showSourceApp));
                    break;

                default:
                    root.Children.Add(BuildReceiptCardSurface(w, h));
                    root.Children.Add(BuildReceiptContent(note, theme, isDark, showSourceApp));
                    break;
            }
        }
        else
        {
            root.Children.Add(BuildThemedCardSurface(theme, note.Paper, w, h));
            switch (note.Paper)
            {
                case PaperKind.Tractor:
                    root.Children.Add(BuildTractorContent(note, theme, showSourceApp));
                    break;
                case PaperKind.Tag:
                    root.Children.Add(BuildTagContent(note, theme, showSourceApp));
                    break;
                case PaperKind.Polaroid:
                    root.Children.Add(BuildPolaroidContent(note, theme, w, h, showSourceApp));
                    break;
                case PaperKind.Folder:
                    root.Children.Add(BuildManilaContent(note, theme, showSourceApp));
                    break;
                default:
                    root.Children.Add(BuildReceiptContent(note, theme, isDark, showSourceApp));
                    break;
            }
        }

        if (isSelected)
        {
            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(ThemePalette.CardCorner(theme)),
                BorderBrush = ThemePalette.Brush(ThemePalette.PickedColor(theme)),
                BorderThickness = new Thickness(ThemePalette.PickedWidth(theme)),
                IsHitTestVisible = false
            });
        }

        return root;
    }

    public static UIElement BuildThemedCardSurface(LineTheme theme, PaperKind paper, double w, double h)
    {
        double corner = ThemePalette.CardCorner(theme);
        var fill = ThemePalette.CardFill(theme, paper);
        var edge = ThemePalette.CardEdge(theme, paper);

        var grid = new Grid { Width = w, Height = h, ClipToBounds = true };
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(corner),
            Background = ThemePalette.Brush(fill)
        });

        if (theme == LineTheme.Skeuo)
        {
            var sheen = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF), 0.0),
                    new(Colors.Transparent, 0.55),
                    new(Color.FromArgb(0x14, 0x00, 0x00, 0x00), 1.0)
                },
                new Point(0, 0),
                new Point(0, 1));
            grid.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(corner),
                Background = sheen,
                IsHitTestVisible = false
            });
        }
        else if (theme == LineTheme.Pixel)
        {
            grid.Children.Add(new Border
            {
                Height = 3,
                VerticalAlignment = VerticalAlignment.Top,
                Background = ThemePalette.Brush(paper == PaperKind.Tractor
                    ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                IsHitTestVisible = false
            });
        }
        else if (theme == LineTheme.Washi)
        {
            var dots = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
            var dotBrush = ThemePalette.Brush(Color.FromArgb(0x55, 0x8C, 0x80, 0x73));
            for (double y = 9; y < h - 4; y += 10)
            {
                for (double x = 9; x < w - 4; x += 10)
                {
                    var d = new Ellipse { Width = 1.5, Height = 1.5, Fill = dotBrush };
                    Canvas.SetLeft(d, x);
                    Canvas.SetTop(d, y);
                    dots.Children.Add(d);
                }
            }
            grid.Children.Add(dots);
        }

        if (edge is { } e)
        {
            grid.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(corner),
                BorderBrush = ThemePalette.Brush(e.Color),
                BorderThickness = new Thickness(e.Width),
                IsHitTestVisible = false
            });
        }

        return grid;
    }

    private static UIElement BuildReceiptCardSurface(double w, double h)
    {
        double toothW = 6.0;
        double toothH = 4.5;
        var fig = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true };
        fig.Segments.Add(new LineSegment(new Point(w, 0), true));
        fig.Segments.Add(new LineSegment(new Point(w, h - toothH), true));

        double x = w;
        bool down = true;
        while (x > 0.1)
        {
            x = Math.Max(0.0, x - toothW);
            fig.Segments.Add(new LineSegment(new Point(x, down ? h : h - toothH), true));
            down = !down;
        }

        return new WpfPath
        {
            Data = new PathGeometry([fig]),
            Fill = ThemePalette.Brush(ThemePalette.ReceiptFill),
            Stroke = ThemePalette.Brush(Color.FromArgb(0x28, 0x3D, 0x32, 0x24)),
            StrokeThickness = 0.8
        };
    }

    private static UIElement BuildReceiptContent(Note note, LineTheme theme, bool isDark, bool showSourceApp)
    {
        _ = isDark;
        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, PaperKind.Receipt));
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, PaperKind.Receipt));
        var link = ThemePalette.Brush(theme == LineTheme.Neon ? Color.FromRgb(0x33, 0xF2, 0xFF) : ThemePalette.MarkdownLinkInk);
        var font = ThemePalette.CardFont(theme, code: false);

        var grid = new Grid { Margin = new Thickness(10, 12, 10, 11) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var contentStack = new StackPanel { VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
        if (!string.IsNullOrEmpty(note.Title))
        {
            var titleBlock = new TextBlock
            {
                FontSize = string.IsNullOrEmpty(note.Body) ? 11.5 : 10.5,
                FontWeight = FontWeights.Bold,
                FontFamily = font,
                Foreground = ink,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = string.IsNullOrEmpty(note.Body) ? 64 : 34,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (note.IsMarkdown)
            {
                foreach (var inline in Markdown.BuildInlines(note.Title, titleBlock.FontSize, ink, ink, link))
                {
                    titleBlock.Inlines.Add(inline);
                }
            }
            else
            {
                titleBlock.Text = note.Title;
            }
            contentStack.Children.Add(titleBlock);
        }

        if (!string.IsNullOrEmpty(note.Body))
        {
            if (!string.IsNullOrEmpty(note.Title))
            {
                contentStack.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    Y1 = 0,
                    X2 = 128,
                    Y2 = 0,
                    Stroke = soft,
                    Opacity = 0.45,
                    StrokeThickness = 0.8,
                    StrokeDashArray = [3, 2],
                    Margin = new Thickness(0, 4, 0, 4)
                });
            }

            var bodyBlock = new TextBlock
            {
                FontSize = 8.8,
                FontFamily = font,
                Foreground = soft,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = string.IsNullOrEmpty(note.Title) ? 70 : 40,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (note.IsMarkdown)
            {
                foreach (var inline in Markdown.BuildInlines(note.Body, 8.8, ink, soft, link))
                {
                    bodyBlock.Inlines.Add(inline);
                }
            }
            else
            {
                bodyBlock.Text = note.Body;
            }
            contentStack.Children.Add(bodyBlock);
        }

        Grid.SetRow(contentStack, 0);
        grid.Children.Add(contentStack);

        var caption = BuildCardCaptionBar(note, soft, showSourceApp, centered: false, topMargin: 3);
        Grid.SetRow(caption, 1);
        grid.Children.Add(caption);

        return grid;
    }

    private static UIElement BuildTractorCardSurface(double w, double h)
    {
        var canvas = new Canvas { Width = w, Height = h, ClipToBounds = true };
        double bandH = 13.0;
        bool light = true;
        for (double y = 0; y < h; y += bandH)
        {
            var band = new Rectangle
            {
                Width = w,
                Height = Math.Min(bandH, h - y),
                Fill = ThemePalette.Brush(light ? ThemePalette.TractorLightStripe : ThemePalette.TractorDarkStripe)
            };
            Canvas.SetLeft(band, 0);
            Canvas.SetTop(band, y);
            canvas.Children.Add(band);
            light = !light;
        }

        var holeBrush = ThemePalette.Brush(Color.FromArgb(0x5E, 0x5A, 0x50, 0x3D));
        foreach (double hx in new[] { 4.5, w - 7.5 })
        {
            for (double hy = 5.0; hy < h - 4.0; hy += 13.0)
            {
                var hole = new Ellipse { Width = 3.6, Height = 3.6, Fill = holeBrush };
                Canvas.SetLeft(hole, hx);
                Canvas.SetTop(hole, hy);
                canvas.Children.Add(hole);
            }
        }

        return new Border
        {
            Width = w,
            Height = h,
            BorderBrush = ThemePalette.Brush(Color.FromArgb(0x28, 0x3A, 0x48, 0x32)),
            BorderThickness = new Thickness(0.8),
            Child = canvas
        };
    }

    private static UIElement BuildTractorContent(Note note, LineTheme theme, bool showSourceApp)
    {
        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, PaperKind.Tractor));
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, PaperKind.Tractor));
        double sidePad = theme == LineTheme.Paper ? 15 : 10;

        var grid = new Grid { Margin = new Thickness(sidePad, 11, sidePad, 8), ClipToBounds = true };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var codeBlock = new TextBlock
        {
            Text = note.Body,
            FontSize = 8.0,
            FontFamily = MonoFont,
            Foreground = ink,
            LineHeight = 12.8,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(codeBlock, 0);
        grid.Children.Add(codeBlock);

        var caption = BuildCardCaptionBar(note, soft, showSourceApp, centered: false);
        Grid.SetRow(caption, 1);
        grid.Children.Add(caption);

        return grid;
    }

    private static UIElement BuildTagCardSurface(double w, double h)
    {
        const double chamfer = 12.0;
        var fig = new PathFigure { StartPoint = new Point(chamfer, 0), IsClosed = true };
        fig.Segments.Add(new LineSegment(new Point(w - chamfer, 0), true));
        fig.Segments.Add(new LineSegment(new Point(w, chamfer), true));
        fig.Segments.Add(new LineSegment(new Point(w, h), true));
        fig.Segments.Add(new LineSegment(new Point(0, h), true));
        fig.Segments.Add(new LineSegment(new Point(0, chamfer), true));

        var grid = new Grid { Width = w, Height = h };
        grid.Children.Add(new WpfPath
        {
            Data = new PathGeometry([fig]),
            Fill = ThemePalette.Brush(ThemePalette.KraftFill),
            Stroke = ThemePalette.Brush(Color.FromArgb(0x36, 0x40, 0x30, 0x1C)),
            StrokeThickness = 0.8
        });

        var eyelet = new Grid
        {
            Width = 10,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 9, 0),
            IsHitTestVisible = false
        };
        eyelet.Children.Add(new Ellipse { Fill = ThemePalette.Brush(ThemePalette.KraftEyeletOuter) });
        eyelet.Children.Add(new Ellipse { Width = 4.6, Height = 4.6, Fill = ThemePalette.Brush(ThemePalette.KraftEyeletInner) });
        grid.Children.Add(eyelet);

        return grid;
    }

    private static UIElement BuildTagContent(Note note, LineTheme theme, bool showSourceApp)
    {
        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, PaperKind.Tag));
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, PaperKind.Tag));
        var grid = new Grid { Margin = new Thickness(11, 16, 11, 8) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = note.Title,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            FontFamily = ThemePalette.CardFont(theme, code: false),
            Foreground = ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 14, 2)
        });
        if (!string.IsNullOrEmpty(note.LinkPath))
        {
            stack.Children.Add(new TextBlock
            {
                Text = note.LinkPath,
                FontSize = 8.8,
                FontFamily = MonoFont,
                Foreground = soft,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 32,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetRow(stack, 0);
        grid.Children.Add(stack);

        var caption = BuildCardCaptionBar(note, soft, showSourceApp, centered: false);
        Grid.SetRow(caption, 1);
        grid.Children.Add(caption);

        return grid;
    }

    private static UIElement BuildPolaroidContent(Note note, LineTheme theme, double w, double h, bool showSourceApp)
    {
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, PaperKind.Polaroid));
        var outer = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(ThemePalette.CardCorner(theme)),
            Background = theme == LineTheme.Paper ? ThemePalette.Brush(ThemePalette.PolaroidFill) : Brushes.Transparent,
            BorderBrush = theme == LineTheme.Paper ? ThemePalette.Brush(Color.FromArgb(0x28, 0x3D, 0x32, 0x24)) : null,
            BorderThickness = new Thickness(theme == LineTheme.Paper ? 0.8 : 0.0),
            Padding = new Thickness(6, 6, 6, 5)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var well = new Border
        {
            Background = ThemePalette.Brush(note.Kind == ItemKind.Video || theme == LineTheme.Neon
                ? Color.FromRgb(0x22, 0x25, 0x2D)
                : Color.FromRgb(0xE8, 0xE3, 0xDA)),
            CornerRadius = new CornerRadius(Math.Max(1.0, ThemePalette.CardCorner(theme) - 2.0)),
            ClipToBounds = true
        };

        var thumb = note.Kind == ItemKind.Image ? TryLoadThumbnail(note.FilePath) : null;
        if (thumb is not null)
        {
            well.Child = new Image
            {
                Source = thumb,
                Stretch = Stretch.UniformToFill
            };
        }
        else
        {
            var badgeStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeStack.Children.Add(new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = ThemePalette.Brush(note.Kind == ItemKind.Video
                    ? Color.FromArgb(0xCC, 0xDC, 0x47, 0x4D)
                    : Color.FromArgb(0xC8, 0x8F, 0x57, 0xDC)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = note.Kind == ItemKind.Video ? "▶" : "▣",
                    FontSize = 12,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            badgeStack.Children.Add(new TextBlock
            {
                Text = note.SummaryLine,
                FontSize = 8.0,
                FontFamily = MonoFont,
                Foreground = ThemePalette.Brush(note.Kind == ItemKind.Video || theme == LineTheme.Neon ? Colors.WhiteSmoke : ThemePalette.PaperSoft),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = w - 22,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            well.Child = badgeStack;
        }

        Grid.SetRow(well, 0);
        grid.Children.Add(well);

        var caption = BuildCardCaptionBar(note, soft, showSourceApp, centered: true, topMargin: 4);
        Grid.SetRow(caption, 1);
        grid.Children.Add(caption);

        outer.Child = grid;
        return outer;
    }

    private static UIElement BuildManilaCardSurface(double w, double h)
    {
        var fig = new PathFigure { StartPoint = new Point(0, 8), IsClosed = true };
        fig.Segments.Add(new LineSegment(new Point(0, 2), true));
        fig.Segments.Add(new LineSegment(new Point(3, 0), true));
        fig.Segments.Add(new LineSegment(new Point(46, 0), true));
        fig.Segments.Add(new LineSegment(new Point(53, 7), true));
        fig.Segments.Add(new LineSegment(new Point(w, 7), true));
        fig.Segments.Add(new LineSegment(new Point(w, h), true));
        fig.Segments.Add(new LineSegment(new Point(0, h), true));

        var grid = new Grid { Width = w, Height = h };
        grid.Children.Add(new WpfPath
        {
            Data = new PathGeometry([fig]),
            Fill = ThemePalette.Brush(ThemePalette.ManilaFill),
            Stroke = ThemePalette.Brush(ThemePalette.ManilaStroke),
            StrokeThickness = 0.9
        });
        grid.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 6,
            Y1 = 15,
            X2 = w - 6,
            Y2 = 15,
            Stroke = ThemePalette.Brush(Color.FromArgb(0x55, 0x85, 0x61, 0x3D)),
            StrokeThickness = 0.8
        });
        return grid;
    }

    private static UIElement BuildManilaContent(Note note, LineTheme theme, bool showSourceApp)
    {
        var ink = ThemePalette.Brush(ThemePalette.CardInk(theme, PaperKind.Folder));
        var soft = ThemePalette.Brush(ThemePalette.CardSoft(theme, PaperKind.Folder));

        var grid = new Grid { Margin = new Thickness(10, 18, 10, 8) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var centerStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var iconTarget = note.FileTargets.FirstOrDefault() ?? note.FilePath;
        var fileIcon = SourceApp.GetFileIcon(iconTarget, large: true);
        if (fileIcon is not null && note.FileTargets.Count <= 1)
        {
            centerStack.Children.Add(new Image
            {
                Source = fileIcon,
                Width = 28,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }
        else
        {
            centerStack.Children.Add(new Border
            {
                Width = 30,
                Height = 22,
                CornerRadius = new CornerRadius(3),
                Background = ThemePalette.Brush(ThemePalette.TagColor(ItemKind.File)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5),
                Child = new TextBlock
                {
                    Text = note.FileTargets.Count > 1 ? note.FileTargets.Count.ToString() : "▤",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        centerStack.Children.Add(new TextBlock
        {
            Text = note.Title,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = ThemePalette.CardFont(theme, code: false),
            Foreground = ink,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 28,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var bodyLine = note.IsMovedAway ? Localization.L("Moved away") : note.Body;
        if (!string.IsNullOrEmpty(bodyLine))
        {
            centerStack.Children.Add(new TextBlock
            {
                Text = bodyLine,
                FontSize = 8.2,
                FontWeight = note.IsMovedAway ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = MonoFont,
                Foreground = soft,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetRow(centerStack, 0);
        grid.Children.Add(centerStack);

        var caption = BuildCardCaptionBar(note, soft, showSourceApp, centered: true);
        Grid.SetRow(caption, 1);
        grid.Children.Add(caption);

        return grid;
    }

    private static UIElement BuildCardCaptionBar(Note note, Brush soft, bool showSourceApp, bool centered, double topMargin = 0)
    {
        var captionText = showSourceApp
            ? note.Caption
            : (note.Caption.Split(" · ", 2)[0]);

        var grid = new Grid
        {
            Margin = new Thickness(0, topMargin, 0, 0),
            HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var txt = new TextBlock
        {
            Text = captionText,
            FontSize = 7.5,
            FontFamily = MonoFont,
            Foreground = soft,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(txt, 0);
        grid.Children.Add(txt);

        if (showSourceApp && SourceApp.GetAppIcon(note.AppName) is { } appIcon)
        {
            var img = new Image
            {
                Source = appIcon,
                Width = 11,
                Height = 11,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(img, 1);
            grid.Children.Add(img);
        }

        return grid;
    }

    private static FrameworkElement BuildThemePeg(LineTheme theme, bool pinned)
    {
        switch (theme)
        {
            case LineTheme.Pixel:
            {
                var jawBrush = ThemePalette.Brush(pinned ? Color.FromRgb(0x4D, 0x4D, 0x4D) : Color.FromRgb(0xDB, 0x5C, 0x3D));
                var stack = new StackPanel { Width = 12, Height = 26, IsHitTestVisible = false };
                var topRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                topRow.Children.Add(new Rectangle { Width = 4, Height = 12, Fill = jawBrush, Margin = new Thickness(0, 0, 2, 0) });
                topRow.Children.Add(new Rectangle { Width = 4, Height = 12, Fill = jawBrush });
                stack.Children.Add(topRow);
                stack.Children.Add(new Rectangle { Width = 12, Height = 3, Fill = Brushes.Black });
                var botRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                botRow.Children.Add(new Rectangle { Width = 4, Height = 8, Fill = jawBrush, Margin = new Thickness(0, 0, 2, 0) });
                botRow.Children.Add(new Rectangle { Width = 4, Height = 8, Fill = jawBrush });
                stack.Children.Add(botRow);
                return stack;
            }

            case LineTheme.Skeuo:
            {
                var grid = new Grid { Width = 24, Height = 27, IsHitTestVisible = false };
                var wireBrush = ThemePalette.Brush(Color.FromRgb(0xC8, 0xCC, 0xD2));
                grid.Children.Add(new Border
                {
                    Width = 5,
                    Height = 12,
                    CornerRadius = new CornerRadius(2.5),
                    BorderBrush = wireBrush,
                    BorderThickness = new Thickness(1.5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(5, 1, 0, 0)
                });
                grid.Children.Add(new Border
                {
                    Width = 5,
                    Height = 12,
                    CornerRadius = new CornerRadius(2.5),
                    BorderBrush = wireBrush,
                    BorderThickness = new Thickness(1.5),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 5, 0)
                });
                grid.Children.Add(new Border
                {
                    Width = 22,
                    Height = 12,
                    CornerRadius = new CornerRadius(2),
                    Background = new LinearGradientBrush(
                        pinned ? Color.FromRgb(0x58, 0x58, 0x60) : Color.FromRgb(0x42, 0x42, 0x48),
                        pinned ? Color.FromRgb(0x28, 0x28, 0x2E) : Color.FromRgb(0x14, 0x14, 0x18),
                        90.0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 2)
                });
                return grid;
            }

            case LineTheme.Minimal:
            {
                var host = new Grid { Width = 14, Height = 24, IsHitTestVisible = false };
                host.Children.Add(new Ellipse
                {
                    Width = 8.5,
                    Height = 8.5,
                    Fill = ThemePalette.Brush(pinned ? Color.FromRgb(0x3D, 0x3D, 0x3D) : Color.FromRgb(0xB8, 0xB8, 0xBE)),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 2)
                });
                return host;
            }

            case LineTheme.Neon:
            {
                var dotColor = pinned ? Color.FromRgb(0xFF, 0x4D, 0xCC) : Color.FromRgb(0x33, 0xF2, 0xFF);
                var host = new Grid { Width = 16, Height = 24, IsHitTestVisible = false };
                host.Children.Add(new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = ThemePalette.Brush(dotColor),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 2),
                    Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.85, Color = dotColor }
                });
                return host;
            }

            case LineTheme.Washi:
            {
                var baseColor = pinned ? Color.FromArgb(0xCC, 0x73, 0x73, 0x73) : Color.FromArgb(0xCF, 0xF0, 0x99, 0x8C);
                var tape = new Canvas
                {
                    Width = 38,
                    Height = 12,
                    Background = ThemePalette.Brush(baseColor),
                    ClipToBounds = true,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(-6.0)
                };
                var stripeBrush = ThemePalette.Brush(Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF));
                for (double x = -10; x < 42; x += 7)
                {
                    tape.Children.Add(new System.Windows.Shapes.Line
                    {
                        X1 = x,
                        Y1 = 12,
                        X2 = x + 12,
                        Y2 = 0,
                        Stroke = stripeBrush,
                        StrokeThickness = 2.0
                    });
                }
                var host = new Grid { Width = 44, Height = 24, IsHitTestVisible = false };
                tape.VerticalAlignment = VerticalAlignment.Bottom;
                tape.Margin = new Thickness(0, 0, 0, 1);
                host.Children.Add(tape);
                return host;
            }

            default:
                return BuildClothespin(pinned);
        }
    }

    private static Grid BuildClothespin(bool pinned)
    {
        var pegBrush = pinned
            ? new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0x4D, 0x50, 0x57), 0.0),
                    new(Color.FromRgb(0x78, 0x7C, 0x85), 0.45),
                    new(Color.FromRgb(0x3D, 0x3F, 0x45), 1.0)
                },
                new Point(0, 0),
                new Point(1, 0))
            : new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0xB9, 0x81, 0x46), 0.0),
                    new(Color.FromRgb(0xE9, 0xBF, 0x85), 0.46),
                    new(Color.FromRgb(0xC8, 0x93, 0x5A), 0.56),
                    new(Color.FromRgb(0x9C, 0x6A, 0x36), 1.0)
                },
                new Point(0, 0),
                new Point(1, 0));

        var springBrush = new LinearGradientBrush(
            Color.FromRgb(0x9E, 0x9E, 0xA0),
            Color.FromRgb(0xF0, 0xF0, 0xF2),
            90.0);

        var grid = new Grid
        {
            Width = 14,
            Height = 27,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1.5, Opacity = 0.30, Color = Colors.Black }
        };
        grid.Children.Add(new Border
        {
            Width = 10,
            Height = 27,
            CornerRadius = new CornerRadius(2.5),
            Background = pegBrush,
            BorderBrush = ThemePalette.Brush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0.6)
        });
        grid.Children.Add(new Border
        {
            Width = 13,
            Height = 3.2,
            CornerRadius = new CornerRadius(1.6),
            Background = springBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0)
        });
        return grid;
    }

    private static UIElement BuildKnot(bool isDark, LineTheme theme)
    {
        var grid = new Grid { Width = 12, Height = 16, IsHitTestVisible = false };
        var knotColor = ThemePalette.Brush(RopeMainForTheme(theme, isDark));
        grid.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 6,
            Fill = knotColor,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        grid.Children.Add(new WpfPath
        {
            Stroke = knotColor,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 4,4 Q 2,10 1.5,14 M 8,4 Q 10,10 10.5,13")
        });
        return grid;
    }

    private static Color RopeMainForTheme(LineTheme theme, bool isDark) => theme switch
    {
        LineTheme.Pixel => Colors.Black,
        LineTheme.Skeuo => Color.FromRgb(0x8C, 0x66, 0x38),
        LineTheme.Minimal => isDark ? Color.FromRgb(0x99, 0x99, 0x99) : Color.FromRgb(0x8C, 0x8C, 0x8C),
        LineTheme.Neon => Color.FromRgb(0x33, 0xF2, 0xFF),
        LineTheme.Washi => Color.FromRgb(0xD9, 0x40, 0x40),
        _ => ThemePalette.RopeMain(isDark)
    };

    private static QuadraticBezierSegment DrawRope(Canvas canvas, double width, bool isDark, LineTheme theme, double sagScale)
    {
        double top = ScreenPlacement.RopeTop;
        double controlY = top + 2.0 * ScreenPlacement.RopeSag(width) * sagScale;
        var figure = new PathFigure { StartPoint = new Point(-20, top), IsClosed = false };
        var bezier = new QuadraticBezierSegment(new Point(width / 2.0, controlY), new Point(width + 20, top), true);
        figure.Segments.Add(bezier);
        var geom = new PathGeometry([figure]);

        var maskBrush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Colors.Transparent, 0.0),
                new(Colors.Black, 0.06),
                new(Colors.Black, 0.94),
                new(Colors.Transparent, 1.0)
            },
            new Point(0, 0),
            new Point(1, 0));

        switch (theme)
        {
            case LineTheme.Pixel:
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = Brushes.Black,
                    StrokeThickness = 3.0,
                    StrokeDashArray = [2.0, 1.0],
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false
                });
                return bezier;

            case LineTheme.Skeuo:
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = ThemePalette.Brush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
                    StrokeThickness = 4.5,
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = ThemePalette.Brush(Color.FromRgb(0x8C, 0x66, 0x38)),
                    StrokeThickness = 4.0,
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false
                });
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = ThemePalette.Brush(Color.FromRgb(0xD9, 0xB3, 0x73)),
                    StrokeThickness = 4.0,
                    StrokeDashArray = [1.0, 1.2],
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false
                });
                return bezier;

            case LineTheme.Minimal:
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = ThemePalette.Brush(RopeMainForTheme(theme, isDark)),
                    StrokeThickness = 1.1,
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false
                });
                return bezier;

            case LineTheme.Neon:
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = ThemePalette.Brush(Color.FromArgb(0x99, 0x33, 0xF2, 0xFF)),
                    StrokeThickness = 4.5,
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false,
                    Effect = new BlurEffect { Radius = 5 }
                });
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = ThemePalette.Brush(Color.FromRgb(0xCC, 0xFF, 0xFF)),
                    StrokeThickness = 1.5,
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false
                });
                return bezier;

            case LineTheme.Washi:
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = ThemePalette.Brush(Color.FromRgb(0xD9, 0x40, 0x40)),
                    StrokeThickness = 1.8,
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false
                });
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.8,
                    StrokeDashArray = [2.2, 2.2],
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false
                });
                return bezier;

            default:
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = ThemePalette.Brush(Color.FromArgb(0x3D, 0x00, 0x00, 0x00)),
                    StrokeThickness = 2.6,
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false,
                    Margin = new Thickness(0, 1.5, 0, 0)
                });
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = ThemePalette.Brush(ThemePalette.RopeMain(isDark)),
                    StrokeThickness = 2.2,
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false
                });
                canvas.Children.Add(new WpfPath
                {
                    Data = geom,
                    Stroke = ThemePalette.Brush(ThemePalette.RopeHighlight(isDark)),
                    StrokeThickness = 2.2,
                    StrokeDashArray = [1.8, 3.2],
                    OpacityMask = maskBrush,
                    IsHitTestVisible = false
                });
                return bezier;
        }
    }

    private static UIElement BuildEmptyHint(bool filtering, bool isDark)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemePalette.Brush(ThemePalette.ChromeFill(isDark)),
            BorderBrush = ThemePalette.Brush(ThemePalette.ChromeBorder(isDark)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 12, 6),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = filtering ? Localization.L("Nothing matches") : Localization.L("The line is empty"),
                FontSize = 12,
                Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(isDark))
            }
        };
    }

    private static UIElement BuildBasketHandle(int count, bool open, bool isDark, LineTheme theme, Action onClick)
    {
        var stroke = ThemePalette.Brush(ThemePalette.ChromeText(isDark));
        var basketGlyph = new WpfPath
        {
            Width = 14,
            Height = 12,
            Stretch = Stretch.Uniform,
            Stroke = stroke,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M 1,5 H 13 L 11.5,11 H 2.5 Z M 4,5 L 5.5,1.5 M 10,5 L 8.5,1.5"),
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(basketGlyph);
        stack.Children.Add(new TextBlock
        {
            Text = Localization.Format("Basket {0}", count),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = SansFont,
            Foreground = ThemePalette.Brush(ThemePalette.ChromeText(isDark)),
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(new Border
        {
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(5, 1, 5, 1),
            CornerRadius = new CornerRadius(4),
            Background = ThemePalette.Brush(ThemePalette.ChipIdleFill(isDark)),
            Child = new TextBlock
            {
                Text = open ? "↑" : "↓",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                FontFamily = MonoFont,
                Foreground = ThemePalette.Brush(ThemePalette.ChromeText(isDark))
            }
        });

        var pill = new Border
        {
            Background = ThemePalette.Brush(ThemePalette.ChromeFill(isDark)),
            BorderBrush = ThemePalette.Brush(open ? ThemePalette.PickedColor(theme) : ThemePalette.ChromeBorder(isDark)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(11, 5, 11, 5),
            Cursor = Cursors.Hand,
            ToolTip = Localization.L("Open the basket"),
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.22, Color = Colors.Black },
            Child = stack
        };
        pill.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return pill;
    }

    private static UIElement BuildPaperStrip(PaperKind paper, LineTheme theme)
    {
        if (theme != LineTheme.Paper)
        {
            var edge = ThemePalette.CardEdge(theme, paper);
            return new Border
            {
                CornerRadius = new CornerRadius(Math.Max(3.0, ThemePalette.CardCorner(theme))),
                Background = ThemePalette.Brush(ThemePalette.CardFill(theme, paper)),
                BorderBrush = edge is { } e ? ThemePalette.Brush(e.Color) : null,
                BorderThickness = new Thickness(edge?.Width ?? 0.0)
            };
        }

        if (paper == PaperKind.Tag)
        {
            const double c = 9;
            return new Border
            {
                CornerRadius = new CornerRadius(c, 3, 3, c),
                Background = ThemePalette.Brush(ThemePalette.KraftFill)
            };
        }
        if (paper == PaperKind.Tractor)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = ThemePalette.Brush(ThemePalette.TractorLightStripe),
                BorderBrush = ThemePalette.Brush(Color.FromArgb(0x2A, 0x3D, 0x5A, 0x32)),
                BorderThickness = new Thickness(0.8)
            };
        }
        if (paper == PaperKind.Folder)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = ThemePalette.Brush(ThemePalette.ManilaFill),
                BorderBrush = ThemePalette.Brush(ThemePalette.ManilaStroke),
                BorderThickness = new Thickness(0.8)
            };
        }
        if (paper == PaperKind.Polaroid)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = ThemePalette.Brush(ThemePalette.PolaroidFill)
            };
        }
        return new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = ThemePalette.Brush(ThemePalette.ReceiptFill),
            BorderBrush = ThemePalette.Brush(Color.FromArgb(0x45, 0xDC, 0x85, 0x1A)),
            BorderThickness = new Thickness(0.8)
        };
    }

    private static UIElement? BuildBasketLeadingPreview(Note note)
    {
        if (note.Paper == PaperKind.Polaroid)
        {
            var thumb = note.Kind == ItemKind.Image ? TryLoadThumbnail(note.FilePath) : null;
            return new Border
            {
                Width = 40,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(2, 2, 2, 4),
                Background = Brushes.White,
                BorderBrush = ThemePalette.Brush(Color.FromArgb(0x28, 0x00, 0x00, 0x00)),
                BorderThickness = new Thickness(0.6),
                Child = thumb is not null
                    ? new Image { Source = thumb, Stretch = Stretch.UniformToFill }
                    : new Border
                    {
                        Background = ThemePalette.Brush(ThemePalette.TagColor(note.Kind)),
                        Child = new TextBlock
                        {
                            Text = note.Kind == ItemKind.Video ? "▶" : "▣",
                            FontSize = 10,
                            Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
            };
        }

        if (note.Paper == PaperKind.Folder)
        {
            var iconTarget = note.FileTargets.FirstOrDefault() ?? note.FilePath;
            if (SourceApp.GetFileIcon(iconTarget, large: true) is { } fileIcon && note.FileTargets.Count <= 1)
            {
                return new Image
                {
                    Source = fileIcon,
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            return new Border
            {
                Width = 24,
                Height = 20,
                Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = new CornerRadius(3),
                Background = ThemePalette.Brush(ThemePalette.TagColor(ItemKind.File)),
                Child = new TextBlock
                {
                    Text = note.FileTargets.Count > 1 ? note.FileTargets.Count.ToString() : "▤",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        return null;
    }

    private static string? BasketDetailText(Note note)
    {
        if (note.Link is not null)
        {
            return string.IsNullOrEmpty(note.LinkPath) ? null : note.LinkPath;
        }
        if (note.IsCode)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(note.Body))
        {
            return null;
        }
        var oneLine = note.Body.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return note.IsMarkdown ? Markdown.StripInlineSyntax(oneLine) : oneLine;
    }

    private static string FormatBasketRowTime(DateTime date)
    {
        if (date.Date >= DateTime.Today.AddDays(-6))
        {
            return date.ToString("ddd HH:mm");
        }
        return date.ToString("MM-dd HH:mm");
    }

    private static BitmapImage? TryLoadThumbnail(string path)
    {
        if (!File.Exists(path))
        {
            ThumbCache.Remove(path);
            return null;
        }
        var mtime = File.GetLastWriteTimeUtc(path).Ticks;
        var cacheKey = $"{path}|{mtime}";
        if (ThumbCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }
        if (ThumbCache.Count > 128)
        {
            ThumbCache.Clear();
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 240;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            ThumbCache[cacheKey] = bmp;
            return bmp;
        }
        catch
        {
            ThumbCache[cacheKey] = null;
            return null;
        }
    }
}
