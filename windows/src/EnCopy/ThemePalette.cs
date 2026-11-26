using System.Windows.Media;
using Microsoft.Win32;

namespace EnCopy;

public enum LineTheme
{
    Paper,
    Pixel,
    Skeuo,
    Minimal,
    Neon,
    Washi
}

/// <summary>
/// 六套晾衣绳与设置纸张主题色板（对齐 Mac 版 Theme.swift，T-060）：
/// 纸（Paper）、像素（Pixel）、拟物（Skeuo）、极简（Minimal）、霓虹（Neon）、手账（Washi）。
/// </summary>
public static class ThemePalette
{
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Microsoft YaHei UI");
    private static readonly FontFamily SerifFont = new("Georgia, SimSun, Microsoft YaHei UI");
    private static readonly FontFamily SansFont = new("Segoe UI, Microsoft YaHei UI");

    private static bool? _darkOverride;

    public static void SetDarkOverride(bool? isDark) => _darkOverride = isDark;

    public static bool IsDarkMode()
    {
        if (_darkOverride is { } forced)
        {
            return forced;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int appsUseLight)
            {
                return appsUseLight == 0;
            }
        }
        catch
        {
            // 注册表访问受限或无键时按浅色主题展示
        }
        return false;
    }

    public static string ThemeTitle(LineTheme theme) => theme switch
    {
        LineTheme.Pixel => Localization.L("Pixel"),
        LineTheme.Skeuo => Localization.L("Skeuomorphic"),
        LineTheme.Minimal => Localization.L("Minimal"),
        LineTheme.Neon => Localization.L("Neon"),
        LineTheme.Washi => Localization.L("Journal"),
        _ => Localization.L("Paper")
    };

    // 固定 6 色种类标签（所有主题通用）
    public static Color TagColor(ItemKind kind) => kind switch
    {
        ItemKind.Text => Color.FromRgb(0xDC, 0x85, 0x1A),
        ItemKind.Code => Color.FromRgb(0x2E, 0x94, 0x57),
        ItemKind.Link => Color.FromRgb(0x29, 0x70, 0xDC),
        ItemKind.Image => Color.FromRgb(0x8F, 0x57, 0xDC),
        ItemKind.Video => Color.FromRgb(0xDC, 0x47, 0x4D),
        ItemKind.File => Color.FromRgb(0x85, 0x61, 0x3D),
        _ => Color.FromRgb(0xDC, 0x85, 0x1A)
    };

    // Paper 主题经典纸张色
    public static readonly Color ReceiptFill = Color.FromRgb(0xFB, 0xFA, 0xF4);
    public static readonly Color TractorLightStripe = Color.FromRgb(0xF5, 0xF8, 0xF0);
    public static readonly Color TractorDarkStripe = Color.FromRgb(0xE1, 0xED, 0xD6);
    public static readonly Color KraftFill = Color.FromRgb(0xD4, 0xB0, 0x86);
    public static readonly Color KraftEyeletOuter = Color.FromRgb(0xF1, 0xE1, 0xC3);
    public static readonly Color KraftEyeletInner = Color.FromRgb(0x55, 0x3D, 0x29);
    public static readonly Color PolaroidFill = Color.FromRgb(0xFD, 0xFA, 0xFA);
    public static readonly Color ManilaFill = Color.FromRgb(0xED, 0xD5, 0x9B);
    public static readonly Color ManilaStroke = Color.FromRgb(0xC5, 0xA8, 0x68);

    public static readonly Color PaperInk = Color.FromRgb(0x2A, 0x25, 0x1F);
    public static readonly Color PaperSoft = Color.FromRgb(0x76, 0x69, 0x5A);
    public static readonly Color KraftInk = Color.FromRgb(0x40, 0x30, 0x1C);
    public static readonly Color KraftSoft = Color.FromRgb(0x5C, 0x4A, 0x33);
    public static readonly Color MarkdownLinkInk = Color.FromRgb(0x99, 0x5C, 0x14);
    public static readonly Color PickedAmber = Color.FromRgb(0xD3, 0x8A, 0x22);

    public static Color CardFill(LineTheme theme, PaperKind paper) => theme switch
    {
        LineTheme.Paper => paper switch
        {
            PaperKind.Tractor => TractorLightStripe,
            PaperKind.Tag => KraftFill,
            PaperKind.Polaroid => PolaroidFill,
            PaperKind.Folder => ManilaFill,
            _ => ReceiptFill
        },
        LineTheme.Pixel => paper switch
        {
            PaperKind.Tractor => Color.FromRgb(0x1C, 0x26, 0x38),
            PaperKind.Tag => Color.FromRgb(0x8C, 0xD1, 0xFA),
            PaperKind.Folder => Color.FromRgb(0xFC, 0xCC, 0x5C),
            _ => Color.FromRgb(0xFA, 0xF7, 0xE8)
        },
        LineTheme.Skeuo => paper switch
        {
            PaperKind.Tag => Color.FromRgb(0xED, 0xDE, 0xBD),
            PaperKind.Tractor => Color.FromRgb(0xED, 0xF2, 0xE6),
            _ => Color.FromRgb(0xFC, 0xFA, 0xF0)
        },
        LineTheme.Minimal => Colors.White,
        LineTheme.Neon => Color.FromArgb(0xEB, 0x0F, 0x0F, 0x1F),
        LineTheme.Washi => paper switch
        {
            PaperKind.Tag => Color.FromRgb(0xF5, 0xE6, 0xDC),
            PaperKind.Tractor => Color.FromRgb(0xEB, 0xF0, 0xF5),
            _ => Color.FromRgb(0xFA, 0xF5, 0xE6)
        },
        _ => ReceiptFill
    };

    public static Color CardInk(LineTheme theme, PaperKind paper) => theme switch
    {
        LineTheme.Paper when paper == PaperKind.Tag || paper == PaperKind.Folder => KraftInk,
        LineTheme.Pixel when paper == PaperKind.Tractor => Color.FromRgb(0x8C, 0xFA, 0x9E),
        LineTheme.Neon => paper == PaperKind.Tag ? Color.FromRgb(0xFF, 0x73, 0xD9) : Color.FromRgb(0xDB, 0xF7, 0xFF),
        LineTheme.Minimal => Color.FromRgb(0x1F, 0x1F, 0x1F),
        _ => PaperInk
    };

    public static Color CardSoft(LineTheme theme, PaperKind paper) => theme switch
    {
        LineTheme.Paper when paper == PaperKind.Tag || paper == PaperKind.Folder => KraftSoft,
        LineTheme.Pixel when paper == PaperKind.Tractor => Color.FromRgb(0x73, 0xBF, 0x80),
        LineTheme.Neon => Color.FromRgb(0x8C, 0xBF, 0xD9),
        LineTheme.Minimal => Color.FromRgb(0x80, 0x80, 0x80),
        _ => PaperSoft
    };

    public static double CardCorner(LineTheme theme) => theme switch
    {
        LineTheme.Pixel => 0.0,
        LineTheme.Minimal => 10.0,
        LineTheme.Neon => 8.0,
        LineTheme.Washi => 3.5,
        _ => 2.0
    };

    public static (Color Color, double Width)? CardEdge(LineTheme theme, PaperKind paper) => theme switch
    {
        LineTheme.Pixel => (Colors.Black, 2.0),
        LineTheme.Minimal => (Color.FromRgb(0xDB, 0xDB, 0xDB), 0.9),
        LineTheme.Neon => (paper == PaperKind.Tag ? Color.FromRgb(0xFF, 0x4D, 0xCC) : Color.FromRgb(0x33, 0xF2, 0xFF), 1.4),
        LineTheme.Skeuo => (Color.FromArgb(0x38, 0x5A, 0x46, 0x2E), 0.9),
        LineTheme.Washi => (Color.FromArgb(0x30, 0x8C, 0x76, 0x62), 0.8),
        _ => null
    };

    public static Color PickedColor(LineTheme theme) => theme switch
    {
        LineTheme.Pixel => Color.FromRgb(0xFF, 0xD4, 0x00),
        LineTheme.Minimal => Color.FromRgb(0x25, 0x72, 0xEB),
        LineTheme.Neon => Color.FromRgb(0xFF, 0x4D, 0xCC),
        LineTheme.Washi => Color.FromRgb(0xED, 0x73, 0x66),
        _ => PickedAmber
    };

    public static double PickedWidth(LineTheme theme) => theme == LineTheme.Pixel ? 4.0 : 2.5;

    public static FontFamily CardFont(LineTheme theme, bool code = false)
    {
        if (code || theme is LineTheme.Paper or LineTheme.Pixel)
        {
            return MonoFont;
        }
        return theme is LineTheme.Skeuo or LineTheme.Washi ? SerifFont : SansFont;
    }

    public static Color SettingsBackdropColor(LineTheme theme) => theme switch
    {
        LineTheme.Pixel => Color.FromRgb(0x5C, 0x94, 0xED),
        LineTheme.Skeuo => Color.FromRgb(0x8C, 0x69, 0x47),
        LineTheme.Minimal => Color.FromRgb(0xF2, 0xF2, 0xF4),
        LineTheme.Neon => Color.FromRgb(0x0A, 0x0A, 0x17),
        LineTheme.Washi => Color.FromRgb(0xF5, 0xED, 0xDB),
        _ => Color.FromRgb(0xE6, 0xD9, 0xC2)
    };

    // 随系统深浅色切换的外壳与绳索色彩
    public static Color RopeMain(bool dark) => dark ? Color.FromRgb(0xA9, 0x8A, 0x5E) : Color.FromRgb(0x8A, 0x6D, 0x45);
    public static Color RopeHighlight(bool dark) => dark ? Color.FromRgb(0xD4, 0xB9, 0x8B) : Color.FromRgb(0xC9, 0xAB, 0x7C);
    public static Color ChromeFill(bool dark) => dark ? Color.FromArgb(0xEC, 0x24, 0x24, 0x28) : Color.FromArgb(0xF2, 0xFB, 0xF8, 0xF2);
    public static Color ChromeBorder(bool dark) => dark ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x48, 0x7D, 0x6D, 0x58);
    public static Color ChromeText(bool dark) => dark ? Color.FromRgb(0xF2, 0xF2, 0xF4) : Color.FromRgb(0x23, 0x1E, 0x18);
    public static Color ChromeSubText(bool dark) => dark ? Color.FromRgb(0xA7, 0xA9, 0xB2) : Color.FromRgb(0x76, 0x6B, 0x5E);
    public static Color ChipIdleFill(bool dark) => dark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0x28, 0x22, 0x1A);
    public static Color ChipActiveFill(bool dark) => dark ? Color.FromRgb(0xD3, 0x8A, 0x22) : Color.FromRgb(0xC8, 0x7E, 0x18);
    public static Color GroupHeaderFill(bool dark) => dark ? Color.FromArgb(0xEA, 0x2B, 0x2C, 0x31) : Color.FromArgb(0xEC, 0xF3, 0xEE, 0xE5);
    public static Color DividerColor(bool dark) => dark ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x5D, 0x50, 0x3E);
    public static Color NoticeBg(bool dark) => dark ? Color.FromArgb(0xE8, 0x4A, 0x36, 0x14) : Color.FromArgb(0xF5, 0xFF, 0xF3, 0xD6);
    public static Color NoticeBorder(bool dark) => dark ? Color.FromRgb(0xC6, 0x8C, 0x28) : Color.FromRgb(0xE2, 0xBA, 0x64);
    public static Color NoticeFg(bool dark) => dark ? Color.FromRgb(0xFF, 0xE2, 0xA4) : Color.FromRgb(0x6A, 0x48, 0x08);
    public static Color AccentPrimary(bool dark) => dark ? Color.FromRgb(0xEB, 0x9F, 0x38) : Color.FromRgb(0xC8, 0x7E, 0x18);

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
