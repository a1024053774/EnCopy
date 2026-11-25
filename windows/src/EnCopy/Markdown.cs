using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace EnCopy;

public enum MarkdownBlockKind
{
    Heading,
    Bullet,
    Number,
    Check,
    Quote,
    Code,
    Paragraph
}

public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    int HeadingLevel,
    string NumberPrefix,
    bool Checked,
    int IndentLevel,
    string Text);

/// <summary>
/// 轻量 Markdown 解析与小票内嵌排版（对应 Mac 版 Markdown.swift）：
/// 原文件始终原样保存，只在小票纸张展示时格式化标题、列表、引用与行内强调/代码。
/// </summary>
public static partial class Markdown
{
    private static readonly FontFamily MonoFamily = new("Consolas, Cascadia Mono, Courier New");

    [GeneratedRegex(@"^#{1,6}\s+\S")]
    private static partial Regex HeadingNonEmptyRegex();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex HeadingPrefixRegex();

    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+\S")]
    private static partial Regex ListNonEmptyRegex();

    [GeneratedRegex(@"^[-*+]\s+\[([ xX])\]\s+")]
    private static partial Regex CheckListPrefixRegex();

    [GeneratedRegex(@"^[-*+]\s+")]
    private static partial Regex BulletPrefixRegex();

    [GeneratedRegex(@"^(\d+[.)])\s+")]
    private static partial Regex NumberPrefixRegex();

    [GeneratedRegex(@"^>\s?")]
    private static partial Regex QuotePrefixRegex();

    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+")]
    private static partial Regex ListStartRegex();

    [GeneratedRegex(@"\*\*[^*\n]+\*\*")]
    private static partial Regex BoldAsteriskRegex();

    [GeneratedRegex(@"__[^_\n]+__")]
    private static partial Regex BoldUnderscoreRegex();

    [GeneratedRegex(@"`[^`\n]+`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\[[^\]\n]+\]\([^)\s]+\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"(\*\*[^*\n]+\*\*|__[^_\n]+__|`[^`\n]+`|\[[^\]\n]+\]\([^)\s]+\)|\*[^*\n]+\*|~~[^~\n]+~~)")]
    private static partial Regex InlineTokenRegex();

    public static bool LooksLikeMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        int blocks = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var t = raw.Trim();
            if (HeadingNonEmptyRegex().IsMatch(t) || t.StartsWith("```", StringComparison.Ordinal))
            {
                blocks += 2;
            }
            else if (ListNonEmptyRegex().IsMatch(t) || QuotePrefixRegex().IsMatch(t))
            {
                blocks += 1;
            }
        }

        int inline = BoldAsteriskRegex().Count(text)
            + BoldUnderscoreRegex().Count(text)
            + InlineCodeRegex().Count(text)
            + MarkdownLinkRegex().Count(text);

        return blocks >= 2 || (blocks >= 1 && inline >= 1) || inline >= 2;
    }

    public static string? Heading(string line)
    {
        var t = line.Trim();
        var m = HeadingPrefixRegex().Match(t);
        if (!m.Success) return null;
        return t[m.Length..].Trim(' ', '#', '\t');
    }

    public static bool StartsBlock(string line)
    {
        var t = line.Trim();
        return t.StartsWith("```", StringComparison.Ordinal)
            || t.StartsWith('>')
            || ListStartRegex().IsMatch(t);
    }

    public static IReadOnlyList<MarkdownBlock> Blocks(string text)
    {
        var result = new List<MarkdownBlock>();
        bool inCode = false;

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var t = raw.Trim();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                result.Add(new MarkdownBlock(MarkdownBlockKind.Code, 0, "", false, 0, raw));
                continue;
            }
            if (t.Length == 0) continue;

            int indent = 0;
            foreach (var ch in raw)
            {
                if (ch == ' ') indent++;
                else if (ch == '\t') indent += 4;
                else break;
            }
            int indentLevel = Math.Min(indent / 2, 4);

            var h = HeadingPrefixRegex().Match(t);
            if (h.Success)
            {
                int level = t[..h.Length].Count(c => c == '#');
                result.Add(new MarkdownBlock(MarkdownBlockKind.Heading, level, "", false, 0, t[h.Length..].TrimEnd('#', ' ')));
                continue;
            }

            var ck = CheckListPrefixRegex().Match(t);
            if (ck.Success)
            {
                bool done = ck.Groups[1].Value is "x" or "X";
                result.Add(new MarkdownBlock(MarkdownBlockKind.Check, 0, "", done, indentLevel, t[ck.Length..]));
                continue;
            }

            var bl = BulletPrefixRegex().Match(t);
            if (bl.Success)
            {
                result.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, 0, "", false, indentLevel, t[bl.Length..]));
                continue;
            }

            var nm = NumberPrefixRegex().Match(t);
            if (nm.Success)
            {
                result.Add(new MarkdownBlock(MarkdownBlockKind.Number, 0, nm.Groups[1].Value, false, indentLevel, t[nm.Length..]));
                continue;
            }

            var qt = QuotePrefixRegex().Match(t);
            if (qt.Success)
            {
                result.Add(new MarkdownBlock(MarkdownBlockKind.Quote, 0, "", false, 0, t[qt.Length..]));
                continue;
            }

            result.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, 0, "", false, 0, t));
        }

        return result;
    }

    public static string Marker(MarkdownBlock block)
    {
        var indent = block.IndentLevel > 0 ? new string('\u2003', block.IndentLevel) : string.Empty;
        return block.Kind switch
        {
            MarkdownBlockKind.Bullet => indent + "•\u00A0",
            MarkdownBlockKind.Number => indent + block.NumberPrefix + "\u00A0",
            MarkdownBlockKind.Check => indent + (block.Checked ? "☑\u00A0" : "☐\u00A0"),
            MarkdownBlockKind.Quote => "▍\u00A0",
            _ => string.Empty
        };
    }

    public static IReadOnlyList<Inline> BuildInlines(
        string text,
        double baseSize,
        Brush inkBrush,
        Brush softBrush,
        Brush linkBrush)
    {
        var output = new List<Inline>();
        var blocks = Blocks(text);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0) output.Add(new LineBreak());
            var block = blocks[i];

            double size = baseSize;
            FontWeight weight = FontWeights.Normal;
            FontStyle style = FontStyles.Normal;
            Brush foreground = softBrush;
            bool monospace = false;

            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    size *= block.HeadingLevel == 1 ? 1.24 : block.HeadingLevel == 2 ? 1.12 : 1.05;
                    weight = FontWeights.Bold;
                    foreground = inkBrush;
                    break;
                case MarkdownBlockKind.Code:
                    size *= 0.92;
                    monospace = true;
                    break;
                case MarkdownBlockKind.Quote:
                    style = FontStyles.Italic;
                    break;
            }

            var marker = Marker(block);
            if (!string.IsNullOrEmpty(marker))
            {
                var markerRun = new Run(marker)
                {
                    FontSize = size,
                    FontWeight = weight,
                    Foreground = block is { Kind: MarkdownBlockKind.Check, Checked: true } ? linkBrush : foreground
                };
                if (monospace) markerRun.FontFamily = MonoFamily;
                output.Add(markerRun);
            }

            if (block.Kind == MarkdownBlockKind.Code)
            {
                output.Add(new Run(block.Text)
                {
                    FontSize = size,
                    FontFamily = MonoFamily,
                    Foreground = foreground
                });
                continue;
            }

            AppendInlineRuns(
                block.Text,
                size,
                weight,
                style,
                foreground,
                linkBrush,
                strikethroughAll: block is { Kind: MarkdownBlockKind.Check, Checked: true },
                output);
        }

        return output;
    }

    public static string StripInlineSyntax(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return InlineTokenRegex().Replace(text, m =>
        {
            var token = m.Value;
            if ((token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal)) ||
                (token.StartsWith("__", StringComparison.Ordinal) && token.EndsWith("__", StringComparison.Ordinal)) ||
                (token.StartsWith("~~", StringComparison.Ordinal) && token.EndsWith("~~", StringComparison.Ordinal)))
            {
                return token[2..^2];
            }
            if ((token.StartsWith('`') && token.EndsWith('`')) ||
                (token.StartsWith('*') && token.EndsWith('*')))
            {
                return token[1..^1];
            }
            if (token.StartsWith('[') && token.Contains("](", StringComparison.Ordinal))
            {
                int close = token.IndexOf("](", StringComparison.Ordinal);
                return token[1..close];
            }
            return token;
        });
    }

    private static void AppendInlineRuns(
        string line,
        double size,
        FontWeight baseWeight,
        FontStyle baseStyle,
        Brush foreground,
        Brush linkBrush,
        bool strikethroughAll,
        List<Inline> output)
    {
        int pos = 0;
        foreach (Match m in InlineTokenRegex().Matches(line))
        {
            if (m.Index > pos)
            {
                output.Add(CreateRun(line[pos..m.Index], size, baseWeight, baseStyle, false, foreground, strikethroughAll, underline: false));
            }

            var token = m.Value;
            if ((token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal)) ||
                (token.StartsWith("__", StringComparison.Ordinal) && token.EndsWith("__", StringComparison.Ordinal)))
            {
                output.Add(CreateRun(token[2..^2], size, FontWeights.Bold, baseStyle, false, foreground, strikethroughAll, underline: false));
            }
            else if (token.StartsWith('`') && token.EndsWith('`'))
            {
                output.Add(CreateRun(token[1..^1], size * 0.94, baseWeight, baseStyle, true, foreground, strikethroughAll, underline: false));
            }
            else if (token.StartsWith("~~", StringComparison.Ordinal) && token.EndsWith("~~", StringComparison.Ordinal))
            {
                output.Add(CreateRun(token[2..^2], size, baseWeight, baseStyle, false, foreground, strikethrough: true, underline: false));
            }
            else if (token.StartsWith('*') && token.EndsWith('*'))
            {
                output.Add(CreateRun(token[1..^1], size, baseWeight, FontStyles.Italic, false, foreground, strikethroughAll, underline: false));
            }
            else if (token.StartsWith('[') && token.Contains("](", StringComparison.Ordinal))
            {
                int close = token.IndexOf("](", StringComparison.Ordinal);
                output.Add(CreateRun(token[1..close], size, baseWeight, baseStyle, false, linkBrush, strikethroughAll, underline: true));
            }
            else
            {
                output.Add(CreateRun(token, size, baseWeight, baseStyle, false, foreground, strikethroughAll, underline: false));
            }

            pos = m.Index + m.Length;
        }

        if (pos < line.Length)
        {
            output.Add(CreateRun(line[pos..], size, baseWeight, baseStyle, false, foreground, strikethroughAll, underline: false));
        }
    }

    private static Run CreateRun(
        string text,
        double size,
        FontWeight weight,
        FontStyle style,
        bool monospace,
        Brush foreground,
        bool strikethrough,
        bool underline)
    {
        var run = new Run(text)
        {
            FontSize = size,
            FontWeight = weight,
            FontStyle = style,
            Foreground = foreground
        };
        if (monospace)
        {
            run.FontFamily = MonoFamily;
        }
        if (strikethrough && underline)
        {
            run.TextDecorations = new TextDecorationCollection(TextDecorations.Strikethrough.Concat(TextDecorations.Underline));
        }
        else if (strikethrough)
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }
        else if (underline)
        {
            run.TextDecorations = TextDecorations.Underline;
        }
        return run;
    }
}
