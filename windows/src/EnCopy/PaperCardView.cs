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

/// <summary>
/// 渲染五种纸张卡片、六色种类标签、木夹、号码贴纸、绳结与篮子长条行（PaperStrip）。
/// </summary>
public static class PaperCardView
{
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Courier New");
    private static readonly FontFamily SansFont = new("Microsoft YaHei UI, Segoe UI");
    private static readonly Dictionary<string, BitmapImage?> ThumbCache = new(StringComparer.OrdinalIgnoreCase);

    public static UIElement BuildRopeAndCards(
        Canvas canvas,
        double canvasWidth,
        Line line,
        bool isDark,
        Action<Note, int, bool> onCardMouseClick,
        Action onBasketHandleClick)
    {
        canvas.Children.Clear();
        canvas.Width = canvasWidth;
        canvas.Height = ScreenPlacement.LineBandHeight;

        DrawRope(canvas, canvasWidth, isDark);

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
            Canvas.SetTop(hint, ScreenPlacement.RopeY(canvasWidth / 2.0, canvasWidth) + 28);
            canvas.Children.Add(hint);
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
                var knot = BuildKnot(isDark);
                Canvas.SetLeft(knot, kx - 6);
                Canvas.SetTop(knot, ScreenPlacement.RopeY(kx, canvasWidth) - 4);
                Panel.SetZIndex(knot, 28);
                canvas.Children.Add(knot);
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

                double ropeY = ScreenPlacement.RopeY(cx, canvasWidth);
                bool isPicked = line.Picked is not null &&
                    string.Equals(line.Picked.FilePath, note.FilePath, StringComparison.OrdinalIgnoreCase);
                bool isSelected = isPicked || line.SelectedPaths.Contains(System.IO.Path.GetFullPath(note.FilePath));
                bool isCopied = string.Equals(line.CopiedFeedbackPath, note.FilePath, StringComparison.OrdinalIgnoreCase);
                int? stickerNumber = i < 9 ? i + 1 : null;

                var cardVisual = BuildPeggedCard(
                    note,
                    isSelected,
                    isPicked,
                    isCopied,
                    isDark,
                    stickerNumber,
                    onCardMouseClick);

                const double hostWidth = ScreenPlacement.CardWidth + 16;
                Canvas.SetLeft(cardVisual, cx - hostWidth / 2.0);
                Canvas.SetTop(cardVisual, Math.Max(0.0, ropeY - 11.0));
                Panel.SetZIndex(cardVisual, i < pinnedInShown ? 25 : (isPicked ? 20 : 2));
                canvas.Children.Add(cardVisual);
            }
        }

        if (line.BasketCount > 0 || line.BasketOpen)
        {
            double hx = canvasWidth - 96.0;
            double topY = ScreenPlacement.RopeY(hx, canvasWidth);
            double hy = ScreenPlacement.LineBandHeight - 36.0;

            var cord = new System.Windows.Shapes.Line
            {
                X1 = hx,
                Y1 = topY,
                X2 = hx,
                Y2 = hy,
                Stroke = ThemePalette.Brush(ThemePalette.RopeMain(isDark)),
                StrokeThickness = 1.3,
                StrokeDashArray = [3, 2],
                Opacity = 0.78,
                IsHitTestVisible = false
            };
            Canvas.SetZIndex(cord, 4);
            canvas.Children.Add(cord);

            var handle = BuildBasketHandle(line.BasketCount, line.BasketOpen, isDark, onBasketHandleClick);
            handle.Measure(new Size(200, 40));
            Canvas.SetLeft(handle, hx - handle.DesiredSize.Width / 2.0);
            Canvas.SetTop(handle, hy);
            Panel.SetZIndex(handle, 25);
            canvas.Children.Add(handle);
        }

        return canvas;
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

    public static UIElement BuildBasketRow(BasketRow row, bool isPicked, bool isDark, Action<BasketRow> onSelect, Action<BasketRow> onActivate)
    {
        var note = row.Note;
        var cardBg = BuildPaperStrip(note.Paper);
        var inkColor = note.Paper == PaperKind.Tag ? ThemePalette.KraftInk : ThemePalette.PaperInk;
        var softColor = note.Paper == PaperKind.Tag ? ThemePalette.KraftSoft : ThemePalette.PaperSoft;
        var ink = ThemePalette.Brush(inkColor);
        var soft = ThemePalette.Brush(softColor);

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
            FontFamily = note.IsCode || note.Kind == ItemKind.Link ? MonoFont : SansFont,
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
                FontFamily = note.Kind == ItemKind.Text ? SansFont : MonoFont,
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
        if (!string.IsNullOrWhiteSpace(note.AppName))
        {
            rightPanel.Children.Add(new TextBlock
            {
                Text = note.AppName,
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

        var container = new Grid
        {
            Margin = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
            Opacity = isPicked ? 1.0 : 0.88,
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
                CornerRadius = new CornerRadius(4),
                BorderBrush = ThemePalette.Brush(ThemePalette.PickedAmber),
                BorderThickness = new Thickness(2.4),
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

    private static UIElement BuildPeggedCard(
        Note note,
        bool isSelected,
        bool isPicked,
        bool isCopied,
        bool isDark,
        int? stickerNumber,
        Action<Note, int, bool> onCardMouseClick)
    {
        double tilt = Line.TiltFor(note.FilePath);
        double scale = isPicked ? 1.04 : 1.0;

        var host = new Grid
        {
            Width = ScreenPlacement.CardWidth + 16,
            Background = null,
            RenderTransformOrigin = new Point(0.5, 0.08),
            RenderTransform = new TransformGroup
            {
                Children =
                {
                    new RotateTransform(tilt),
                    new ScaleTransform(scale, scale)
                }
            }
        };

        // 卡片本体与贴纸层（木夹压在卡片上沿上方）
        var cardSurface = BuildPaperCardBody(note, isSelected, isDark);
        cardSurface.Margin = new Thickness(8, 16, 8, 4);
        cardSurface.Cursor = Cursors.Hand;
        if (isPicked)
        {
            cardSurface.Margin = new Thickness(8, 20, 8, 0);
        }

        cardSurface.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            onCardMouseClick(note, e.ClickCount, shift);
        };

        host.Children.Add(cardSurface);

        // 左上角种类标签
        var kindTag = BuildKindTag(note.Kind, scale: 0.90);
        if (kindTag is FrameworkElement tagEl)
        {
            tagEl.HorizontalAlignment = HorizontalAlignment.Left;
            tagEl.VerticalAlignment = VerticalAlignment.Top;
            tagEl.Margin = new Thickness(12, isPicked ? 11 : 7, 0, 0);
            Panel.SetZIndex(tagEl, 6);
            host.Children.Add(tagEl);
        }

        // 木夹（居中压在绳线上）
        var peg = BuildClothespin(note.Pinned);
        peg.HorizontalAlignment = HorizontalAlignment.Center;
        peg.VerticalAlignment = VerticalAlignment.Top;
        Panel.SetZIndex(peg, 8);
        host.Children.Add(peg);

        // 右上角号码贴纸（1-9）
        if (stickerNumber is { } num)
        {
            var sticker = new Border
            {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(8.5),
                Background = ThemePalette.Brush(isSelected ? ThemePalette.PickedAmber : Color.FromArgb(0xE8, 0xFF, 0xFD, 0xF7)),
                BorderBrush = ThemePalette.Brush(isSelected ? ThemePalette.PickedAmber : Color.FromArgb(0x55, 0x62, 0x52, 0x3D)),
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

        // 单击复制反馈胶囊
        if (isCopied)
        {
            var copiedPill = new Border
            {
                Background = ThemePalette.Brush(ThemePalette.ChromeFill(isDark)),
                BorderBrush = ThemePalette.Brush(ThemePalette.PickedAmber),
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

        return host;
    }

    private static Grid BuildPaperCardBody(Note note, bool isSelected, bool isDark)
    {
        double w = ScreenPlacement.CardWidth;
        double h = ScreenPlacement.CardHeight;
        var root = new Grid
        {
            Width = w,
            Height = h,
            Effect = new DropShadowEffect
            {
                BlurRadius = isSelected ? 14 : 10,
                ShadowDepth = isSelected ? 6 : 4,
                Opacity = isSelected ? 0.30 : 0.20,
                Color = Colors.Black
            }
        };

        switch (note.Paper)
        {
            case PaperKind.Tractor:
                root.Children.Add(BuildTractorCardSurface(w, h));
                root.Children.Add(BuildTractorContent(note));
                break;

            case PaperKind.Tag:
                root.Children.Add(BuildTagCardSurface(w, h));
                root.Children.Add(BuildTagContent(note));
                break;

            case PaperKind.Polaroid:
                root.Children.Add(BuildPolaroidContent(note, w, h));
                break;

            case PaperKind.Folder:
                root.Children.Add(BuildManilaCardSurface(w, h));
                root.Children.Add(BuildManilaContent(note));
                break;

            default:
                root.Children.Add(BuildReceiptCardSurface(w, h));
                root.Children.Add(BuildReceiptContent(note, isDark));
                break;
        }

        if (isSelected)
        {
            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderBrush = ThemePalette.Brush(ThemePalette.PickedAmber),
                BorderThickness = new Thickness(2.5),
                IsHitTestVisible = false
            });
        }

        return root;
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

    private static UIElement BuildReceiptContent(Note note, bool isDark)
    {
        _ = isDark;
        var ink = ThemePalette.Brush(ThemePalette.PaperInk);
        var soft = ThemePalette.Brush(ThemePalette.PaperSoft);
        var link = ThemePalette.Brush(ThemePalette.MarkdownLinkInk);

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
                FontFamily = MonoFont,
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
                    Stroke = ThemePalette.Brush(Color.FromArgb(0x88, 0x8A, 0x7D, 0x6D)),
                    StrokeThickness = 0.8,
                    StrokeDashArray = [3, 2],
                    Margin = new Thickness(0, 4, 0, 4)
                });
            }

            var bodyBlock = new TextBlock
            {
                FontSize = 8.8,
                FontFamily = MonoFont,
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

        var caption = new TextBlock
        {
            Text = note.Caption,
            FontSize = 7.5,
            FontFamily = MonoFont,
            Foreground = soft,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        };
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

        var border = new Border
        {
            Width = w,
            Height = h,
            BorderBrush = ThemePalette.Brush(Color.FromArgb(0x28, 0x3A, 0x48, 0x32)),
            BorderThickness = new Thickness(0.8),
            Child = canvas
        };
        return border;
    }

    private static UIElement BuildTractorContent(Note note)
    {
        var grid = new Grid { Margin = new Thickness(15, 11, 15, 8), ClipToBounds = true };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var codeBlock = new TextBlock
        {
            Text = note.Body,
            FontSize = 8.0,
            FontFamily = MonoFont,
            Foreground = ThemePalette.Brush(ThemePalette.PaperInk),
            LineHeight = 12.8,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(codeBlock, 0);
        grid.Children.Add(codeBlock);

        var caption = new TextBlock
        {
            Text = note.Caption,
            FontSize = 7.5,
            FontFamily = MonoFont,
            Foreground = ThemePalette.Brush(ThemePalette.PaperSoft),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
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

    private static UIElement BuildTagContent(Note note)
    {
        var grid = new Grid { Margin = new Thickness(11, 16, 11, 8) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = note.Title,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            FontFamily = SansFont,
            Foreground = ThemePalette.Brush(ThemePalette.KraftInk),
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
                Foreground = ThemePalette.Brush(ThemePalette.KraftSoft),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 32,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetRow(stack, 0);
        grid.Children.Add(stack);

        var caption = new TextBlock
        {
            Text = note.Caption,
            FontSize = 7.5,
            FontFamily = MonoFont,
            Foreground = ThemePalette.Brush(ThemePalette.KraftSoft),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(caption, 1);
        grid.Children.Add(caption);

        return grid;
    }

    private static UIElement BuildPolaroidContent(Note note, double w, double h)
    {
        var outer = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(2),
            Background = ThemePalette.Brush(ThemePalette.PolaroidFill),
            BorderBrush = ThemePalette.Brush(Color.FromArgb(0x28, 0x3D, 0x32, 0x24)),
            BorderThickness = new Thickness(0.8),
            Padding = new Thickness(6, 6, 6, 5)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var well = new Border
        {
            Background = ThemePalette.Brush(note.Kind == ItemKind.Video
                ? Color.FromRgb(0x22, 0x25, 0x2D)
                : Color.FromRgb(0xE8, 0xE3, 0xDA)),
            CornerRadius = new CornerRadius(1),
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
                Foreground = ThemePalette.Brush(note.Kind == ItemKind.Video ? Colors.WhiteSmoke : ThemePalette.PaperSoft),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = w - 22,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            well.Child = badgeStack;
        }

        Grid.SetRow(well, 0);
        grid.Children.Add(well);

        var caption = new TextBlock
        {
            Text = note.Caption,
            FontSize = 7.5,
            FontFamily = MonoFont,
            Foreground = ThemePalette.Brush(ThemePalette.PaperSoft),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0)
        };
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

    private static UIElement BuildManilaContent(Note note)
    {
        var grid = new Grid { Margin = new Thickness(10, 20, 10, 8) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var centerStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
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
        centerStack.Children.Add(new TextBlock
        {
            Text = note.Title,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = SansFont,
            Foreground = ThemePalette.Brush(ThemePalette.KraftInk),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 30,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(note.Body))
        {
            centerStack.Children.Add(new TextBlock
            {
                Text = note.Body,
                FontSize = 8.2,
                FontFamily = MonoFont,
                Foreground = ThemePalette.Brush(ThemePalette.KraftSoft),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetRow(centerStack, 0);
        grid.Children.Add(centerStack);

        var caption = new TextBlock
        {
            Text = note.Caption,
            FontSize = 7.5,
            FontFamily = MonoFont,
            Foreground = ThemePalette.Brush(ThemePalette.KraftSoft),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(caption, 1);
        grid.Children.Add(caption);

        return grid;
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

    private static UIElement BuildKnot(bool isDark)
    {
        var grid = new Grid { Width = 12, Height = 16, IsHitTestVisible = false };
        var knotColor = ThemePalette.Brush(ThemePalette.RopeMain(isDark));
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

    private static void DrawRope(Canvas canvas, double width, bool isDark)
    {
        double top = ScreenPlacement.RopeTop;
        double controlY = top + 2.0 * ScreenPlacement.RopeSag(width);
        var figure = new PathFigure { StartPoint = new Point(-20, top), IsClosed = false };
        figure.Segments.Add(new QuadraticBezierSegment(new Point(width / 2.0, controlY), new Point(width + 20, top), true));
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

        var shadow = new WpfPath
        {
            Data = geom,
            Stroke = ThemePalette.Brush(Color.FromArgb(0x3D, 0x00, 0x00, 0x00)),
            StrokeThickness = 2.6,
            OpacityMask = maskBrush,
            IsHitTestVisible = false,
            Margin = new Thickness(0, 1.5, 0, 0)
        };
        var baseStrand = new WpfPath
        {
            Data = geom,
            Stroke = ThemePalette.Brush(ThemePalette.RopeMain(isDark)),
            StrokeThickness = 2.2,
            OpacityMask = maskBrush,
            IsHitTestVisible = false
        };
        var twistedStrand = new WpfPath
        {
            Data = geom,
            Stroke = ThemePalette.Brush(ThemePalette.RopeHighlight(isDark)),
            StrokeThickness = 2.2,
            StrokeDashArray = [1.8, 3.2],
            OpacityMask = maskBrush,
            IsHitTestVisible = false
        };

        canvas.Children.Add(shadow);
        canvas.Children.Add(baseStrand);
        canvas.Children.Add(twistedStrand);
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

    private static UIElement BuildBasketHandle(int count, bool open, bool isDark, Action onClick)
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
            BorderBrush = ThemePalette.Brush(open ? ThemePalette.PickedAmber : ThemePalette.ChromeBorder(isDark)),
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

    private static UIElement BuildPaperStrip(PaperKind paper)
    {
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
