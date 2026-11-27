using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EnCopy;

public sealed class ClearedEntry
{
    public string Path { get; set; } = string.Empty;
    public double Used { get; set; }
    public string? App { get; set; }
    public string? Words { get; set; }
    public bool Shrunk { get; set; }
}

public sealed class ClearedBatch
{
    public string Id { get; set; } = string.Empty;
    public DateTime DateUtc { get; set; }
    /// <summary>"expired" 或 "manual"。</summary>
    public string Reason { get; set; } = "manual";
    public List<ClearedEntry> Entries { get; set; } = [];
}

/// <summary>
/// 「最近清掉」（Recently Cleared，对齐 Mac 版 Clearing.swift）：
/// 自动到期或手动清理的条目按批次移入此处保留 7 天，保存原路径、使用时间、来源应用、识别词与缩略图标记，可整批放回。
/// </summary>
public sealed class Clearing
{
    public const double KeepDays = 7.0;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Folder { get; }

    public Clearing(string? folder = null)
    {
        Folder = folder ?? DataFolder.RecentlyClearedFolder;
    }

    public ClearedBatch? Clear(IReadOnlyList<string> fullPaths, string reason, History history, DateTime? nowUtc = null)
    {
        if (fullPaths.Count == 0) return null;
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        long ms = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        var batchId = $"{ms}-{Guid.NewGuid().ToString("N")[..6]}";
        var batchDir = Path.Combine(Folder, batchId);

        var metaByFull = new Dictionary<string, History.KeptEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in history.AllEntriesWithMeta())
        {
            metaByFull[Path.GetFullPath(k.FullPath)] = k;
        }

        var entries = new List<ClearedEntry>();
        foreach (var rawPath in fullPaths)
        {
            var full = Path.GetFullPath(rawPath);
            var rel = history.RelativePath(full);
            if (rel is null)
            {
                // 非私有目录的外部条目绝不搬移原文件，仅清掉历史记录行
                history.Remove(full);
                continue;
            }
            if (!File.Exists(full))
            {
                history.Remove(full);
                continue;
            }

            var target = Path.Combine(batchDir, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                metaByFull.TryGetValue(full, out var info);
                bool isNoteText = IsTextFile(full);
                var words = isNoteText ? null : history.WordsOf(full);

                File.Move(full, target, overwrite: true);
                history.Remove(full);
                entries.Add(new ClearedEntry
                {
                    Path = rel,
                    Used = info?.UsedUnix ?? (ms / 1000.0),
                    App = info?.AppName,
                    Words = words,
                    Shrunk = info?.Shrunk ?? false
                });
            }
            catch (IOException)
            {
                // 单文件被锁定时跳过该项，保留在原处
            }
        }

        if (entries.Count == 0)
        {
            if (Directory.Exists(batchDir))
            {
                try { Directory.Delete(batchDir, recursive: true); } catch (IOException) { }
            }
            return null;
        }

        var batch = new ClearedBatch
        {
            Id = batchId,
            DateUtc = now,
            Reason = reason,
            Entries = entries
        };

        Directory.CreateDirectory(batchDir);
        var manifestPath = Path.Combine(batchDir, "batch.json");
        var tmp = manifestPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(batch, JsonOptions), Utf8NoBom);
        File.Move(tmp, manifestPath, overwrite: true);
        return batch;
    }

    public IReadOnlyList<ClearedBatch> Batches()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<ClearedBatch>();
        foreach (var dir in Directory.EnumerateDirectories(Folder))
        {
            var manifest = Path.Combine(dir, "batch.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                var json = File.ReadAllText(manifest, Utf8NoBom);
                var batch = JsonSerializer.Deserialize<ClearedBatch>(json, JsonOptions);
                if (batch is not null) list.Add(batch);
            }
            catch (Exception)
            {
                // 跳过损坏批次文件
            }
        }
        return list.OrderByDescending(b => b.DateUtc).ToArray();
    }

    public IReadOnlyList<string> PutBack(string batchId, History history)
    {
        var batch = Batches().FirstOrDefault(b => string.Equals(b.Id, batchId, StringComparison.Ordinal));
        if (batch is null) return [];

        var batchDir = Path.Combine(Folder, batch.Id);
        var restored = new List<string>();
        var remaining = new List<ClearedEntry>();

        foreach (var entry in batch.Entries)
        {
            var from = Path.Combine(batchDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(from)) continue;

            var dest = NextFreePath(history.AbsolutePath(entry.Path));
            try
            {
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Move(from, dest, overwrite: false);
            }
            catch (IOException)
            {
                remaining.Add(entry);
                continue;
            }

            var usedUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.Used * 1000.0)).UtcDateTime;
            if (IsTextFile(dest))
            {
                history.Record(dest, plainText: null, usedAt: usedUtc, appName: entry.App);
            }
            else if (FileList.IsManifestPath(dest))
            {
                history.RecordFileList(dest, appName: entry.App, ttlSeconds: null, usedAt: usedUtc);
            }
            else
            {
                history.RecordImage(dest, ocrWords: entry.Words ?? string.Empty, pngBytesForHash: null, usedAt: usedUtc, appName: entry.App, ttlSeconds: null);
                if (entry.Shrunk)
                {
                    history.SetShrunk(dest, true);
                }
            }
            restored.Add(dest);
        }

        if (remaining.Count == 0)
        {
            try { Directory.Delete(batchDir, recursive: true); } catch (IOException) { }
        }
        else
        {
            batch.Entries = remaining;
            var manifestPath = Path.Combine(batchDir, "batch.json");
            var tmp = manifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(batch, JsonOptions), Utf8NoBom);
            File.Move(tmp, manifestPath, overwrite: true);
        }

        return restored;
    }

    public void Purge(DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        foreach (var batch in Batches())
        {
            if ((now - batch.DateUtc).TotalSeconds > KeepDays * 86400.0)
            {
                var dir = Path.Combine(Folder, batch.Id);
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }
    }

    internal static bool IsTextFile(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".rtf", StringComparison.OrdinalIgnoreCase);
    }

    private static string NextFreePath(string candidate)
    {
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var dir = Path.GetDirectoryName(candidate) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);
        int n = 2;
        string next;
        do
        {
            next = Path.Combine(dir, $"{stem} {n}{ext}");
            n++;
        } while (File.Exists(next) || Directory.Exists(next));
        return next;
    }
}

public sealed record RetentionStatus(long OriginalsBytes, long CapBytes, int WillExpireCount, int WillShrinkCount);

public sealed record RetentionPlan(
    IReadOnlyList<string> ExpiredPaths,
    IReadOnlyList<string> ShrunkPaths,
    IReadOnlyList<string> MissingPaths);

/// <summary>
/// 自动保留规则（T-004 / T-061）：
/// 不碰挂在绳上或收藏的项；文字/链接/代码/缩略图/有效文件清单留 1 年，原文件全不在的清单立即到期；
/// EnCopy 私有原图满 30 天或超过容量上限（默认 1 GB，从最旧原图起）缩成 640px 缩略图并保留已识别文字。
/// </summary>
public static class Retention
{
    public const double TextDays = 365.0;
    public const double OriginalImageDays = 30.0;
    public const long DefaultCapBytes = 1L << 30;
    public const int ThumbnailMaxPixels = 640;

    public static RetentionStatus GetStatus(
        History history,
        IReadOnlySet<string> onLinePaths,
        DateTime? nowUtc = null,
        long capBytes = DefaultCapBytes)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var plan = Plan(history, onLinePaths, now, capBytes);
        long originalsBytes = 0;
        foreach (var item in history.KeptEntries())
        {
            if (!item.Shrunk && IsPrivateImage(item.FullPath) && File.Exists(item.FullPath))
            {
                originalsBytes += SafeFileLength(item.FullPath);
            }
        }
        return new RetentionStatus(originalsBytes, capBytes, plan.ExpiredPaths.Count, plan.ShrunkPaths.Count);
    }

    public static RetentionPlan Plan(
        History history,
        IReadOnlySet<string> onLinePaths,
        DateTime? nowUtc = null,
        long capBytes = DefaultCapBytes)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        double nowSec = new DateTimeOffset(now).ToUnixTimeMilliseconds() / 1000.0;

        var expired = new List<string>();
        var shrunk = new List<string>();
        var missing = new List<string>();
        var keptOriginals = new List<(string Path, double UsedUnix, long Size)>();

        foreach (var item in history.KeptEntries())
        {
            var full = Path.GetFullPath(item.FullPath);
            if (onLinePaths.Contains(full)) continue;

            double ageSeconds = nowSec - item.UsedUnix;
            bool isPrivImg = IsPrivateImage(full);

            if (Clearing.IsTextFile(full) || (isPrivImg && item.Shrunk))
            {
                if (!File.Exists(full))
                {
                    missing.Add(full);
                }
                else if (ageSeconds > TextDays * 86400.0)
                {
                    expired.Add(full);
                }
            }
            else if (FileList.IsManifestPath(full))
            {
                if (!File.Exists(full))
                {
                    missing.Add(full);
                }
                else if (ageSeconds > TextDays * 86400.0 || FileList.AreAllTargetsMissing(full))
                {
                    expired.Add(full);
                }
            }
            else if (isPrivImg)
            {
                if (!File.Exists(full))
                {
                    missing.Add(full);
                    continue;
                }
                if (ageSeconds > OriginalImageDays * 86400.0)
                {
                    shrunk.Add(full);
                }
                else
                {
                    keptOriginals.Add((full, item.UsedUnix, SafeFileLength(full)));
                }
            }
            else if (!File.Exists(full) && !Directory.Exists(full))
            {
                missing.Add(full);
            }
            else if (ageSeconds > TextDays * 86400.0)
            {
                expired.Add(full);
            }
        }

        long totalBytes = keptOriginals.Sum(o => o.Size);
        foreach (var oldest in keptOriginals.OrderBy(o => o.UsedUnix))
        {
            if (totalBytes <= capBytes) break;
            shrunk.Add(oldest.Path);
            totalBytes -= oldest.Size;
        }

        return new RetentionPlan(expired, shrunk, missing);
    }

    public static RetentionPlan Run(
        History history,
        Clearing clearing,
        IReadOnlySet<string> onLinePaths,
        DateTime? nowUtc = null,
        long capBytes = DefaultCapBytes)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var plan = Plan(history, onLinePaths, now, capBytes);
        foreach (var m in plan.MissingPaths)
        {
            history.Remove(m);
        }
        foreach (var imgPath in plan.ShrunkPaths)
        {
            ShrinkImageInPlace(imgPath, history);
        }
        if (plan.ExpiredPaths.Count > 0)
        {
            clearing.Clear(plan.ExpiredPaths, reason: "expired", history, now);
        }
        clearing.Purge(now);
        return plan;
    }

    public static bool ShrinkImageInPlace(string path, History history, int maxPixelEdge = ThumbnailMaxPixels)
    {
        if (!File.Exists(path) || !DataFolder.Contains(path)) return false;
        try
        {
            var rawBytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(rawBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return false;

            BitmapSource source = decoder.Frames[0];
            int maxDim = Math.Max(source.PixelWidth, source.PixelHeight);
            if (maxDim > maxPixelEdge && maxPixelEdge > 0)
            {
                double scale = (double)maxPixelEdge / maxDim;
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
                source = scaled;
            }

            var ext = Path.GetExtension(path);
            BitmapEncoder encoder = string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 82 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            var tmp = path + ".shrunk.tmp";
            using (var outStream = File.Create(tmp))
            {
                encoder.Save(outStream);
            }
            File.Move(tmp, path, overwrite: true);
            history.MarkShrunk(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsPrivateImage(string fullPath)
    {
        if (!DataFolder.Contains(fullPath)) return false;
        var ext = Path.GetExtension(fullPath);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    public static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";
        double gb = mb / 1024.0;
        return $"{gb:0.##} GB";
    }
}

public sealed record CleanupFilter(
    KindFilter Kind = KindFilter.All,
    double? OlderThanDays = 30.0,
    string? App = null,
    string QueryText = "",
    bool IncludePinnedAndOnLine = false);

public sealed record CleanupPreview(IReadOnlyList<string> CandidatePaths, long EstimatedFreedBytes);
