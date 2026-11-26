using System.IO;
using System.Text;
using System.Text.Json;

namespace EnCopy;

public sealed class AppSettings
{
    public bool RecordingPaused { get; set; }
    public bool TextEnabled { get; set; } = true;
    public bool LinksEnabled { get; set; } = true;
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
