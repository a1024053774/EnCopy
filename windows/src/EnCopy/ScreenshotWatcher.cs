using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

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
/// 监测单个目录中的新图片（及可选录屏），通过格式尾字节完整性检查确保刚写入的大图完全落盘后才触发回调（对齐 Mac 版 ScreenshotWatcher.swift，T-063）。
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
    private readonly Action<string> _onNewFile;
    private readonly Action? _onFolderChanged;
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _unfinishedSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _firstSeenUtc = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string>? _knownPaths;
    private FileSystemWatcher? _fsw;
    private Timer? _debounceTimer;
    private Timer? _unfinishedTimer;
    private bool _stopped;
    private bool _disposed;

    public static bool IsRecordingPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && RecordingExtensions.Contains(Path.GetExtension(path));

    public string FolderPath => _folderPath;

    public ScreenshotWatcher(string folderPath, bool acceptRecordings, Action<string> onNewFile, Action? onFolderChanged = null)
    {
        _folderPath = Path.GetFullPath(folderPath);
        _acceptRecordings = acceptRecordings;
        _onNewFile = onNewFile;
        _onFolderChanged = onFolderChanged;
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
        lock (_sync)
        {
            if (_disposed || _fsw is not null) return;
            _stopped = false;
            if (!Directory.Exists(_folderPath)) return;

            // 启动瞬间仅将已完整落盘的存量文件纳入基线，正在写入中的半截新图留待写完后挂上绳
            var existing = EnumerateCandidatesSafe(_folderPath, _acceptRecordings);
            _knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
    }

    public void ScanNow() => RunScanPass();

    public void Stop()
    {
        lock (_sync)
        {
            _stopped = true;
            _unfinishedSizes.Clear();
            _firstSeenUtc.Clear();
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

            // 若已知文件被外部移走或删除，同步移出基线并通知上层刷新
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
        var ready = new List<string>();
        var nowUtc = DateTime.UtcNow;

        lock (_sync)
        {
            if (_stopped || _disposed) return;

            foreach (var path in paths)
            {
                if (!File.Exists(path))
                {
                    _unfinishedSizes.Remove(path);
                    _firstSeenUtc.Remove(path);
                    continue;
                }

                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    continue;
                }

                long? prev = _unfinishedSizes.TryGetValue(path, out var oldSize) ? oldSize : null;
                var since = _firstSeenUtc.TryGetValue(path, out var first) ? first : nowUtc;

                if (IsWholeFile(path, size, prev))
                {
                    _unfinishedSizes.Remove(path);
                    _firstSeenUtc.Remove(path);
                    _knownPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (_knownPaths.Add(path))
                    {
                        ready.Add(path);
                    }
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
                    _knownPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _knownPaths.Add(path);
                }
            }

            if (_unfinishedSizes.Count > 0)
            {
                ScheduleUnfinishedCheck();
            }
        }

        foreach (var path in ready)
        {
            _onNewFile(path);
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
/// 统一编排系统截图目录（FOLDERID_Screenshots + FOLDERID_Captures，复制进 EnCopy\Screenshots\ 且原文件不动、按像素哈希去重）
/// 与最多 3 个自定义监视文件夹（直接挂上绳、取下时不删原文件）。
/// </summary>
public sealed class ScreenshotCoordinator : IDisposable
{
    public const double TwinChannelDedupWindowSeconds = 4.0;

    private readonly AppSettings _settings;
    private readonly History _history;
    private readonly IClipboardBackend _clipboardBackend;
    private readonly Action<string, bool> _onNewItem;
    private readonly Action<string, DateTime> _onRepeatedItem;
    private readonly Action? _onExternalFolderChanged;
    private readonly Func<string, bool>? _isDesktopSaveGuard;
    private readonly string? _systemScreenshotsOverride;
    private readonly string? _systemCapturesOverride;
    private readonly object _gate = new();
    private readonly List<ScreenshotWatcher> _watchers = [];

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
        Func<string, bool>? isDesktopSaveGuard = null)
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
    }

    public void RefreshWatchers()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopAllUnlocked();

            if (_settings.RecordingPaused) return;

            var watchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_settings.ScreenshotsEnabled)
            {
                var shotsDir = _systemScreenshotsOverride ?? ScreenshotFolders.GetSystemScreenshotsFolder();
                TryStartOneUnlocked(shotsDir, acceptRecordings: false, copyToPrivateFolder: true, watchedDirs);

                var capturesDir = _systemCapturesOverride ?? ScreenshotFolders.GetSystemCapturesFolder();
                TryStartOneUnlocked(capturesDir, acceptRecordings: true, copyToPrivateFolder: true, watchedDirs);
            }

            foreach (var customDir in _settings.NormalizedWatchedFolders)
            {
                TryStartOneUnlocked(customDir, acceptRecordings: false, copyToPrivateFolder: false, watchedDirs);
            }
        }
    }

    public void TriggerImmediateScanForTest()
    {
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

    private void TryStartOneUnlocked(string? rawDir, bool acceptRecordings, bool copyToPrivateFolder, HashSet<string> watchedDirs)
    {
        if (string.IsNullOrWhiteSpace(rawDir) || !Directory.Exists(rawDir)) return;
        string fullDir;
        try
        {
            fullDir = Path.GetFullPath(rawDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return;
        }

        // 绝不监视 EnCopy 自己的私有目录，避免复制副本触发循环
        if (DataFolder.Contains(fullDir) || !watchedDirs.Add(fullDir))
        {
            return;
        }

        var watcher = new ScreenshotWatcher(
            fullDir,
            acceptRecordings,
            onNewFile: path => HandleDetectedFile(path, copyToPrivateFolder),
            onFolderChanged: _onExternalFolderChanged);
        watcher.Start();
        _watchers.Add(watcher);
    }

    private void HandleDetectedFile(string sourcePath, bool copyToPrivateFolder)
    {
        if (_settings.RecordingPaused || !File.Exists(sourcePath)) return;
        if (_isDesktopSaveGuard is not null && _isDesktopSaveGuard(sourcePath)) return;

        lock (_gate)
        {
            if (_disposed || !File.Exists(sourcePath)) return;

            if (!copyToPrivateFolder)
            {
                // 自定义监视文件夹：原位引用挂上绳，取下时不删原文件
                byte[]? extBytes = TryReadBytesShared(sourcePath);
                var stamp = NextMonotonicUtc();
                bool alreadyKnown = _history.Contains(sourcePath);
                _history.RecordImage(sourcePath, ocrWords: null, pngBytesForHash: extBytes, usedAt: stamp, appName: null, ttlSeconds: null);
                if (alreadyKnown) _onRepeatedItem(sourcePath, stamp);
                else _onNewItem(sourcePath, false);
                return;
            }

            bool isVideo = ScreenshotWatcher.IsRecordingPath(sourcePath);
            byte[]? rawBytes = isVideo ? null : TryReadBytesShared(sourcePath);
            if (!isVideo && rawBytes is null) return;

            if (rawBytes is not null && _history.FindPictureHolding(rawBytes) is { } existingPath)
            {
                // 双通道协同去重：若 Win+Shift+S 剪贴板通道刚刚（4 秒内）存入同一像素图片，静默合并为同一件
                var prevUsedUtc = _history.LastUsedUtcOf(existingPath);
                if (prevUsedUtc.HasValue && (DateTime.UtcNow - prevUsedUtc.Value).TotalSeconds <= TwinChannelDedupWindowSeconds)
                {
                    if (_settings.CopyEachScreenshot && !isVideo)
                    {
                        _clipboardBackend.TryWriteImage(existingPath);
                    }
                    return;
                }

                var repeatStamp = NextMonotonicUtc();
                _onRepeatedItem(existingPath, repeatStamp);
                return;
            }

            Directory.CreateDirectory(DataFolder.ScreenshotsFolder);
            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            var stampName = DateTime.Now.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture);
            var destPath = Path.Combine(DataFolder.ScreenshotsFolder, $"Screenshot {stampName}{ext}");
            int n = 2;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(DataFolder.ScreenshotsFolder, $"Screenshot {stampName} {n}{ext}");
                n++;
            }

            try
            {
                var tmp = destPath + ".tmp";
                File.Copy(sourcePath, tmp, overwrite: true);
                File.Move(tmp, destPath, overwrite: false);
            }
            catch (IOException)
            {
                return;
            }

            var recordedUtc = NextMonotonicUtc();
            _history.RecordImage(destPath, ocrWords: null, pngBytesForHash: rawBytes, usedAt: recordedUtc, appName: null, ttlSeconds: null);
            if (_settings.CopyEachScreenshot && !isVideo)
            {
                _clipboardBackend.TryWriteImage(destPath);
            }
            _onNewItem(destPath, true);
        }
    }

    private static byte[]? TryReadBytesShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= 0 || fs.Length > 128L * 1024 * 1024) return null;
            var buf = new byte[fs.Length];
            int offset = 0;
            while (offset < buf.Length)
            {
                int r = fs.Read(buf, offset, buf.Length - offset);
                if (r <= 0) break;
                offset += r;
            }
            return offset == buf.Length ? buf : buf[..offset];
        }
        catch (Exception)
        {
            return null;
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
