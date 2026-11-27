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
    Link,
    Image,
    Video,
    File
}

public enum PaperKind
{
    Receipt,
    Tractor,
    Tag,
    Polaroid,
    Folder
}

public enum KindFilter
{
    All,
    Text,
    Link,
    Image,
    File
}

/// <summary>
/// 剪贴板条目及五种纸张分类模型（小票/打孔纸/吊牌/拍立得/文件夹），
/// 对应 Mac 版 NoteSheet.swift 与 BasketList.swift 的类型归属与标签展示。
/// </summary>
public sealed class Note
{
    private const int TitleLimit = 60;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi"
    };

    public string FilePath { get; }
    public string RelativePath { get; }
    public string Title { get; }
    public string Body { get; }
    public string FullText { get; }
    public ItemKind Kind { get; }
    public Uri? Link { get; }
    public bool IsMarkdown { get; }
    public DateTime UsedAt { get; }
    public bool Pinned { get; }
    public string? AppName { get; }
    public IReadOnlyList<string> FileTargets { get; }
    public bool IsMovedAway { get; }

    public string? DisplayAppName => SourceApp.GetDisplayName(AppName);

    public bool IsCode => Kind == ItemKind.Code;

    public PaperKind Paper => Kind switch
    {
        ItemKind.Code => PaperKind.Tractor,
        ItemKind.Link => PaperKind.Tag,
        ItemKind.Image or ItemKind.Video => PaperKind.Polaroid,
        ItemKind.File => PaperKind.Folder,
        _ => PaperKind.Receipt
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

    public string SummaryLine
    {
        get
        {
            if (Link is not null)
            {
                return string.IsNullOrEmpty(LinkPath) ? FullText : $"{Title}{LinkPath}";
            }
            if (Kind is ItemKind.Image or ItemKind.Video or ItemKind.File)
            {
                return !string.IsNullOrEmpty(Title) ? Title : Path.GetFileName(FilePath);
            }
            var rawTitle = !string.IsNullOrEmpty(Title) ? Title : FirstLine(Body);
            return IsMarkdown ? Markdown.StripInlineSyntax(rawTitle) : rawTitle;
        }
    }

    public string Caption =>
        string.IsNullOrWhiteSpace(DisplayAppName)
            ? FormatRelativeStamp(UsedAt)
            : $"{FormatRelativeStamp(UsedAt)} · {DisplayAppName}";

    public bool MatchesFilter(KindFilter filter) => filter switch
    {
        KindFilter.All => true,
        KindFilter.Text => Kind is ItemKind.Text or ItemKind.Code,
        KindFilter.Link => Kind == ItemKind.Link,
        KindFilter.Image => Kind is ItemKind.Image or ItemKind.Video,
        KindFilter.File => Kind == ItemKind.File,
        _ => true
    };

    public Note(
        string filePath,
        string relativePath,
        string title,
        string body,
        string fullText,
        ItemKind kind,
        Uri? link,
        DateTime usedAt,
        bool pinned = false,
        bool isMarkdown = false,
        string? appName = null,
        IReadOnlyList<string>? fileTargets = null,
        bool isMovedAway = false)
    {
        FilePath = filePath;
        RelativePath = relativePath;
        Title = title;
        Body = body;
        FullText = fullText;
        Kind = kind;
        Link = link;
        UsedAt = usedAt;
        Pinned = pinned;
        IsMarkdown = isMarkdown;
        AppName = appName;
        FileTargets = fileTargets ?? [];
        IsMovedAway = isMovedAway;
    }

    public Note WithUsedAt(DateTime usedAt) =>
        new(FilePath, RelativePath, Title, Body, FullText, Kind, Link, usedAt, Pinned, IsMarkdown, AppName, FileTargets, IsMovedAway);

    public Note WithPinned(bool pinned) =>
        new(FilePath, RelativePath, Title, Body, FullText, Kind, Link, UsedAt, pinned, IsMarkdown, AppName, FileTargets, IsMovedAway);

    public static Note? Load(
        string filePath,
        string relativePath,
        DateTime usedAt,
        bool pinned = false,
        string? appName = null)
    {
        var ext = Path.GetExtension(filePath);
        if (Directory.Exists(filePath))
        {
            var dirName = Path.GetFileName(filePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var parent = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);
            return new Note(filePath, relativePath, dirName, parent, filePath, ItemKind.File, null, usedAt, pinned, false, appName, [filePath]);
        }

        if (!File.Exists(filePath)) return null;

        if (ImageExtensions.Contains(ext))
        {
            var fileName = Path.GetFileName(filePath);
            return new Note(filePath, relativePath, fileName, string.Empty, filePath, ItemKind.Image, null, usedAt, pinned, false, appName, [filePath]);
        }

        if (VideoExtensions.Contains(ext))
        {
            var fileName = Path.GetFileName(filePath);
            return new Note(filePath, relativePath, fileName, string.Empty, filePath, ItemKind.Video, null, usedAt, pinned, false, appName, [filePath]);
        }

        if (FileList.IsManifestPath(filePath))
        {
            var summary = FileList.Summarize(filePath);
            if (summary.AllPaths.Count == 0)
            {
                return new Note(filePath, relativePath, Path.GetFileName(filePath), string.Empty, filePath, ItemKind.File, null, usedAt, pinned, false, appName);
            }
            var fullText = summary.AllPaths.Count == 1
                ? summary.AllPaths[0]
                : string.Join(Environment.NewLine, summary.AllPaths);
            return new Note(
                filePath,
                relativePath,
                summary.Title,
                summary.Footer,
                fullText,
                ItemKind.File,
                null,
                usedAt,
                pinned,
                false,
                appName,
                summary.AllPaths,
                isMovedAway: summary.IsMovedAway);
        }

        if (!string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".rtf", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(filePath);
            var parent = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);
            return new Note(filePath, relativePath, fileName, parent, filePath, ItemKind.File, null, usedAt, pinned, false, appName, [filePath]);
        }

        string? text = ReadPlainText(filePath);
        if (text is null) return null;
        return Parse(filePath, relativePath, text, usedAt, pinned, appName);
    }

    public static Note Parse(
        string filePath,
        string relativePath,
        string plainText,
        DateTime usedAt,
        bool pinned = false,
        string? appName = null)
    {
        var full = plainText;
        var preview = full.Length > 4000 ? full[..4000] : full;
        var trimmed = preview.Trim();

        if (WebAddress(trimmed) is { } link)
        {
            var host = link.Host;
            var site = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
            return new Note(filePath, relativePath, site, trimmed, full, ItemKind.Link, link, usedAt, pinned, false, appName);
        }

        var ext = Path.GetExtension(filePath);
        bool isRtf = string.Equals(ext, ".rtf", StringComparison.OrdinalIgnoreCase);
        bool explicitMd = string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase);
        if (explicitMd || (!isRtf && Markdown.LooksLikeMarkdown(trimmed)))
        {
            var mdLines = NormalizeNewlines(trimmed).Split('\n');
            var first = mdLines.Length > 0 ? mdLines[0].Trim() : string.Empty;
            var rest = string.Join('\n', mdLines.Skip(1)).Trim();
            if (Markdown.Heading(first) is { } heading)
            {
                return new Note(filePath, relativePath, heading, rest, full, ItemKind.Text, null, usedAt, pinned, isMarkdown: true, appName);
            }
            if (Markdown.StartsBlock(first) || first.Length > TitleLimit)
            {
                return new Note(filePath, relativePath, string.Empty, trimmed, full, ItemKind.Text, null, usedAt, pinned, isMarkdown: true, appName);
            }
            return new Note(filePath, relativePath, first, rest, full, ItemKind.Text, null, usedAt, pinned, isMarkdown: true, appName);
        }

        if (LooksLikeCode(trimmed))
        {
            return new Note(filePath, relativePath, string.Empty, trimmed, full, ItemKind.Code, null, usedAt, pinned, false, appName);
        }

        var lines = NormalizeNewlines(trimmed).Split('\n');
        var firstLine = lines.Length > 0 ? lines[0].Trim() : string.Empty;
        if (firstLine.Length > TitleLimit)
        {
            return new Note(filePath, relativePath, string.Empty, trimmed, full, ItemKind.Text, null, usedAt, pinned, false, appName);
        }
        var body = string.Join('\n', lines.Skip(1)).Trim();
        return new Note(filePath, relativePath, firstLine, body, full, ItemKind.Text, null, usedAt, pinned, false, appName);
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

    private static string FormatRelativeStamp(DateTime usedLocal)
    {
        var today = DateTime.Today;
        if (usedLocal.Date == today)
        {
            return Localization.Format("Today {0:HH:mm}", usedLocal);
        }
        if (usedLocal.Date == today.AddDays(-1))
        {
            return Localization.Format("Yesterday {0:HH:mm}", usedLocal);
        }
        return usedLocal.ToString("MM-dd HH:mm");
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
