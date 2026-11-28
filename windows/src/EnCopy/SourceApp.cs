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
        var cacheKey = $"{(Localization.IsChinese ? "zh" : "en")}|{appId}";
        if (DisplayNameCache.TryGetValue(cacheKey, out var cached)) return cached;

        string resolved = ResolveFriendlyNameUncached(appId);
        DisplayNameCache[cacheKey] = resolved;
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
            "notepad" => Localization.IsChinese ? "记事本" : "Notepad",
            "explorer" => Localization.IsChinese ? "文件资源管理器" : "File Explorer",
            "mspaint" => Localization.IsChinese ? "画图" : "Paint",
            "snippingtool" or "screenclippinghost" => Localization.IsChinese ? "截图工具" : "Snipping Tool",
            "cmd" => Localization.IsChinese ? "命令提示符" : "Command Prompt",
            "windowsterminal" => "Windows Terminal",
            "powershell" or "pwsh" => "PowerShell",
            "weixin" or "wechat" => Localization.IsChinese ? "微信" : "WeChat",
            _ => null
        };
        if (presetTitle is not null)
        {
            return presetTitle;
        }

        try
        {
            if ((appId.Contains(Path.DirectorySeparatorChar) || appId.Contains(Path.AltDirectorySeparatorChar)) && File.Exists(appId))
            {
                var info = FileVersionInfo.GetVersionInfo(appId);
                if (CleanExecutableTitle(info.FileDescription) is { } cleanDesc)
                {
                    return cleanDesc;
                }
                if (CleanExecutableTitle(info.ProductName) is { } cleanProd)
                {
                    return cleanProd;
                }
                return Path.GetFileNameWithoutExtension(appId);
            }
        }
        catch (Exception)
        {
            // 无版本信息表或拒绝访问时退回可读文件名
        }

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

    private static string? CleanExecutableTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4].Trim();
        }
        return trimmed.Length > 0 ? trimmed : null;
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    public static string? GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var sb = new StringBuilder(256);
        int len = GetClassNameW(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : null;
    }
}

public readonly record struct WindowTraits(
    bool IsVisible,
    bool IsCloaked,
    uint Style,
    uint ExStyle,
    bool HasOwner,
    string? ClassName);

/// <summary>
/// 持续跟踪「最后一次激活的外部正常应用」：
/// 按通用窗口特征（排除 tool window、不激活窗口、不在任务栏上显示的隐藏 Owner / 纯无框 Popup 浮层与系统外壳类）过滤，
/// 不依赖特定第三方 Dock 进程名，确保任意任务栏、托盘或第三方 Dock 软件点击打开设置时仍保留先前正在使用的应用。
/// </summary>
public sealed class ForegroundAppTracker
{
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int DwmwaCloaked = 14;

    private const uint WsPopup = 0x80000000u;
    private const uint WsCaption = 0x00C00000u;
    private const uint WsSysMenu = 0x00080000u;
    private const uint WsThickFrame = 0x00040000u;
    private const uint WsMinimizeBox = 0x00020000u;

    private const uint WsExToolWindow = 0x00000080u;
    private const uint WsExAppWindow = 0x00040000u;
    private const uint WsExNoActivate = 0x08000000u;

    private static readonly HashSet<string> IgnoredShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForXamlIsland",
        "XamlExplorerHostIslandWindow",
        "Progman",
        "WorkerW",
        "#32768",
        "TrayShowDesktopButtonWClass",
        "Shell_InputSwitchTopLevelWindow"
    };

    private IntPtr _lastObservedHwnd;
    private uint _lastObservedPid;

    public string? LastExternalApp { get; private set; }

    public static bool IsIgnoredShellClass(string? className) =>
        !string.IsNullOrWhiteSpace(className) && IgnoredShellClasses.Contains(className.Trim());

    public static bool IsExplorerFolderWindowClass(string? className) =>
        string.Equals(className?.Trim(), "CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className?.Trim(), "ExploreWClass", StringComparison.OrdinalIgnoreCase);

    public static bool IsTaskbarEligibleWindow(WindowTraits traits)
    {
        if (!traits.IsVisible || traits.IsCloaked || IsIgnoredShellClass(traits.ClassName))
        {
            return false;
        }
        if ((traits.ExStyle & WsExToolWindow) != 0 || (traits.ExStyle & WsExNoActivate) != 0)
        {
            return false;
        }
        if (traits.HasOwner && (traits.ExStyle & WsExAppWindow) == 0)
        {
            return false;
        }
        bool hasStandardChrome = (traits.Style & (WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox)) != 0;
        if ((traits.Style & WsPopup) != 0 && !hasStandardChrome && (traits.ExStyle & WsExAppWindow) == 0)
        {
            return false;
        }
        return true;
    }

    public static WindowTraits InspectWindowTraits(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return default;
        bool visible = IsWindowVisible(hwnd);
        int cloaked = 0;
        try { _ = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int)); } catch { }
        uint style = unchecked((uint)GetWindowLongW(hwnd, GwlStyle));
        uint exStyle = unchecked((uint)GetWindowLongW(hwnd, GwlExStyle));
        bool hasOwner = GetWindow(hwnd, GwOwner) != IntPtr.Zero;
        var cls = SourceApp.GetWindowClassName(hwnd);
        return new WindowTraits(visible, cloaked != 0, style, exStyle, hasOwner, cls);
    }

    public void RecordObservation(string? windowClass, string? exePath, uint pid, uint selfPid, WindowTraits? traits = null)
    {
        if (pid == 0 || pid == selfPid || IsIgnoredShellClass(windowClass) || string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }
        if (traits is { } t && !IsTaskbarEligibleWindow(t))
        {
            return;
        }

        var baseKey = SourceApp.NormalizeRuleKey(exePath);
        if (string.IsNullOrEmpty(baseKey))
        {
            return;
        }

        // explorer.exe 同时承载桌面、任务栏、托盘弹窗与文件夹窗口，仅文件夹窗口算正在使用的外部应用
        if (string.Equals(baseKey, "explorer", StringComparison.OrdinalIgnoreCase) &&
            !IsExplorerFolderWindowClass(windowClass))
        {
            return;
        }

        LastExternalApp = exePath;
    }

    public string? ObserveWindow(IntPtr hwnd, string? exePathOverrideForTest = null, uint? pidOverrideForTest = null)
    {
        if (hwnd == IntPtr.Zero) return LastExternalApp;
        IntPtr root = GetAncestor(hwnd, GaRoot);
        IntPtr target = root != IntPtr.Zero ? root : hwnd;

        uint pid;
        if (pidOverrideForTest is { } forcedPid)
        {
            pid = forcedPid;
        }
        else
        {
            GetWindowThreadProcessId(target, out pid);
            if (target == _lastObservedHwnd && pid == _lastObservedPid)
            {
                return LastExternalApp;
            }
            _lastObservedHwnd = target;
            _lastObservedPid = pid;
        }

        uint selfPid = (uint)Environment.ProcessId;
        if (pid == 0 || pid == selfPid) return LastExternalApp;

        var cls = SourceApp.GetWindowClassName(hwnd);
        if (IsIgnoredShellClass(cls)) return LastExternalApp;

        var traits = InspectWindowTraits(target);
        if (!IsTaskbarEligibleWindow(traits)) return LastExternalApp;

        var exe = exePathOverrideForTest ?? SourceApp.ResolveWindowExecutable(target);
        RecordObservation(traits.ClassName ?? cls, exe, pid, selfPid, traits);
        return LastExternalApp;
    }

    public string? ObserveLiveForeground() => ObserveWindow(GetForegroundWindow());

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}

/// <summary>
/// Windows「登录时打开」注册表项（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）：
/// 日常版写 EnCopy，开发版写 EnCopyDev（与 clean-dev.ps1 一致），互不干扰；隔离测试模式（ENCOPY_CHECKS_RUNNER=1）默认在内存中模拟，绝不触碰真实 Run。
/// </summary>
public static class LaunchAtLogin
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool _simulatedInChecks;

    public static string RegistryValueNameForEdition(string editionAppName) =>
        string.Equals(editionAppName?.Trim(), "EnCopy", StringComparison.OrdinalIgnoreCase)
            ? "EnCopy"
            : "EnCopyDev";

    public static bool IsEnabled()
    {
        if (IsInChecksRunner()) return _simulatedInChecks;
        return IsEnabledForEdition(RunRegistryPath, AppIdentity.AppName);
    }

    public static void SetEnabled(bool enabled)
    {
        if (IsInChecksRunner())
        {
            _simulatedInChecks = enabled;
            return;
        }
        SetEnabledForEdition(RunRegistryPath, AppIdentity.AppName, Environment.ProcessPath ?? string.Empty, enabled);
    }

    public static bool IsEnabledForEdition(string registrySubKeyPath, string editionAppName)
    {
        if (string.IsNullOrWhiteSpace(registrySubKeyPath)) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registrySubKeyPath, writable: false);
            var valName = RegistryValueNameForEdition(editionAppName);
            return key?.GetValue(valName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void SetEnabledForEdition(string registrySubKeyPath, string editionAppName, string exePath, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(registrySubKeyPath)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(registrySubKeyPath, writable: true);
            if (key is null) return;
            var valName = RegistryValueNameForEdition(editionAppName);
            if (enabled)
            {
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(valName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(valName, throwOnMissingValue: false);
                if (!string.Equals(valName, editionAppName, StringComparison.Ordinal))
                {
                    key.DeleteValue(editionAppName, throwOnMissingValue: false);
                }
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
