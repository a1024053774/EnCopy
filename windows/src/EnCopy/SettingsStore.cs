using System.IO;
using System.Text;
using System.Text.Json;

namespace EnCopy;

public sealed class SavedShortcut
{
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }
    public string Display { get; set; } = string.Empty;

    public Shortcut ToShortcut() => new(Modifiers, VirtualKey, Display);

    public static SavedShortcut From(Shortcut shortcut) => new()
    {
        Modifiers = shortcut.Modifiers,
        VirtualKey = shortcut.VirtualKey,
        Display = shortcut.Display
    };
}

public sealed class AppSettings
{
    public static readonly int[] KeepChoices = [25, 50, 100];
    public static readonly double[] PeekChoices = [0.8, 1.5, 2.5, 4.0];
    public static readonly (long Bytes, string Label)[] OriginalsCapChoices =
    [
        (512L << 20, "512 MB"),
        (1L << 30, "1 GB"),
        (2L << 30, "2 GB"),
        (5L << 30, "5 GB")
    ];

    public const int MaxExtraWatchedFolders = 3;

    public bool RecordingPaused { get; set; }
    public bool ImagesEnabled { get; set; }
    public bool TextEnabled { get; set; } = true;
    public bool LinksEnabled { get; set; } = true;
    public bool FilesEnabled { get; set; } = true;
    public bool ShowsSourceApp { get; set; } = true;
    public bool SoundsEnabled { get; set; } = true;
    public bool ScreenshotsEnabled { get; set; } = true;
    public bool CopyEachScreenshot { get; set; }
    public List<string> ExtraWatchedFolders { get; set; } = [];
    public int? KeepOnLine { get; set; }
    public string Theme { get; set; } = "paper";
    public bool OpensFromTopEdge { get; set; } = true;
    public bool PeeksAtNew { get; set; } = true;
    public double PeekSeconds { get; set; } = 1.5;
    public bool ShowOverFullScreen { get; set; }
    public bool ZoomOnHover { get; set; }
    public long OriginalsCapBytes { get; set; } = 1L << 30;
    public double RetentionLastRunUnix { get; set; }
    public bool RecordingExplained { get; set; }
    public Dictionary<string, string> AppRules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShortcutCustomized { get; set; }
    public SavedShortcut? CustomShortcut { get; set; }

    public int? NormalizedKeepOnLine => KeepOnLine is { } n && KeepChoices.Contains(n) ? n : null;
    public double NormalizedPeekSeconds => PeekChoices.Contains(PeekSeconds) ? PeekSeconds : 1.5;
    public long NormalizedOriginalsCapBytes =>
        OriginalsCapChoices.Any(c => c.Bytes == OriginalsCapBytes) ? OriginalsCapBytes : (1L << 30);

    public IReadOnlyList<string> NormalizedWatchedFolders
    {
        get
        {
            if (ExtraWatchedFolders is not { Count: > 0 }) return [];
            var result = new List<string>(MaxExtraWatchedFolders);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in ExtraWatchedFolders)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string full;
                try
                {
                    full = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (!Directory.Exists(full) || DataFolder.Contains(full)) continue;
                if (seen.Add(full))
                {
                    result.Add(full);
                    if (result.Count >= MaxExtraWatchedFolders) break;
                }
            }
            return result;
        }
    }

    public bool TryAddWatchedFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (!Directory.Exists(full) || DataFolder.Contains(full)) return false;

        var current = NormalizedWatchedFolders.ToList();
        if (current.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (current.Count >= MaxExtraWatchedFolders)
        {
            return false;
        }
        current.Add(full);
        ExtraWatchedFolders = current;
        return true;
    }

    public bool RemoveWatchedFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || ExtraWatchedFolders is not { Count: > 0 }) return false;
        string full;
        try
        {
            full = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            full = folderPath.Trim();
        }
        int removed = ExtraWatchedFolders.RemoveAll(p =>
        {
            try
            {
                var norm = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(norm, full, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return string.Equals(p, full, StringComparison.OrdinalIgnoreCase);
            }
        });
        return removed > 0;
    }

    public LineTheme GetActiveTheme() => Theme?.Trim().ToLowerInvariant() switch
    {
        "pixel" => LineTheme.Pixel,
        "skeuo" => LineTheme.Skeuo,
        "minimal" => LineTheme.Minimal,
        "neon" => LineTheme.Neon,
        "washi" => LineTheme.Washi,
        _ => LineTheme.Paper
    };

    public void SetActiveTheme(LineTheme theme) => Theme = theme switch
    {
        LineTheme.Pixel => "pixel",
        LineTheme.Skeuo => "skeuo",
        LineTheme.Minimal => "minimal",
        LineTheme.Neon => "neon",
        LineTheme.Washi => "washi",
        _ => "paper"
    };

    public Shortcut? GetActiveShortcut()
    {
        if (!ShortcutCustomized)
        {
            return Shortcut.Standard;
        }
        return CustomShortcut is { VirtualKey: > 0 } saved ? saved.ToShortcut() : null;
    }

    public void SetActiveShortcut(Shortcut? shortcut)
    {
        ShortcutCustomized = true;
        CustomShortcut = shortcut is { } s ? SavedShortcut.From(s) : null;
    }
}

/// <summary>
/// 以原子写入保存与读取应用设置，避免崩溃留下半截 JSON。
/// </summary>
public sealed class SettingsStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? DataFolder.SettingsPath;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }
        var json = File.ReadAllText(_path, Utf8NoBom);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(tmp, json, Utf8NoBom);
        File.Move(tmp, _path, overwrite: true);
    }
}
