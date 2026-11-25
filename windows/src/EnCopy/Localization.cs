using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace EnCopy;

/// <summary>
/// 加载简体中文与英文嵌入字符串资源；按英文原文键查找，缺失时回退键值本身。
/// </summary>
public static class Localization
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> English =
        new(() => LoadResource("EnCopy.Resources.Strings.en.json"));

    private static readonly Lazy<IReadOnlyDictionary<string, string>> SimplifiedChinese =
        new(() => LoadResource("EnCopy.Resources.Strings.zh-Hans.json"));

    private static string? _languageOverride;

    public static void SetLanguageOverride(string? language) => _languageOverride = language;

    public static bool IsChinese => CurrentLanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string CurrentLanguageTag =>
        !string.IsNullOrWhiteSpace(_languageOverride)
            ? _languageOverride!
            : CultureInfo.CurrentUICulture.Name;

    public static string L(string english)
    {
        var table = IsChinese ? SimplifiedChinese.Value : English.Value;
        if (table.TryGetValue(english, out var value) && !string.IsNullOrEmpty(value))
        {
            return value;
        }
        if (English.Value.TryGetValue(english, out var fallback) && !string.IsNullOrEmpty(fallback))
        {
            return fallback;
        }
        return english;
    }

    public static string Format(string english, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, L(english), args);

    public static IReadOnlyDictionary<string, string> ReadTable(string language) =>
        language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? SimplifiedChinese.Value : English.Value;

    private static IReadOnlyDictionary<string, string> LoadResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded localization resource: {name}");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException($"Invalid localization JSON: {name}");
    }
}
