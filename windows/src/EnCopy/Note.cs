using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace EnCopy;

public static class NoteFolder
{
    public static string Folder => DataFolder.NotesFolder;

    public static bool Contains(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        return string.Equals(parent, Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase);
    }
}

public enum ItemKind
{
    Text,
    Code,
    Link
}

/// <summary>
/// 剪贴板文本/链接/代码的展示模型，对应 Mac 版 NoteSheet.swift 的切分与归类规则。
/// </summary>
public sealed class Note
{
    private const int TitleLimit = 60;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public string FilePath { get; }
    public string RelativePath { get; }
    public string Title { get; }
    public string Body { get; }
    public string FullText { get; }
    public bool IsCode { get; }
    public Uri? Link { get; }
    public DateTime UsedAt { get; }
    public bool Pinned { get; }

    public ItemKind Kind => Link is not null ? ItemKind.Link : IsCode ? ItemKind.Code : ItemKind.Text;

    public string KindLabel => Kind switch
    {
        ItemKind.Link => Localization.L("Link"),
        ItemKind.Code => Localization.L("Code"),
        _ => Localization.L("Text")
    };

    public string LinkPath
    {
        get
        {
            if (Link is null) return string.Empty;
            var path = Link.AbsolutePath;
            return string.IsNullOrEmpty(path) || path == "/" ? string.Empty : path;
        }
    }

    public string SummaryLine =>
        Link is not null
            ? (string.IsNullOrEmpty(LinkPath) ? FullText : $"{Title}{LinkPath}")
            : !string.IsNullOrEmpty(Title)
                ? Title
                : FirstLine(Body);

    public Note(
        string filePath,
        string relativePath,
        string title,
        string body,
        string fullText,
        bool isCode,
        Uri? link,
        DateTime usedAt,
        bool pinned = false)
    {
        FilePath = filePath;
        RelativePath = relativePath;
        Title = title;
        Body = body;
        FullText = fullText;
        IsCode = isCode;
        Link = link;
        UsedAt = usedAt;
        Pinned = pinned;
    }

    public Note WithUsedAt(DateTime usedAt) =>
        new(FilePath, RelativePath, Title, Body, FullText, IsCode, Link, usedAt, Pinned);

    public static Note? Load(string filePath, string relativePath, DateTime usedAt, bool pinned = false)
    {
        if (!File.Exists(filePath)) return null;
        string? text = ReadPlainText(filePath);
        if (text is null) return null;
        return Parse(filePath, relativePath, text, usedAt, pinned);
    }

    public static Note Parse(string filePath, string relativePath, string plainText, DateTime usedAt, bool pinned = false)
    {
        var full = plainText;
        var preview = full.Length > 4000 ? full[..4000] : full;
        var trimmed = preview.Trim();

        if (WebAddress(trimmed) is { } link)
        {
            var host = link.Host;
            var site = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
            return new Note(filePath, relativePath, site, trimmed, full, isCode: false, link, usedAt, pinned);
        }

        if (LooksLikeCode(trimmed))
        {
            return new Note(filePath, relativePath, string.Empty, trimmed, full, isCode: true, link: null, usedAt, pinned);
        }

        var lines = NormalizeNewlines(trimmed).Split('\n');
        var first = lines.Length > 0 ? lines[0].Trim() : string.Empty;
        if (first.Length > TitleLimit)
        {
            return new Note(filePath, relativePath, string.Empty, trimmed, full, isCode: false, link: null, usedAt, pinned);
        }
        var rest = string.Join('\n', lines.Skip(1)).Trim();
        return new Note(filePath, relativePath, first, rest, full, isCode: false, link: null, usedAt, pinned);
    }

    public static Uri? WebAddress(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace))
        {
            return null;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }
        return uri;
    }

    public static string? ReadPlainText(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        if (string.Equals(Path.GetExtension(filePath), ".rtf", StringComparison.OrdinalIgnoreCase))
        {
            var rtfBytes = File.ReadAllBytes(filePath);
            return ExtractTextFromRtf(rtfBytes);
        }
        return File.ReadAllText(filePath, Utf8NoBom);
    }

    public static string ExtractTextFromRtf(byte[] rtfBytes)
    {
        var doc = new FlowDocument();
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var ms = new MemoryStream(rtfBytes);
        range.Load(ms, DataFormats.Rtf);
        return range.Text.TrimEnd('\r', '\n');
    }

    public static bool LooksLikeCode(string text)
    {
        var lines = NormalizeNewlines(text)
            .Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        if (lines.Length < 2) return false;

        int codeLike = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("  ", StringComparison.Ordinal) ||
                line.StartsWith('\t') ||
                line.EndsWith('{') || line.EndsWith('}') ||
                line.EndsWith(';') || line.EndsWith('\\') ||
                line.EndsWith(')') ||
                line.EndsWith("=>", StringComparison.Ordinal) ||
                line.EndsWith("->", StringComparison.Ordinal))
            {
                codeLike++;
            }
        }

        int symbols = text.Count(c => "{}[]();=<>$#|\\".Contains(c));
        return codeLike * 2 >= lines.Length || symbols * 12 >= text.Length;
    }

    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string FirstLine(string s)
    {
        var norm = NormalizeNewlines(s).TrimStart();
        var idx = norm.IndexOf('\n');
        var line = idx >= 0 ? norm[..idx] : norm;
        return line.Length > 80 ? line[..80] : line;
    }
}
