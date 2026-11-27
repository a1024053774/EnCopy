using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Windows.Media.Ocr;
using WinRtBitmapAlphaMode = Windows.Graphics.Imaging.BitmapAlphaMode;
using WinRtBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WinRtBitmapInterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode;
using WinRtBitmapPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;
using WinRtBitmapTransform = Windows.Graphics.Imaging.BitmapTransform;
using WinRtColorManagementMode = Windows.Graphics.Imaging.ColorManagementMode;
using WinRtExifOrientationMode = Windows.Graphics.Imaging.ExifOrientationMode;
using WinRtLanguage = Windows.Globalization.Language;
using WpfBitmapCacheOption = System.Windows.Media.Imaging.BitmapCacheOption;
using WpfBitmapCreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions;
using WpfBitmapDecoder = System.Windows.Media.Imaging.BitmapDecoder;

namespace EnCopy;

public sealed record OcrLanguageStatus(
    string? ActiveLanguageTag,
    bool IsChinesePackMissing,
    bool HasAnyEngine);

public interface IOcrBackend
{
    IReadOnlyList<string> GetInstalledLanguageTags();
    Task<string> RecognizeImageAsync(string imagePath, string? preferredLanguageTag, CancellationToken cancellationToken = default);
}

public static class OcrLanguageResolver
{
    public static OcrLanguageStatus Resolve(IReadOnlyList<string> installedLanguageTags)
    {
        if (installedLanguageTags is null || installedLanguageTags.Count == 0)
        {
            return new OcrLanguageStatus(null, IsChinesePackMissing: true, HasAnyEngine: false);
        }

        // 优先精确选用 zh-Hans-CN，其次任意已安装的简体中文语言标签（如 zh-Hans / zh-CN / zh-SG）
        var exactZhHansCn = installedLanguageTags.FirstOrDefault(t =>
            string.Equals(t, "zh-Hans-CN", StringComparison.OrdinalIgnoreCase));
        if (exactZhHansCn is not null)
        {
            return new OcrLanguageStatus(exactZhHansCn, IsChinesePackMissing: false, HasAnyEngine: true);
        }

        var anySimplifiedZh = installedLanguageTags.FirstOrDefault(IsSimplifiedChineseTag);
        if (anySimplifiedZh is not null)
        {
            return new OcrLanguageStatus(anySimplifiedZh, IsChinesePackMissing: false, HasAnyEngine: true);
        }

        // 未装简体中文识别包时平稳回退已安装语言（优先英文，再取首个可用项），绝不静默丢弃识字能力
        var fallback = installedLanguageTags.FirstOrDefault(t => t.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                       ?? installedLanguageTags[0];
        return new OcrLanguageStatus(fallback, IsChinesePackMissing: true, HasAnyEngine: true);
    }

    public static OcrLanguageStatus DetectSystemStatus()
    {
        try
        {
            var tags = OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
            return Resolve(tags);
        }
        catch (Exception)
        {
            return new OcrLanguageStatus(null, IsChinesePackMissing: true, HasAnyEngine: false);
        }
    }

    private static bool IsSimplifiedChineseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        return tag.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "zh-SG", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 调用系统内置离线 Windows.Media.Ocr.OcrEngine 提取图片文字；
/// 对超大图先缩至 <= 2400px（避开 OcrEngine.MaxImageDimension 4096 上限），
/// 并拼接中文相邻单字词元以保证 SQLite FTS5 trigram 子串检索命中。
/// </summary>
public sealed class WinRtOcrBackend : IOcrBackend
{
    private const uint MaxDecodePixels = 2400;

    public IReadOnlyList<string> GetInstalledLanguageTags()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public async Task<string> RecognizeImageAsync(string imagePath, string? preferredLanguageTag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return string.Empty;
        }

        byte[] rawBytes;
        try
        {
            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= 0 || fs.Length > 64L * 1024 * 1024) return string.Empty;
            rawBytes = new byte[fs.Length];
            int offset = 0;
            while (offset < rawBytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await fs.ReadAsync(rawBytes.AsMemory(offset, rawBytes.Length - offset), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                offset += read;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return string.Empty;
        }

        try
        {
            OcrEngine? engine = null;
            if (!string.IsNullOrWhiteSpace(preferredLanguageTag))
            {
                try
                {
                    engine = OcrEngine.TryCreateFromLanguage(new WinRtLanguage(preferredLanguageTag));
                }
                catch (Exception)
                {
                    engine = null;
                }
            }
            engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                var firstLang = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
                if (firstLang is not null)
                {
                    engine = OcrEngine.TryCreateFromLanguage(firstLang);
                }
            }
            if (engine is null) return string.Empty;

            using var mem = new MemoryStream(rawBytes, writable: false);
            using var winRtStream = mem.AsRandomAccessStream();
            var decoder = await WinRtBitmapDecoder.CreateAsync(winRtStream).AsTask(cancellationToken).ConfigureAwait(false);
            uint pw = decoder.PixelWidth;
            uint ph = decoder.PixelHeight;
            if (pw == 0 || ph == 0) return string.Empty;

            uint maxDim = Math.Min(MaxDecodePixels, OcrEngine.MaxImageDimension);
            var transform = new WinRtBitmapTransform();
            uint longEdge = Math.Max(pw, ph);
            if (longEdge > maxDim)
            {
                double ratio = (double)maxDim / longEdge;
                transform.ScaledWidth = Math.Max(1u, (uint)Math.Round(pw * ratio));
                transform.ScaledHeight = Math.Max(1u, (uint)Math.Round(ph * ratio));
                transform.InterpolationMode = WinRtBitmapInterpolationMode.Fant;
            }

            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                WinRtBitmapPixelFormat.Bgra8,
                WinRtBitmapAlphaMode.Premultiplied,
                transform,
                WinRtExifOrientationMode.RespectExifOrientation,
                WinRtColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

            var primaryResult = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);
            var primaryText = FormatOcrResult(primaryResult);

            // 若主引擎为中文且系统同时装有英文识别包，复用同一内存 SoftwareBitmap 补跑一次英文模型（~4ms，零额外解码 I/O），
            // 消除中文模型将英文 CO/O/0 误判为全角括号「（0」的固有偏差，对齐 Vision 双语联合识字质量
            if (!string.IsNullOrWhiteSpace(preferredLanguageTag) &&
                preferredLanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                var enLang = OcrEngine.AvailableRecognizerLanguages
                    .FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                if (enLang is not null && OcrEngine.TryCreateFromLanguage(enLang) is { } enEngine)
                {
                    var enResult = await enEngine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);
                    var enText = FormatOcrResult(enResult);
                    if (!string.IsNullOrWhiteSpace(enText) && !string.Equals(enText, primaryText, StringComparison.Ordinal))
                    {
                        return string.IsNullOrWhiteSpace(primaryText) ? enText : $"{primaryText}\n{enText}";
                    }
                }
            }

            return primaryText;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public static string FormatOcrResult(OcrResult? result)
    {
        if (result?.Lines is not { Count: > 0 } lines) return string.Empty;

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Words is not { Count: > 0 } words)
            {
                if (!string.IsNullOrWhiteSpace(line.Text))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(line.Text.Trim());
                }
                continue;
            }

            var lineBuilder = new StringBuilder();
            var compactBuilder = new StringBuilder();
            bool hasMixedBoundary = false;

            for (int i = 0; i < words.Count; i++)
            {
                var token = words[i].Text?.Trim() ?? string.Empty;
                if (token.Length == 0) continue;

                if (lineBuilder.Length > 0)
                {
                    var prev = words[i - 1];
                    var prevText = prev.Text?.Trim() ?? string.Empty;
                    bool prevCjk = EndsWithCjk(prevText);
                    bool currCjk = StartsWithCjk(token);
                    double avgHeight = Math.Max(1.0, (prev.BoundingRect.Height + words[i].BoundingRect.Height) * 0.5);
                    double gap = words[i].BoundingRect.Left - prev.BoundingRect.Right;

                    if (prevCjk && currCjk)
                    {
                        // Windows.Media.Ocr 会把每个汉字切成独立 OcrWord，相邻汉字绝不插空格，保证 trigram 连续匹配
                    }
                    else if (prevCjk != currCjk)
                    {
                        hasMixedBoundary = true;
                        if (gap > avgHeight * 0.30)
                        {
                            lineBuilder.Append(' ');
                        }
                    }
                    else
                    {
                        // zh-Hans-CN 引擎同样会将英文单词与数字按单字符拆成独立 OcrWord，按真实字距决定是否保留单词空格
                        double spaceThreshold = (prevText.Length == 1 && token.Length == 1)
                            ? avgHeight * 0.36
                            : avgHeight * 0.25;
                        if (gap > spaceThreshold)
                        {
                            lineBuilder.Append(' ');
                            compactBuilder.Append(' ');
                        }
                    }
                }

                lineBuilder.Append(token);
                compactBuilder.Append(token);
            }

            var assembled = lineBuilder.ToString().Trim();
            if (assembled.Length == 0) continue;

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(assembled);

            if (hasMixedBoundary)
            {
                var compact = compactBuilder.ToString().Trim();
                if (!string.Equals(compact, assembled, StringComparison.Ordinal))
                {
                    sb.Append('\n').Append(compact);
                }
            }
        }

        return sb.ToString();
    }

    private static bool StartsWithCjk(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var rune in text.EnumerateRunes())
        {
            return IsCjkRune(rune);
        }
        return false;
    }

    private static bool EndsWithCjk(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        Rune last = default;
        bool any = false;
        foreach (var rune in text.EnumerateRunes())
        {
            last = rune;
            any = true;
        }
        return any && IsCjkRune(last);
    }

    private static bool IsCjkRune(Rune r)
    {
        int v = r.Value;
        return (v is >= 0x4E00 and <= 0x9FFF) ||
               (v is >= 0x3400 and <= 0x4DBF) ||
               (v is >= 0x3000 and <= 0x303F) ||
               (v is >= 0xFF00 and <= 0xFFEF) ||
               (v is >= 0x20000 and <= 0x2FA1F);
    }
}

/// <summary>
/// 后台低优先级离线图片文字识别队列与缓存（对应 Mac 版 SearchIndex.swift，T-062）：
/// 对图片文件大小、像素宽高与最后修改时间做持久化缓存避免重复识别；
/// 识别结果折叠写入 History.sqlite FTS5 索引，原图缩成缩略图后依然保留原图识别词。
/// </summary>
public sealed class SearchIndex : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    private sealed class CacheEntry
    {
        public long FileSize { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Words { get; set; } = string.Empty;
    }

    private readonly History _history;
    private readonly IOcrBackend _backend;
    private readonly Action<string>? _onImageIndexed;
    private readonly string _cachePath;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queuedOrRunning = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;

    private int _pendingWorkCount;
    private int _backendRecognitionCount;
    private bool _cacheDirty;
    private bool _disposed;
    private TaskCompletionSource<bool>? _idleTcs;

    public OcrLanguageStatus LanguageStatus { get; }

    public int BackendRecognitionCount => Volatile.Read(ref _backendRecognitionCount);

    public SearchIndex(
        History history,
        IOcrBackend? backend = null,
        Action<string>? onImageIndexed = null,
        string? cachePath = null)
    {
        _history = history;
        _backend = backend ?? new WinRtOcrBackend();
        _onImageIndexed = onImageIndexed;
        _cachePath = cachePath ?? DataFolder.SearchIndexPath;
        LanguageStatus = OcrLanguageResolver.Resolve(_backend.GetInstalledLanguageTags());

        LoadCache();
        _workerTask = Task.Factory.StartNew(
            WorkerLoopAsync,
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    public static bool IsRecognizableImagePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && ImageExtensions.Contains(Path.GetExtension(path));

    public void ScanUnindexedImages()
    {
        foreach (var entry in _history.AllEntriesWithMeta())
        {
            if (IsRecognizableImagePath(entry.FullPath) && File.Exists(entry.FullPath))
            {
                Enqueue(entry.FullPath);
            }
        }
    }

    public void Enqueue(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !IsRecognizableImagePath(imagePath))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(imagePath);
        }
        catch (ArgumentException)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_disposed || !_queuedOrRunning.Add(full))
            {
                return;
            }
            _pendingWorkCount++;
            if (_idleTcs is { Task.IsCompleted: true })
            {
                _idleTcs = null;
            }
        }

        if (!_channel.Writer.TryWrite(full))
        {
            CompleteOne(full);
        }
    }

    public async Task FlushAsync(TimeSpan? timeout = null)
    {
        Task waitTask;
        lock (_stateLock)
        {
            if (_pendingWorkCount == 0)
            {
                SaveCacheIfDirty();
                return;
            }
            _idleTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            waitTask = _idleTcs.Task;
        }

        var limit = timeout ?? TimeSpan.FromSeconds(15);
        await waitTask.WaitAsync(limit).ConfigureAwait(false);
        SaveCacheIfDirty();
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            // 在后台线程保持较低调度优先级，避免批量截图入队时抢占前台交互
            if (Thread.CurrentThread.IsThreadPoolThread == false)
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
            }

            while (await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var fullPath))
                {
                    try
                    {
                        await ProcessOneAsync(fullPath, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // 单张损坏图片绝不中断后续识别队列
                    }
                    finally
                    {
                        CompleteOne(fullPath);
                    }
                }
                SaveCacheIfDirty();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessOneAsync(string fullPath, CancellationToken ct)
    {
        if (!File.Exists(fullPath)) return;

        // 已缩成缩略图且历史里已有原图识别词时绝不重跑，防止模糊小图覆盖高清原图识字结果
        if (_history.IsShrunk(fullPath) && !string.IsNullOrWhiteSpace(_history.OcrWordsOf(fullPath)))
        {
            return;
        }

        if (!TryReadImageFingerprint(fullPath, out long fileSize, out int pw, out int ph, out long writeTicks))
        {
            return;
        }

        var cacheKey = _history.StoredKeyFor(fullPath) ?? fullPath;
        CacheEntry? hit = null;
        lock (_stateLock)
        {
            if (_cache.TryGetValue(cacheKey, out var existing) &&
                existing.FileSize == fileSize &&
                existing.PixelWidth == pw &&
                existing.PixelHeight == ph &&
                existing.LastWriteUtcTicks == writeTicks)
            {
                hit = existing;
            }
        }

        if (hit is not null)
        {
            if (_history.OcrWordsOf(fullPath) is null)
            {
                if (_history.UpdateImageOcr(fullPath, hit.Words))
                {
                    _onImageIndexed?.Invoke(fullPath);
                }
            }
            return;
        }

        if (!LanguageStatus.HasAnyEngine)
        {
            return;
        }

        Interlocked.Increment(ref _backendRecognitionCount);
        var recognized = await _backend.RecognizeImageAsync(fullPath, LanguageStatus.ActiveLanguageTag, ct).ConfigureAwait(false);

        // 识别耗时期间若原图已被缩图且原词仍在，UpdateImageOcr 内部也会拒绝覆盖
        lock (_stateLock)
        {
            _cache[cacheKey] = new CacheEntry
            {
                FileSize = fileSize,
                PixelWidth = pw,
                PixelHeight = ph,
                LastWriteUtcTicks = writeTicks,
                Words = recognized
            };
            _cacheDirty = true;
        }

        if (_history.UpdateImageOcr(fullPath, recognized))
        {
            _onImageIndexed?.Invoke(fullPath);
        }
    }

    private static bool TryReadImageFingerprint(string fullPath, out long fileSize, out int pixelWidth, out int pixelHeight, out long lastWriteTicks)
    {
        fileSize = 0;
        pixelWidth = 0;
        pixelHeight = 0;
        lastWriteTicks = 0;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length <= 0) return false;
            fileSize = info.Length;
            lastWriteTicks = info.LastWriteTimeUtc.Ticks;

            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var dec = WpfBitmapDecoder.Create(fs, WpfBitmapCreateOptions.DelayCreation, WpfBitmapCacheOption.None);
            if (dec.Frames.Count == 0) return false;
            pixelWidth = dec.Frames[0].PixelWidth;
            pixelHeight = dec.Frames[0].PixelHeight;
            return pixelWidth > 0 && pixelHeight > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void CompleteOne(string fullPath)
    {
        TaskCompletionSource<bool>? toComplete = null;
        lock (_stateLock)
        {
            _queuedOrRunning.Remove(fullPath);
            if (_pendingWorkCount > 0)
            {
                _pendingWorkCount--;
            }
            if (_pendingWorkCount == 0 && _idleTcs is not null)
            {
                toComplete = _idleTcs;
                _idleTcs = null;
            }
        }
        toComplete?.TrySetResult(true);
    }

    private void LoadCache()
    {
        if (!File.Exists(_cachePath)) return;
        try
        {
            var json = File.ReadAllText(_cachePath, Utf8NoBom);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json, JsonOptions);
            if (loaded is null) return;
            lock (_stateLock)
            {
                foreach (var kv in loaded)
                {
                    _cache[kv.Key] = kv.Value;
                }
            }
        }
        catch (Exception)
        {
            // 缓存损坏时重建，不影响主历史库
        }
    }

    private void SaveCacheIfDirty()
    {
        Dictionary<string, CacheEntry>? snapshot = null;
        lock (_stateLock)
        {
            if (!_cacheDirty) return;
            snapshot = new Dictionary<string, CacheEntry>(_cache, StringComparer.OrdinalIgnoreCase);
            _cacheDirty = false;
        }

        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions), Utf8NoBom);
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _workerTask.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception)
        {
        }
        SaveCacheIfDirty();
        _cts.Dispose();
    }
}
