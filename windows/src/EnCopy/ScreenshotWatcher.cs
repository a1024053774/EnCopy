using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace EnCopy;

/// <summary>
/// 通过 SHGetKnownFolderPath 动态解析系统截图目录与游戏/录屏捕获目录，兼容用户文件夹重定向（如 Pictures\游戏截图）。
/// </summary>
public static class ScreenshotFolders
{
    private static readonly Guid FolderIdScreenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    private static readonly Guid FolderIdCaptures = new("edc0fe71-98d8-4f4a-b920-c8dc133cb165");

    public static string GetSystemScreenshotsFolder() =>
        TryGetKnownFolder(FolderIdScreenshots)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    public static string GetSystemCapturesFolder() =>
        TryGetKnownFolder(FolderIdCaptures)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");

    private static string? TryGetKnownFolder(Guid folderId)
    {
        IntPtr pPath = IntPtr.Zero;
        try
        {
            int hr = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out pPath);
            if (hr == 0 && pPath != IntPtr.Zero)
            {
                var path = Marshal.PtrToStringUni(pPath);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return Path.GetFullPath(path);
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            if (pPath != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pPath);
            }
        }
        return null;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}

/// <summary>
/// 监测单个目录中的新图片（及可选录屏），通过格式尾字节完整性检查确保刚写入的大图完全落盘且成功消费后才记为已处理（T-063）。
/// </summary>
public sealed class ScreenshotWatcher : IDisposable
{
    private static readonly byte[] PngIendTrailer = [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };
    private static readonly HashSet<string> RecordingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".webm"
    };

    private readonly string _folderPath;
    private readonly bool _acceptRecordings;
    private readonly bool _adoptExistingAsBaseline;
    private readonly Func<string, bool> _tryConsumeFile;
    private readonly Action? _onFolderChanged;
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _unfinishedSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _firstSeenUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlightPaths = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string>? _knownPaths;
    private FileSystemWatcher? _fsw;
    private Timer? _debounceTimer;
    private Timer? _unfinishedTimer;
    private bool _stopped;
    private bool _disposed;

    public static bool IsRecordingPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && RecordingExtensions.Contains(Path.GetExtension(path));

    public string FolderPath => _folderPath;

    public ScreenshotWatcher(
        string folderPath,
        bool acceptRecordings,
        Func<string, bool> tryConsumeFile,
        Action? onFolderChanged = null,
        bool adoptExistingAsBaseline = true)
    {
        _folderPath = Path.GetFullPath(folderPath);
        _acceptRecordings = acceptRecordings;
        _tryConsumeFile = tryConsumeFile;
        _onFolderChanged = onFolderChanged;
        _adoptExistingAsBaseline = adoptExistingAsBaseline;
    }

    public static bool IsWholeFile(string path, long currentSize, long? previousSize = null)
    {
        if (string.IsNullOrWhiteSpace(path) || currentSize <= 0 || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            if (len <= 0) return false;

            Span<byte> head = stackalloc byte[(int)Math.Min(16L, len)];
            int headRead = fs.Read(head);
            head = head[..headRead];

            int tailCount = (int)Math.Min(32L, len);
            fs.Seek(len - tailCount, SeekOrigin.Begin);
            Span<byte> tail = stackalloc byte[tailCount];
            int tailRead = fs.Read(tail);
            tail = tail[..tailRead];

            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".png":
                    if (len < 33 || tail.Length < PngIendTrailer.Length) return false;
                    return head.Length >= 8 &&
                           head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 &&
                           tail[^PngIendTrailer.Length..].SequenceEqual(PngIendTrailer);

                case ".jpg":
                case ".jpeg":
                    if (head.Length < 2 || head[0] != 0xFF || head[1] != 0xD8 || tail.Length < 2) return false;
                    for (int i = 0; i < tail.Length - 1; i++)
                    {
                        if (tail[i] == 0xFF && tail[i + 1] == 0xD9) return true;
                    }
                    return false;

                case ".gif":
                    return head.Length >= 6 &&
                           head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F' &&
                           tail.Length >= 1 && tail[^1] == 0x3B;

                case ".webp":
                    if (head.Length < 12 ||
                        head[0] != (byte)'R' || head[1] != (byte)'I' || head[2] != (byte)'F' || head[3] != (byte)'F')
                    {
                        return false;
                    }
                    uint riffLen = BitConverter.ToUInt32(head[4..8]);
                    return len >= (long)riffLen + 8L;

                case ".bmp":
                    if (head.Length < 14 || head[0] != (byte)'B' || head[1] != (byte)'M') return false;
                    uint bmpSize = BitConverter.ToUInt32(head[2..6]);
                    return bmpSize >= 26u && len >= bmpSize;

                default:
                    return previousSize.HasValue && previousSize.Value == len;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Start()
    {
        bool scanInitialNew = false;
        lock (_sync)
        {
            if (_disposed || _fsw is not null) return;
            _stopped = false;
            if (!Directory.Exists(_folderPath)) return;

            _knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_adoptExistingAsBaseline)
            {
                // 启动瞬间仅将已完整落盘的存量文件纳入基线，正在写入中的半截新图留待写完后挂上绳
                var existing = EnumerateCandidatesSafe(_folderPath, _acceptRecordings);
                var nowUtc = DateTime.UtcNow;
                foreach (var f in existing)
                {
                    long sz = SafeFileLength(f);
                    if (sz > 0 && IsWholeFile(f, sz, sz))
                    {
                        _knownPaths.Add(f);
                    }
                    else if (sz >= 0)
                    {
                        _unfinishedSizes[f] = sz;
                        _firstSeenUtc[f] = nowUtc;
                    }
                }
                if (_unfinishedSizes.Count > 0)
                {
                    ScheduleUnfinishedCheck();
                }
            }
            else
            {
                // 运行期间由系统刚新建出的截图目录（如首次截图才创建的 Pictures\Screenshots），目录内首张图属于新截图
                scanInitialNew = true;
            }

            try
            {
                var watcher = new FileSystemWatcher(_folderPath)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime
                };
                watcher.Created += (_, _) => ScheduleDebouncedScan();
                watcher.Changed += (_, _) => ScheduleDebouncedScan();
                watcher.Renamed += (_, _) => ScheduleDebouncedScan();
                watcher.Deleted += (_, _) => ScheduleDebouncedScan();
                watcher.EnableRaisingEvents = true;
                _fsw = watcher;
            }
            catch (Exception)
            {
                _fsw = null;
            }
        }

        if (scanInitialNew)
        {
            RunScanPass();
        }
    }

    public void ScanNow() => RunScanPass();

    public void Stop()
    {
        lock (_sync)
        {
            _stopped = true;
            _unfinishedSizes.Clear();
            _firstSeenUtc.Clear();
            _inFlightPaths.Clear();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _unfinishedTimer?.Dispose();
            _unfinishedTimer = null;
            if (_fsw is not null)
            {
                try { _fsw.EnableRaisingEvents = false; } catch { }
                _fsw.Dispose();
                _fsw = null;
            }
        }
    }

    private void ScheduleDebouncedScan()
    {
        lock (_sync)
        {
            if (_stopped || _disposed) return;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => RunScanPass(), null, dueTime: 160, period: Timeout.Infinite);
        }
    }

    private void ScheduleUnfinishedCheck()
    {
        lock (_sync)
        {
            if (_stopped || _disposed || _unfinishedSizes.Count == 0 || _unfinishedTimer is not null) return;
            _unfinishedTimer = new Timer(_ =>
            {
                lock (_sync)
                {
                    _unfinishedTimer?.Dispose();
                    _unfinishedTimer = null;
                }
                CheckUnfinishedPass();
            }, null, dueTime: 250, period: Timeout.Infinite);
        }
    }

    private void RunScanPass()
    {
        List<string> candidatesToCheck = [];
        bool notifyChange = false;

        lock (_sync)
        {
            if (_stopped || _disposed || !Directory.Exists(_folderPath)) return;

            var currentFiles = EnumerateCandidatesSafe(_folderPath, _acceptRecordings);
            var currentSet = currentFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);

            _knownPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int beforeKnown = _knownPaths.Count;
            _knownPaths.IntersectWith(currentSet);
            if (_knownPaths.Count != beforeKnown)
            {
                notifyChange = true;
            }

            foreach (var file in currentFiles)
            {
                if (!_knownPaths.Contains(file))
                {
                    candidatesToCheck.Add(file);
                }
            }
        }

        if (candidatesToCheck.Count > 0)
        {
            EvaluateCandidates(candidatesToCheck);
        }
        if (notifyChange)
        {
            _onFolderChanged?.Invoke();
        }
    }

    private void CheckUnfinishedPass()
    {
        List<string> pending;
        lock (_sync)
        {
            if (_stopped || _disposed || _unfinishedSizes.Count == 0) return;
            pending = _unfinishedSizes.Keys.ToList();
        }
        EvaluateCandidates(pending);
    }

    private void EvaluateCandidates(IReadOnlyList<string> paths)
    {
        var wholeCandidates = new List<(string Path, long Size, DateTime FirstSeen)>();
        var nowUtc = DateTime.UtcNow;

        lock (_sync)
        {
            if (_stopped || _disposed) return;
            _knownPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                if (_knownPaths.Contains(path) || _inFlightPaths.Contains(path)) continue;
                if (!File.Exists(path))
                {
                    _unfinishedSizes.Remove(path);
                    _firstSeenUtc.Remove(path);
                    continue;
                }

                long size = SafeFileLength(path);
                if (size < 0) continue;

                long? prev = _unfinishedSizes.TryGetValue(path, out var oldSize) ? oldSize : null;
                var since = _firstSeenUtc.TryGetValue(path, out var first) ? first : nowUtc;

                if (IsWholeFile(path, size, prev))
                {
                    _inFlightPaths.Add(path);
                    wholeCandidates.Add((path, size, since));
                }
                else if ((nowUtc - since).TotalSeconds < 600.0)
                {
                    _unfinishedSizes[path] = size;
                    _firstSeenUtc[path] = since;
                }
                else
                {
                    _unfinishedSizes.Remove(path);
                    _firstSeenUtc.Remove(path);
                    _knownPaths.Add(path);
                }
            }
        }

        foreach (var (path, size, since) in wholeCandidates)
        {
            // 仅在真正成功读取与入库后才记入 _knownPaths；若遇瞬时文件锁占用返回 false，则留在待重试表等待下轮扫描
            bool consumed = false;
            try
            {
                consumed = _tryConsumeFile(path);
            }
            catch (Exception)
            {
                consumed = false;
            }

            lock (_sync)
            {
                _inFlightPaths.Remove(path);
                if (_stopped || _disposed) continue;
                _knownPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (consumed)
                {
                    _unfinishedSizes.Remove(path);
                    _firstSeenUtc.Remove(path);
                    _knownPaths.Add(path);
                }
                else if ((DateTime.UtcNow - since).TotalSeconds < 600.0)
                {
                    _unfinishedSizes[path] = size;
                    _firstSeenUtc[path] = since;
                }
                else
                {
                    _unfinishedSizes.Remove(path);
                    _firstSeenUtc.Remove(path);
                    _knownPaths.Add(path);
                }
            }
        }

        lock (_sync)
        {
            if (_unfinishedSizes.Count > 0)
            {
                ScheduleUnfinishedCheck();
            }
        }
    }

    private static List<string> EnumerateCandidatesSafe(string folder, bool acceptRecordings)
    {
        var result = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var ext = Path.GetExtension(file);
                if (ImageExtensions.Contains(ext) || (acceptRecordings && RecordingExtensions.Contains(ext)))
                {
                    result.Add(Path.GetFullPath(file));
                }
            }
            result.Sort((a, b) =>
            {
                DateTime ta = SafeCreationUtc(a);
                DateTime tb = SafeCreationUtc(b);
                return ta.CompareTo(tb);
            });
        }
        catch (Exception)
        {
        }
        return result;
    }

    private static DateTime SafeCreationUtc(string path)
    {
        try { return File.GetCreationTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return -1L; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }
}

/// <summary>
/// 统一编排系统截图目录（FOLDERID_Screenshots + FOLDERID_Captures，复制进 EnCopy\Screenshots\ 且原文件不动、按像素哈希原子去重）
/// 与最多 3 个自定义监视文件夹（直接挂上绳、取下时不删原文件）；支持延迟创建的系统目录自动恢复与 STA 剪贴板编组。
/// </summary>
public sealed class ScreenshotCoordinator : IDisposable
{
    public const double TwinChannelDedupWindowSeconds = 4.0;

    private sealed record PendingDirSpec(string FullDir, bool AcceptRecordings, bool CopyToPrivateFolder);

    private readonly AppSettings _settings;
    private readonly History _history;
    private readonly IClipboardBackend _clipboardBackend;
    private readonly Action<string, bool> _onNewItem;
    private readonly Action<string, DateTime> _onRepeatedItem;
    private readonly Action? _onExternalFolderChanged;
    private readonly Func<string, bool>? _isDesktopSaveGuard;
    private readonly Func<bool>? _isOnboardingPending;
    private readonly string? _systemScreenshotsOverride;
    private readonly string? _systemCapturesOverride;
    private readonly Dispatcher? _ownerDispatcher;
    private readonly object _gate = new();
    private readonly List<ScreenshotWatcher> _watchers = [];
    private readonly List<FileSystemWatcher> _parentWatchers = [];
    private readonly List<PendingDirSpec> _pendingDirs = [];
    private readonly HashSet<string> _activeWatchedDirs = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _lastMonotonicUtc = DateTime.MinValue;
    private bool _disposed;

    public ScreenshotCoordinator(
        AppSettings settings,
        History history,
        IClipboardBackend clipboardBackend,
        Action<string, bool> onNewItem,
        Action<string, DateTime> onRepeatedItem,
        Action? onExternalFolderChanged = null,
        string? systemScreenshotsOverride = null,
        string? systemCapturesOverride = null,
        Func<string, bool>? isDesktopSaveGuard = null,
        Func<bool>? isOnboardingPending = null)
    {
        _settings = settings;
        _history = history;
        _clipboardBackend = clipboardBackend;
        _onNewItem = onNewItem;
        _onRepeatedItem = onRepeatedItem;
        _onExternalFolderChanged = onExternalFolderChanged;
        _systemScreenshotsOverride = systemScreenshotsOverride;
        _systemCapturesOverride = systemCapturesOverride;
        _isDesktopSaveGuard = isDesktopSaveGuard;
        _isOnboardingPending = isOnboardingPending;
        _ownerDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
    }

    public void RefreshWatchers()
    {
        bool hasPending = false;
        lock (_gate)
        {
            if (_disposed) return;
            StopAllUnlocked();

            if (_settings.RecordingPaused || _isOnboardingPending?.Invoke() == true)
            {
                return;
            }

            if (_settings.ScreenshotsEnabled)
            {
                var shotsDir = _systemScreenshotsOverride ?? ScreenshotFolders.GetSystemScreenshotsFolder();
                RegisterOrDeferDirUnlocked(shotsDir, acceptRecordings: false, copyToPrivateFolder: true);

                var capturesDir = _systemCapturesOverride ?? ScreenshotFolders.GetSystemCapturesFolder();
                RegisterOrDeferDirUnlocked(capturesDir, acceptRecordings: true, copyToPrivateFolder: true);
            }

            foreach (var customDir in _settings.NormalizedWatchedFolders)
            {
                RegisterOrDeferDirUnlocked(customDir, acceptRecordings: false, copyToPrivateFolder: false);
            }

            hasPending = _pendingDirs.Count > 0;
        }

        // 在父目录监测器 EnableRaisingEvents 开启后立即复检一次，闭合检查与挂载之间的目录新建时间窗
        if (hasPending)
        {
            PromotePendingDirectories();
        }
    }

    public void TriggerImmediateScanForTest()
    {
        PromotePendingDirectories();
        ScreenshotWatcher[] snapshot;
        lock (_gate)
        {
            snapshot = _watchers.ToArray();
        }
        foreach (var w in snapshot)
        {
            w.ScanNow();
        }
    }

    private void RegisterOrDeferDirUnlocked(string? rawDir, bool acceptRecordings, bool copyToPrivateFolder)
    {
        if (string.IsNullOrWhiteSpace(rawDir)) return;
        string fullDir;
        try
        {
            fullDir = Path.GetFullPath(rawDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (DataFolder.Contains(fullDir) || _activeWatchedDirs.Contains(fullDir))
        {
            return;
        }

        if (Directory.Exists(fullDir))
        {
            StartWatcherUnlocked(fullDir, acceptRecordings, copyToPrivateFolder, adoptExistingAsBaseline: true);
            return;
        }

        // 目录暂不存在（例如全新系统尚未产生首张截图时的 Pictures\Screenshots）：监听其最近已存在的父目录并在出现时自动挂载
        if (_pendingDirs.Any(p => string.Equals(p.FullDir, fullDir, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        _pendingDirs.Add(new PendingDirSpec(fullDir, acceptRecordings, copyToPrivateFolder));

        var ancestor = FindExistingAncestor(fullDir);
        if (!string.IsNullOrEmpty(ancestor))
        {
            try
            {
                var pw = new FileSystemWatcher(ancestor)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.DirectoryName
                };
                pw.Created += (_, _) => PromotePendingDirectories();
                pw.Renamed += (_, _) => PromotePendingDirectories();
                pw.EnableRaisingEvents = true;
                _parentWatchers.Add(pw);
            }
            catch (Exception)
            {
            }
        }
    }

    private static string? FindExistingAncestor(string path)
    {
        var cur = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(cur))
        {
            if (Directory.Exists(cur)) return cur;
            cur = Path.GetDirectoryName(cur);
        }
        return null;
    }

    private void PromotePendingDirectories()
    {
        List<ScreenshotWatcher> newlyStarted = [];
        lock (_gate)
        {
            if (_disposed || _pendingDirs.Count == 0) return;

            for (int i = _pendingDirs.Count - 1; i >= 0; i--)
            {
                var spec = _pendingDirs[i];
                if (Directory.Exists(spec.FullDir))
                {
                    _pendingDirs.RemoveAt(i);
                    var w = CreateWatcherUnlocked(spec.FullDir, spec.AcceptRecordings, spec.CopyToPrivateFolder, adoptExistingAsBaseline: false);
                    if (w is not null)
                    {
                        newlyStarted.Add(w);
                    }
                }
            }
        }

        foreach (var w in newlyStarted)
        {
            w.Start();
        }
    }

    private void StartWatcherUnlocked(string fullDir, bool acceptRecordings, bool copyToPrivateFolder, bool adoptExistingAsBaseline)
    {
        var w = CreateWatcherUnlocked(fullDir, acceptRecordings, copyToPrivateFolder, adoptExistingAsBaseline);
        w?.Start();
    }

    private ScreenshotWatcher? CreateWatcherUnlocked(string fullDir, bool acceptRecordings, bool copyToPrivateFolder, bool adoptExistingAsBaseline)
    {
        if (!_activeWatchedDirs.Add(fullDir)) return null;
        var watcher = new ScreenshotWatcher(
            fullDir,
            acceptRecordings,
            tryConsumeFile: path => TryConsumeDetectedFile(path, copyToPrivateFolder),
            onFolderChanged: () => MarshalToSta(() => _onExternalFolderChanged?.Invoke()),
            adoptExistingAsBaseline: adoptExistingAsBaseline);
        _watchers.Add(watcher);
        return watcher;
    }

    private bool TryConsumeDetectedFile(string sourcePath, bool copyToPrivateFolder)
    {
        if (_settings.RecordingPaused || _isOnboardingPending?.Invoke() == true)
        {
            return false;
        }
        if (!File.Exists(sourcePath))
        {
            return true;
        }
        if (_isDesktopSaveGuard is not null && _isDesktopSaveGuard(sourcePath))
        {
            return true;
        }

        lock (_gate)
        {
            if (_disposed) return false;
            if (!File.Exists(sourcePath)) return true;

            if (!copyToPrivateFolder)
            {
                // 自定义监视文件夹：先确认文件可非独占打开读取，原位引用挂上绳（不占私有像素去重槽），取下时不删原文件
                if (!CanOpenForSharedRead(sourcePath, out _))
                {
                    return false;
                }
                var stamp = NextMonotonicUtc();
                bool alreadyKnown = _history.Contains(sourcePath);
                _history.RecordImage(sourcePath, ocrWords: null, pngBytesForHash: null, usedAt: stamp, appName: null, ttlSeconds: null);
                MarshalToSta(() =>
                {
                    if (alreadyKnown) _onRepeatedItem(sourcePath, stamp);
                    else _onNewItem(sourcePath, false);
                });
                return true;
            }

            bool isVideo = ScreenshotWatcher.IsRecordingPath(sourcePath);
            if (isVideo)
            {
                Directory.CreateDirectory(DataFolder.ScreenshotsFolder);
                var ext = Path.GetExtension(sourcePath);
                if (string.IsNullOrEmpty(ext)) ext = ".mp4";
                var stampName = DateTime.Now.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture);
                var destVideo = Path.Combine(DataFolder.ScreenshotsFolder, $"Screenshot {stampName}{ext}");
                int n = 2;
                while (File.Exists(destVideo))
                {
                    destVideo = Path.Combine(DataFolder.ScreenshotsFolder, $"Screenshot {stampName} {n}{ext}");
                    n++;
                }
                try
                {
                    var tmp = destVideo + ".tmp";
                    File.Copy(sourcePath, tmp, overwrite: true);
                    File.Move(tmp, destVideo, overwrite: false);
                }
                catch (IOException)
                {
                    return false;
                }

                var vidUtc = NextMonotonicUtc();
                _history.RecordImage(destVideo, ocrWords: null, pngBytesForHash: null, usedAt: vidUtc, appName: null, ttlSeconds: null);
                MarshalToSta(() => _onNewItem(destVideo, true));
                return true;
            }

            if (!CanOpenForSharedRead(sourcePath, out var rawBytes) || rawBytes is null)
            {
                return false;
            }

            var recordedUtc = NextMonotonicUtc();
            var extImg = Path.GetExtension(sourcePath);
            var outcome = _history.IngestPrivateImage(
                rawBytes,
                "Screenshot",
                string.IsNullOrEmpty(extImg) ? ".png" : extImg,
                recordedUtc,
                appName: null,
                ttlSeconds: null,
                TwinChannelDedupWindowSeconds);
            if (outcome is null)
            {
                return false;
            }

            bool copyClip = _settings.CopyEachScreenshot;
            MarshalToSta(() =>
            {
                if (copyClip)
                {
                    _clipboardBackend.TryWriteImage(outcome.FullPath);
                }
                if (outcome.IsNewFile)
                {
                    _onNewItem(outcome.FullPath, true);
                }
                else if (!outcome.MergedWithinTwinWindow)
                {
                    _onRepeatedItem(outcome.FullPath, recordedUtc);
                }
            });
            return true;
        }
    }

    private void MarshalToSta(Action work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA &&
            (_ownerDispatcher is null || _ownerDispatcher.CheckAccess()))
        {
            work();
            return;
        }

        if (_ownerDispatcher is not null && !_ownerDispatcher.HasShutdownStarted && !_ownerDispatcher.HasShutdownFinished)
        {
            _ownerDispatcher.BeginInvoke(DispatcherPriority.Normal, work);
            return;
        }

        var t = new Thread(() => work()) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    private static bool CanOpenForSharedRead(string path, out byte[]? bytes)
    {
        bytes = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= 0) return false;
            if (fs.Length > 128L * 1024 * 1024)
            {
                return true;
            }
            var buf = new byte[fs.Length];
            int offset = 0;
            while (offset < buf.Length)
            {
                int r = fs.Read(buf, offset, buf.Length - offset);
                if (r <= 0) break;
                offset += r;
            }
            bytes = offset == buf.Length ? buf : buf[..offset];
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private DateTime NextMonotonicUtc()
    {
        var now = DateTime.UtcNow;
        if (now <= _lastMonotonicUtc)
        {
            now = _lastMonotonicUtc.AddMilliseconds(1);
        }
        _lastMonotonicUtc = now;
        return now;
    }

    private void StopAllUnlocked()
    {
        foreach (var pw in _parentWatchers)
        {
            try { pw.EnableRaisingEvents = false; } catch { }
            pw.Dispose();
        }
        _parentWatchers.Clear();
        _pendingDirs.Clear();
        _activeWatchedDirs.Clear();

        foreach (var w in _watchers)
        {
            w.Dispose();
        }
        _watchers.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopAllUnlocked();
        }
    }
}
