using System.IO;

namespace EnCopy;

/// <summary>
/// 唯一决定 Windows 版 EnCopy 数据目录的位置；所有历史数据库、笔记、绳子状态与设置都放在此处，
/// 测试模式（ENCOPY_CHECKS_RUNNER=1）必须显式提供隔离目录，严禁触碰真实数据文件夹。
/// </summary>
public static class DataFolder
{
    public const string ChecksRunnerEnv = "ENCOPY_CHECKS_RUNNER";
    public const string ChecksDataDirEnv = "ENCOPY_CHECKS_DATA_DIR";

    private static string? _overrideRoot;

    public static string Root
    {
        get
        {
            if (_overrideRoot is not null)
            {
                return _overrideRoot;
            }

            if (string.Equals(Environment.GetEnvironmentVariable(ChecksRunnerEnv), "1", StringComparison.Ordinal))
            {
                var checkDir = Environment.GetEnvironmentVariable(ChecksDataDirEnv);
                if (string.IsNullOrWhiteSpace(checkDir))
                {
                    throw new InvalidOperationException("Check mode requires ENCOPY_CHECKS_DATA_DIR to be set.");
                }
                return Path.GetFullPath(checkDir);
            }

            return DefaultRootForEdition(AppIdentity.AppName);
        }
    }

    public static string DefaultRootForEdition(string appName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);

    public static string NotesFolder => Path.Combine(Root, "Notes");
    public static string FilesFolder => Path.Combine(Root, "Files");
    public static string ScreenshotsFolder => Path.Combine(Root, "Screenshots");
    public static string RecentlyClearedFolder => Path.Combine(Root, "Recently Cleared");
    public static string DiscardedFolder => Path.Combine(Root, "Discarded");
    public static string HistoryDatabasePath => Path.Combine(Root, "History.sqlite");
    public static string SettingsPath => Path.Combine(Root, "Settings.json");
    public static string LineStatePath => Path.Combine(Root, "LineState.json");

    /// <summary>
    /// 判断指定路径是否位于 EnCopy 私有数据目录下，避免清理或撤回操作误删外部原文件。
    /// </summary>
    public static bool Contains(string path)
    {
        var rootFull = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var targetFull = Path.GetFullPath(path);
        return targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 仅允许 EnCopy 私有生成的笔记与图片执行外部移动或移入系统回收站；
    /// 外部用户文件与内部 .files / .filelist 清单绝不移入回收站或被拖出移走。
    /// </summary>
    public static bool IsMovablePrivateItem(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Contains(path)) return false;
        var ext = Path.GetExtension(path);
        return !string.Equals(ext, ".files", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(ext, ".filelist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 仅供隔离测试为每个独立用例切换临时根目录；不允许指向用户真实数据目录。
    /// </summary>
    public static void UseTestRoot(string? path)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(ChecksRunnerEnv), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("UseTestRoot is only allowed inside the isolated check runner.");
        }
        _overrideRoot = path is null ? null : Path.GetFullPath(path);
    }
}
