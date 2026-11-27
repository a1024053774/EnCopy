using System.IO;
using System.Text;

namespace EnCopy;

public sealed record FileCardSummary(
    string Title,
    string Footer,
    string? IconTargetPath,
    IReadOnlyList<string> AllPaths,
    IReadOnlyList<string> PresentPaths,
    bool IsMovedAway);

/// <summary>
/// 资源管理器文件清单（Files/*.files，兼容读取旧 .filelist）：
/// 每次复制一个或多个文件/文件夹，只将原路径按行写进 EnCopy 自己的小清单，
/// 绝不复制、移动或删除用户原文件；原文件全部移走后清单标为「已移走」。
/// </summary>
public static class FileList
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static long _lastStampMs;

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff",
        ".mp4", ".mov", ".mkv", ".webm", ".avi"
    };

    public static bool IsManifestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".files", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".filelist", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMediaFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path)) return false;
        return MediaExtensions.Contains(Path.GetExtension(path));
    }

    public static string? Make(IReadOnlyList<string> rawPaths)
    {
        var normalized = NormalizePaths(rawPaths);
        if (normalized.Count == 0) return null;

        var folder = DataFolder.FilesFolder;
        Directory.CreateDirectory(folder);

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long prev = Interlocked.Read(ref _lastStampMs);
        if (nowMs <= prev) nowMs = prev + 1;
        Interlocked.Exchange(ref _lastStampMs, nowMs);

        var dest = Path.Combine(folder, $"Copied {nowMs}.files");
        int n = 2;
        while (File.Exists(dest))
        {
            dest = Path.Combine(folder, $"Copied {nowMs} {n}.files");
            n++;
        }

        var tmp = dest + ".tmp";
        File.WriteAllText(tmp, string.Join('\n', normalized), Utf8NoBom);
        File.Move(tmp, dest, overwrite: false);
        return dest;
    }

    public static IReadOnlyList<string> NormalizePaths(IEnumerable<string> rawPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in rawPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try
            {
                full = Path.GetFullPath(raw.Trim());
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (seen.Add(full))
            {
                list.Add(full);
            }
        }
        return list;
    }

    public static IReadOnlyList<string> ReadPaths(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return [];
        try
        {
            return File.ReadAllLines(manifestPath, Utf8NoBom)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> PresentPaths(string manifestPath) =>
        ReadPaths(manifestPath)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToArray();

    public static bool AreAllTargetsMissing(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return false;
        try
        {
            var all = File.ReadAllLines(manifestPath, Utf8NoBom)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToArray();
            return all.Length > 0 && !all.Any(p => File.Exists(p) || Directory.Exists(p));
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static string Words(IReadOnlyList<string> paths)
    {
        var sb = new StringBuilder();
        foreach (var p in paths)
        {
            var trimmed = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);
            if (!string.IsNullOrEmpty(name)) sb.Append(name).Append(' ');
            if (!string.IsNullOrEmpty(parent)) sb.Append(parent).Append(' ');
            sb.Append(p).Append('\n');
        }
        return sb.ToString();
    }

    public static FileCardSummary Summarize(string manifestPath)
    {
        var all = ReadPaths(manifestPath);
        if (all.Count == 0)
        {
            return new FileCardSummary(Path.GetFileName(manifestPath), string.Empty, null, [], [], false);
        }

        var present = all.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        bool movedAway = present.Length == 0;
        var effective = movedAway ? all : present;

        if (effective.Count == 1)
        {
            var single = effective[0];
            var trimmed = single.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var title = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(title)) title = single;
            var parentDir = Path.GetDirectoryName(trimmed) ?? string.Empty;
            return new FileCardSummary(title, parentDir, single, all, present, movedAway);
        }

        var countTitle = Localization.Format("{0} files", effective.Count);
        var sampleNames = string.Join(", ", effective.Take(3).Select(NameOf));
        return new FileCardSummary(countTitle, sampleNames, effective[0], all, present, movedAway);
    }

    public static string NameOf(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
