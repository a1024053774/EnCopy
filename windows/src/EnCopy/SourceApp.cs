using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace EnCopy;

public enum AppRule
{
    Normal,
    Hourly,
    Skip
}

/// <summary>
/// 来源应用识别、应用与文件 Shell 图标缓存、按应用三档规则（照常记 / 一小时后清 / 不记）以及开机自启注册表管理（T-061）。
/// </summary>
public static class SourceApp
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;

    /// <summary>
    /// 常见密码管理器进程名预设为「不记」（Skip），用户可在设置中自行改档。
    /// </summary>
    public static readonly string[] PresetSkipBaseNames =
    [
        "1password",
        "bitwarden",
        "keepass",
        "keepassxc",
        "keepass2",
        "enpass",
        "dashlane",
        "roboform"
    ];

    private static readonly ConcurrentDictionary<string, string> DisplayNameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, BitmapSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? ResolveClipboardSourceApp()
    {
        var owner = GetClipboardOwner();
        if (ResolveWindowExecutable(owner) is { } ownerExe)
        {
            return ownerExe;
        }
        var fg = GetForegroundWindow();
        return ResolveWindowExecutable(fg);
    }

    public static string? ResolveForegroundApp() =>
        ResolveWindowExecutable(GetForegroundWindow());

    public static string? ResolveWindowExecutable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return null;

        IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProc == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int capacity = sb.Capacity;
            if (QueryFullProcessImageNameW(hProc, 0, sb, ref capacity) && capacity > 0)
            {
                return sb.ToString(0, capacity);
            }
            return null;
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    public static string NormalizeRuleKey(string appIdOrPath)
    {
        if (string.IsNullOrWhiteSpace(appIdOrPath)) return string.Empty;
        var trimmed = appIdOrPath.Trim();
        try
        {
            if (trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar) ||
                trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = Path.GetFileNameWithoutExtension(trimmed);
                if (!string.IsNullOrWhiteSpace(baseName))
                {
                    return baseName.ToLowerInvariant();
                }
            }
        }
        catch (ArgumentException)
        {
        }
        return trimmed.ToLowerInvariant();
    }

    public static AppRule ParseRule(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "hourly" => AppRule.Hourly,
        "skip" => AppRule.Skip,
        "normal" => AppRule.Normal,
        _ => AppRule.Normal
    };

    public static string FormatRule(AppRule rule) => rule switch
    {
        AppRule.Hourly => "hourly",
        AppRule.Skip => "skip",
        _ => "normal"
    };

    public static AppRule GetRule(AppSettings settings, string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return AppRule.Normal;
        if (settings.AppRules.TryGetValue(appId, out var direct))
        {
            return ParseRule(direct);
        }

        var key = NormalizeRuleKey(appId);
        if (!string.IsNullOrEmpty(key))
        {
            if (settings.AppRules.TryGetValue(key, out var byBase))
            {
                return ParseRule(byBase);
            }
            foreach (var kv in settings.AppRules)
            {
                if (string.Equals(NormalizeRuleKey(kv.Key), key, StringComparison.OrdinalIgnoreCase))
                {
                    return ParseRule(kv.Value);
                }
            }
            if (PresetSkipBaseNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return AppRule.Skip;
            }
        }

        return AppRule.Normal;
    }

    public static void SetRule(AppSettings settings, string appId, AppRule rule)
    {
        if (string.IsNullOrWhiteSpace(appId)) return;
        var key = NormalizeRuleKey(appId);
        // 清理同基名的旧键，保持一个应用只有一条规则生效
        var duplicates = settings.AppRules.Keys
            .Where(k => !string.Equals(k, appId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(NormalizeRuleKey(k), key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var dup in duplicates)
        {
            settings.AppRules.Remove(dup);
        }

        bool isPresetSkip = PresetSkipBaseNames.Contains(key, StringComparer.OrdinalIgnoreCase);
        if (rule == AppRule.Normal && !isPresetSkip)
        {
            settings.AppRules.Remove(appId);
        }
        else
        {
            settings.AppRules[appId] = FormatRule(rule);
        }
    }

    public static IReadOnlyList<string> ListRuleApps(AppSettings settings, IEnumerable<string> historyApps, string? frontApp)
    {
        var byBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddCandidate(string? id, bool preferOverwrite)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var key = NormalizeRuleKey(id);
            if (string.IsNullOrEmpty(key)) return;
            // 优先保留完整路径以便读取真实应用图标与友好名称
            bool hasPath = id.Contains(Path.DirectorySeparatorChar) || id.Contains(Path.AltDirectorySeparatorChar);
            if (!byBase.TryGetValue(key, out var existing) || preferOverwrite || (hasPath && !existing.Contains(Path.DirectorySeparatorChar)))
            {
                byBase[key] = id;
            }
        }

        foreach (var preset in PresetSkipBaseNames)
        {
            AddCandidate(preset, preferOverwrite: false);
        }
        foreach (var configured in settings.AppRules.Keys)
        {
            AddCandidate(configured, preferOverwrite: true);
        }
        foreach (var seen in historyApps)
        {
            AddCandidate(seen, preferOverwrite: false);
        }
        if (!string.IsNullOrWhiteSpace(frontApp))
        {
            AddCandidate(frontApp, preferOverwrite: true);
        }

        var frontKey = frontApp is not null ? NormalizeRuleKey(frontApp) : null;
        return byBase
            .OrderBy(kv => string.Equals(kv.Key, frontKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(kv => GetDisplayName(kv.Value), StringComparer.CurrentCultureIgnoreCase)
            .Select(kv => kv.Value)
            .ToArray();
    }

    public static string? GetDisplayName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        if (DisplayNameCache.TryGetValue(appId, out var cached)) return cached;

        string resolved = ResolveFriendlyNameUncached(appId);
        DisplayNameCache[appId] = resolved;
        return resolved;
    }

    public static BitmapSource? GetAppIcon(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        var cacheKey = "app|" + appId;
        if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;

        BitmapSource? bmp = null;
        if ((appId.Contains(Path.DirectorySeparatorChar) || appId.Contains(Path.AltDirectorySeparatorChar)) && File.Exists(appId))
        {
            bmp = ExtractShellIcon(appId, large: false, useFileAttributes: false);
        }
        IconCache[cacheKey] = bmp;
        return bmp;
    }

    public static BitmapSource? GetFileIcon(string? path, bool large = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        bool existsFile = File.Exists(path);
        bool existsDir = !existsFile && Directory.Exists(path);
        string cacheKey = existsFile || existsDir
            ? $"file|{large}|{path}"
            : $"ext|{large}|{Path.GetExtension(path).ToLowerInvariant()}";
        if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
        if (IconCache.Count > 256) IconCache.Clear();

        var bmp = ExtractShellIcon(path, large: large, useFileAttributes: !existsFile && !existsDir, isDirectoryHint: existsDir);
        IconCache[cacheKey] = bmp;
        return bmp;
    }

    private static string ResolveFriendlyNameUncached(string appId)
    {
        var baseKey = NormalizeRuleKey(appId);
        string? presetTitle = baseKey switch
        {
            "1password" => "1Password",
            "bitwarden" => "Bitwarden",
            "keepass" => "KeePass",
            "keepassxc" => "KeePassXC",
            "keepass2" => "KeePass 2",
            "enpass" => "Enpass",
            "dashlane" => "Dashlane",
            "roboform" => "RoboForm",
            "explorer" => "Windows Explorer",
            "notepad" => Localization.IsChinese ? "记事本" : "Notepad",
            "windowsterminal" => "Windows Terminal",
            _ => null
        };

        try
        {
            if ((appId.Contains(Path.DirectorySeparatorChar) || appId.Contains(Path.AltDirectorySeparatorChar)) && File.Exists(appId))
            {
                var info = FileVersionInfo.GetVersionInfo(appId);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                {
                    return info.FileDescription.Trim();
                }
                if (!string.IsNullOrWhiteSpace(info.ProductName))
                {
                    return info.ProductName.Trim();
                }
                if (presetTitle is not null) return presetTitle;
                return Path.GetFileNameWithoutExtension(appId);
            }
        }
        catch (Exception)
        {
            // 无版本信息表或拒绝访问时退回可读文件名
        }

        if (presetTitle is not null) return presetTitle;
        try
        {
            var stem = Path.GetFileNameWithoutExtension(appId);
            if (!string.IsNullOrWhiteSpace(stem)) return stem;
        }
        catch (ArgumentException)
        {
        }
        return appId;
    }

    private static BitmapSource? ExtractShellIcon(string path, bool large, bool useFileAttributes, bool isDirectoryHint = false)
    {
        var shinfo = new SHFILEINFO();
        uint flags = ShgfiIcon | (large ? ShgfiLargeIcon : ShgfiSmallIcon);
        uint attrs = isDirectoryHint ? FileAttributeDirectory : FileAttributeNormal;
        if (useFileAttributes)
        {
            flags |= ShgfiUseFileAttributes;
        }

        IntPtr res = SHGetFileInfoW(path, attrs, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (res == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                shinfo.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>
/// Windows「登录时打开」注册表项（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）：
/// 日常版写 EnCopy，开发版写 EnCopy Dev，互不干扰；隔离测试模式（ENCOPY_CHECKS_RUNNER=1）仅在内存中模拟，绝不触碰真实注册表。
/// </summary>
public static class LaunchAtLogin
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool _simulatedInChecks;

    public static bool IsEnabled()
    {
        if (IsInChecksRunner()) return _simulatedInChecks;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            return key?.GetValue(AppIdentity.AppName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (IsInChecksRunner())
        {
            _simulatedInChecks = enabled;
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(AppIdentity.AppName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppIdentity.AppName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // 受策略限制无法写入 HKCU Run 时静默保持当前状态
        }
    }

    private static bool IsInChecksRunner() =>
        string.Equals(Environment.GetEnvironmentVariable(DataFolder.ChecksRunnerEnv), "1", StringComparison.Ordinal);
}
