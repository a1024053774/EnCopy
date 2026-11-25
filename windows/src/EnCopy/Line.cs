using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace EnCopy;

public sealed record BasketRow(Note Note, DateTime UsedLocal);

public sealed record BasketGroup(string Title, IReadOnlyList<BasketRow> Rows);

/// <summary>
/// EnCopy 绳子与篮子状态模型（T-059）：
/// 绳结左侧放收藏卡，绳结右侧从新到旧挂普通卡；挂满后最老普通卡掉入篮子；
/// 支持种类筛选（Tab）、Shift+←/→ 多选、Ctrl+P 收藏、取下与 Ctrl+Z 原顺序放回、篮子按天/按月分组列表。
/// </summary>
public sealed class Line : INotifyPropertyChanged
{
    private const int MaxUndoSteps = 20;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public History History { get; }
    public ObservableCollection<Note> ShownItems { get; } = [];
    public IReadOnlyList<Note> LineItems => _lineItems;

    private readonly string _lineStatePath;
    private readonly List<Note> _lineItems = [];
    private readonly List<UndoStep> _undoSteps = [];
    private readonly List<BasketGroup> _basketGroups = [];
    private readonly Dictionary<KindFilter, int> _basketKindCounts = new();

    private string _query = string.Empty;
    private KindFilter _kindFilter = KindFilter.All;
    private Note? _picked;
    private string? _anchorPath;
    private HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _copiedFeedbackPath;
    private int _visibleCapacity = 8;
    private int? _keepOnLine;
    private double _scrollOffset;

    private int _matchCount;
    private int _totalCount;
    private int _basketCount;
    private int _basketMatchCount;
    private bool _basketOpen;
    private Note? _basketPicked;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Line(History history, int? keepOnLine = null, string? lineStatePath = null)
    {
        History = history;
        _keepOnLine = keepOnLine is { } k && AppSettings.KeepChoices.Contains(k) ? k : null;
        _lineStatePath = lineStatePath ?? DataFolder.LineStatePath;
        ReloadFromHistory();
    }

    public int VisibleCapacity
    {
        get => _visibleCapacity;
        private set
        {
            int next = Math.Max(3, value);
            if (_visibleCapacity != next)
            {
                _visibleCapacity = next;
                OnPropertyChanged();
            }
        }
    }

    public int? KeepOnLine
    {
        get => _keepOnLine;
        private set
        {
            if (_keepOnLine != value)
            {
                _keepOnLine = value;
                OnPropertyChanged();
            }
        }
    }

    public int EffectiveMaxItems => _keepOnLine ?? _visibleCapacity;

    public int PinnedCount => _lineItems.Count(n => n.Pinned);

    public string Query
    {
        get => _query;
        set
        {
            var next = value ?? string.Empty;
            if (_query == next) return;
            _query = next;
            OnPropertyChanged();
            RefreshCurrentView();
        }
    }

    public KindFilter KindFilter
    {
        get => _kindFilter;
        set
        {
            if (_kindFilter == value) return;
            _kindFilter = value;
            OnPropertyChanged();
            RefreshCurrentView();
        }
    }

    public Note? Picked
    {
        get => _picked;
        set
        {
            if (ReferenceEquals(_picked, value)) return;
            _picked = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlySet<string> SelectedPaths => _selectedPaths;

    public string? CopiedFeedbackPath
    {
        get => _copiedFeedbackPath;
        set
        {
            if (string.Equals(_copiedFeedbackPath, value, StringComparison.OrdinalIgnoreCase)) return;
            _copiedFeedbackPath = value;
            OnPropertyChanged();
        }
    }

    public double ScrollOffset
    {
        get => _scrollOffset;
        private set
        {
            if (Math.Abs(_scrollOffset - value) > 0.01)
            {
                _scrollOffset = value;
                OnPropertyChanged();
            }
        }
    }

    public int MatchCount
    {
        get => _matchCount;
        private set { if (_matchCount != value) { _matchCount = value; OnPropertyChanged(); } }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set { if (_totalCount != value) { _totalCount = value; OnPropertyChanged(); } }
    }

    public int BasketCount
    {
        get => _basketCount;
        private set { if (_basketCount != value) { _basketCount = value; OnPropertyChanged(); } }
    }

    public int BasketMatchCount
    {
        get => _basketMatchCount;
        private set { if (_basketMatchCount != value) { _basketMatchCount = value; OnPropertyChanged(); } }
    }

    public bool BasketOpen
    {
        get => _basketOpen;
        private set { if (_basketOpen != value) { _basketOpen = value; OnPropertyChanged(); } }
    }

    public Note? BasketPicked
    {
        get => _basketPicked;
        set
        {
            if (ReferenceEquals(_basketPicked, value)) return;
            _basketPicked = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<BasketGroup> BasketGroups => _basketGroups;
    public IReadOnlyDictionary<KindFilter, int> BasketKindCounts => _basketKindCounts;

    public bool IsSearching => SearchText.Words(_query).Count > 0;
    public bool CanPutBack => _undoSteps.Count > 0;

    public void SetVisibleCapacity(int count)
    {
        VisibleCapacity = count;
        TrimToCapacity();
        SaveLineState();
        RefreshCurrentView();
    }

    public void SetKeepOnLine(int? keepOnLine)
    {
        KeepOnLine = keepOnLine is { } k && AppSettings.KeepChoices.Contains(k) ? k : null;
        ScrollOffset = 0;
        TrimToCapacity();
        SaveLineState();
        RefreshCurrentView();
    }

    public static double TiltFor(string filePath)
    {
        uint hash = 2166136261u;
        foreach (var ch in filePath.ToLowerInvariant())
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        // 显式先转 int 再减 22，防止无符号下溢把角度卷绕成 ~180° 倒置卡片
        return (((int)(hash % 45)) - 22) * 0.1;
    }

    public void ReloadFromHistory()
    {
        var entries = History.AllEntries();
        var byPath = new Dictionary<string, History.StoredEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            byPath[Path.GetFullPath(e.FullPath)] = e;
        }

        _lineItems.Clear();
        var (saved, allowFallbackPersist) = ReadLineState();
        if (saved is not null)
        {
            var pinned = new List<Note>();
            var loose = new List<Note>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in saved.Items)
            {
                var abs = History.AbsolutePath(slot.Path);
                var full = Path.GetFullPath(abs);
                if (!seen.Add(full) || !byPath.TryGetValue(full, out var entry)) continue;
                var note = Note.Load(
                    entry.FullPath,
                    entry.RelativePath,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.UsedUnix * 1000)).LocalDateTime,
                    pinned: slot.Pinned,
                    appName: entry.AppName);
                if (note is null) continue;
                if (slot.Pinned) pinned.Add(note);
                else loose.Add(note);
            }
            _lineItems.AddRange(pinned);
            _lineItems.AddRange(loose);
        }
        else
        {
            foreach (var entry in entries.Take(EffectiveMaxItems))
            {
                var note = Note.Load(
                    entry.FullPath,
                    entry.RelativePath,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.UsedUnix * 1000)).LocalDateTime,
                    pinned: false,
                    appName: entry.AppName);
                if (note is not null)
                {
                    _lineItems.Add(note);
                }
            }
            if (allowFallbackPersist && _lineItems.Count > 0)
            {
                SaveLineState();
            }
        }

        TrimToCapacity();
        RefreshCurrentView();
    }

    public void AddNew(string filePath)
    {
        var rel = History.StoredKeyFor(filePath);
        if (rel is null) return;
        var note = Note.Load(filePath, rel, DateTime.Now, pinned: false);
        if (note is null) return;

        var full = Path.GetFullPath(filePath);
        _lineItems.RemoveAll(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
        int insertAt = PinnedCount;
        _lineItems.Insert(insertAt, note);
        TrimToCapacity(protectPaths: [full]);
        SaveLineState();
        ScrollOffset = 0;
        RefreshCurrentView();
        PickDefault();
    }

    /// <summary>
    /// 使用某件条目（粘贴、双击、单击复制或再次复制到同一内容）：
    /// 收藏件原地更新时间留在绳结左边；未收藏件（含从篮子召回的件）挪回绳结右侧第一格，绳满则挤出最老普通件进篮子。
    /// </summary>
    public void Use(string filePath, DateTime? usedAt = null)
    {
        var stampUtc = (usedAt ?? DateTime.UtcNow).ToUniversalTime();
        var stampLocal = stampUtc.ToLocalTime();
        var full = Path.GetFullPath(filePath);

        History.Use(filePath, stampUtc);

        int idx = _lineItems.FindIndex(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            var updated = _lineItems[idx].WithUsedAt(stampLocal);
            if (updated.Pinned)
            {
                _lineItems[idx] = updated;
            }
            else
            {
                _lineItems.RemoveAt(idx);
                _lineItems.Insert(PinnedCount, updated);
            }
        }
        else
        {
            var rel = History.StoredKeyFor(filePath);
            if (rel is not null && Note.Load(filePath, rel, stampLocal, pinned: false) is { } reclaimed)
            {
                _lineItems.Insert(PinnedCount, reclaimed);
                TrimToCapacity(protectPaths: [full]);
            }
        }

        SaveLineState();
        RefreshCurrentView();
    }

    public void PickNewest()
    {
        PickSingle(ShownItems.FirstOrDefault());
    }

    public void PickDefault()
    {
        var candidate = ShownItems.FirstOrDefault(n => !n.Pinned) ?? ShownItems.FirstOrDefault();
        PickSingle(candidate);
    }

    public void PickCard(Note? note, bool extendSelection = false)
    {
        if (note is null)
        {
            PickSingle(null);
            return;
        }
        if (!extendSelection || _anchorPath is null)
        {
            PickSingle(note);
            return;
        }
        SelectRangeBetween(_anchorPath, note.FilePath, activeNote: note);
    }

    public void MovePick(int step, bool extendSelection = false)
    {
        if (ShownItems.Count == 0)
        {
            PickSingle(null);
            return;
        }
        if (Picked is null)
        {
            PickDefault();
            return;
        }

        int idx = IndexInShown(Picked.FilePath);
        if (idx < 0)
        {
            PickSingle(ShownItems[0]);
            return;
        }

        int next = Math.Clamp(idx + step, 0, ShownItems.Count - 1);
        var nextNote = ShownItems[next];
        if (extendSelection)
        {
            var anchor = _anchorPath ?? Picked.FilePath;
            SelectRangeBetween(anchor, nextNote.FilePath, nextNote);
        }
        else
        {
            PickSingle(nextNote);
        }

        EnsureIndexVisible(next);
    }

    public IReadOnlyList<Note> GetSelectedNotesInLineOrder()
    {
        if (BasketOpen)
        {
            return BasketPicked is not null ? [BasketPicked] : [];
        }
        if (_selectedPaths.Count <= 1)
        {
            return Picked is not null ? [Picked] : [];
        }
        var ordered = ShownItems.Where(n => _selectedPaths.Contains(Path.GetFullPath(n.FilePath))).ToList();
        return ordered.Count > 0 ? ordered : (Picked is not null ? [Picked] : []);
    }

    public void CycleKindFilter(int step)
    {
        KindFilter[] order = [KindFilter.All, KindFilter.Text, KindFilter.Link, KindFilter.Image, KindFilter.File];
        int current = Array.IndexOf(order, _kindFilter);
        if (current < 0) current = 0;
        int next = (current + step % order.Length + order.Length) % order.Length;
        KindFilter = order[next];
    }

    public void ScrollBy(double deltaDip)
    {
        double maxScroll = MaxScrollOffset();
        ScrollOffset = Math.Clamp(_scrollOffset + deltaDip, 0.0, maxScroll);
    }

    public double MaxScrollOffset()
    {
        if (_keepOnLine is null || ShownItems.Count <= _visibleCapacity) return 0.0;
        return Math.Max(0, ShownItems.Count - _visibleCapacity) * ScreenPlacement.CardSpacing;
    }

    // MARK: Pin / Unpin (Ctrl+P)

    public void TogglePinPicked()
    {
        if (BasketOpen)
        {
            if (BasketPicked is { } basketNote)
            {
                TogglePinByPath(basketNote.FilePath);
                LoadBasket();
            }
            return;
        }

        if (Picked is { } picked)
        {
            TogglePinByPath(picked.FilePath);
        }
    }

    public void TogglePinByPath(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        int lineIdx = _lineItems.FindIndex(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
        if (lineIdx >= 0)
        {
            var current = _lineItems[lineIdx];
            bool nextPinned = !current.Pinned;
            _lineItems.RemoveAt(lineIdx);
            int targetIdx = PinnedCount;
            var updated = current.WithPinned(nextPinned);
            _lineItems.Insert(targetIdx, updated);

            PushUndoStep([
                new UndoEntry(
                    OriginalPath: current.FilePath,
                    StashedPath: null,
                    PlainText: current.FullText,
                    AppName: current.AppName,
                    OriginalUsedUtc: current.UsedAt.ToUniversalTime(),
                    WasOnLine: true,
                    WasPinned: current.Pinned,
                    OldScreenIndex: lineIdx,
                    IsPinChange: true)
            ]);

            SaveLineState();
            RefreshCurrentView();
            PickSingle(FindInShown(full) ?? updated);
            return;
        }

        // 从篮子收藏上绳：放到绳结左侧，并在超容量时把最老非收藏挤回篮子
        var rel = History.StoredKeyFor(filePath);
        if (rel is null) return;
        var entry = History.AllEntries().FirstOrDefault(e => string.Equals(Path.GetFullPath(e.FullPath), full, StringComparison.OrdinalIgnoreCase));
        var usedLocal = entry is not null
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.UsedUnix * 1000)).LocalDateTime
            : DateTime.Now;
        var loaded = Note.Load(filePath, rel, usedLocal, pinned: true, appName: entry?.AppName);
        if (loaded is null) return;

        int pinIdx = PinnedCount;
        _lineItems.Insert(pinIdx, loaded);
        TrimToCapacity(protectPaths: [full]);

        PushUndoStep([
            new UndoEntry(
                OriginalPath: loaded.FilePath,
                StashedPath: null,
                PlainText: loaded.FullText,
                AppName: loaded.AppName,
                OriginalUsedUtc: loaded.UsedAt.ToUniversalTime(),
                WasOnLine: false,
                WasPinned: false,
                OldScreenIndex: pinIdx,
                IsPinChange: true)
        ]);

        SaveLineState();
        RefreshCurrentView();
    }

    // MARK: Take Down (Ctrl+Backspace / Delete) & Put Back (Ctrl+Z)

    public bool TakeDownPicked()
    {
        var targets = GetSelectedNotesInLineOrder();
        if (targets.Count == 0) return false;

        // 预先记录移除前每个条目在绳上的原始槽位，确保多选 Ctrl+Z 放回时严格维持原从左到右顺序
        var preIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _lineItems.Count; i++)
        {
            preIndices[Path.GetFullPath(_lineItems[i].FilePath)] = i;
        }

        var prepared = new List<UndoEntry>(targets.Count);
        var stepId = Guid.NewGuid().ToString("N");
        var movedToStash = new List<(string Original, string Stashed)>();

        // 两阶段提交：先将全部私有文件移入暂存区，若中途发生 IOException 则原样退回，绝不损毁 History 或绳子状态
        try
        {
            foreach (var note in targets)
            {
                var full = Path.GetFullPath(note.FilePath);
                bool wasOnLine = preIndices.TryGetValue(full, out int preIndex);
                int liveIdx = wasOnLine
                    ? _lineItems.FindIndex(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase))
                    : -1;
                bool wasPinned = liveIdx >= 0 && _lineItems[liveIdx].Pinned;

                string? stashed = null;
                if (DataFolder.Contains(note.FilePath) && File.Exists(note.FilePath))
                {
                    Directory.CreateDirectory(DataFolder.DiscardedFolder);
                    var dest = Path.Combine(DataFolder.DiscardedFolder, $"{stepId}_{Path.GetFileName(note.FilePath)}");
                    File.Move(note.FilePath, dest, overwrite: true);
                    movedToStash.Add((note.FilePath, dest));
                    stashed = dest;
                }

                prepared.Add(new UndoEntry(
                    OriginalPath: note.FilePath,
                    StashedPath: stashed,
                    PlainText: note.FullText,
                    AppName: note.AppName,
                    OriginalUsedUtc: note.UsedAt.ToUniversalTime(),
                    WasOnLine: wasOnLine,
                    WasPinned: wasPinned,
                    OldScreenIndex: wasOnLine ? preIndex : 0,
                    IsPinChange: false));
            }
        }
        catch (IOException)
        {
            foreach (var (orig, stash) in movedToStash)
            {
                if (File.Exists(stash) && !File.Exists(orig))
                {
                    try { File.Move(stash, orig, overwrite: false); } catch { }
                }
            }
            return false;
        }

        if (prepared.Count == 0) return false;
        foreach (var entry in prepared)
        {
            History.Remove(entry.OriginalPath);
            if (entry.WasOnLine)
            {
                var full = Path.GetFullPath(entry.OriginalPath);
                _lineItems.RemoveAll(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
            }
        }

        PushUndoStep(prepared);
        SaveLineState();
        RefreshCurrentView();
        if (BasketOpen) LoadBasket();
        return true;
    }

    public bool PutBack()
    {
        if (_undoSteps.Count == 0) return false;
        var step = _undoSteps[^1];
        _undoSteps.RemoveAt(_undoSteps.Count - 1);
        OnPropertyChanged(nameof(CanPutBack));

        var restoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Note? firstRestored = null;

        foreach (var entry in step.Entries.OrderBy(e => e.OldScreenIndex))
        {
            var full = Path.GetFullPath(entry.OriginalPath);
            if (entry.IsPinChange)
            {
                int currentIdx = _lineItems.FindIndex(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
                if (currentIdx >= 0)
                {
                    var cur = _lineItems[currentIdx];
                    _lineItems.RemoveAt(currentIdx);
                    if (entry.WasOnLine)
                    {
                        var reverted = cur.WithPinned(entry.WasPinned);
                        int insertAt = Math.Clamp(entry.OldScreenIndex, 0, _lineItems.Count);
                        _lineItems.Insert(insertAt, reverted);
                        SortPinnedBeforeLoosePreservingRelativeOrder();
                        firstRestored ??= reverted;
                    }
                    restoredPaths.Add(full);
                }
                continue;
            }

            if (entry.StashedPath is not null)
            {
                if (!File.Exists(entry.StashedPath)) continue;
                var dir = Path.GetDirectoryName(entry.OriginalPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Move(entry.StashedPath, entry.OriginalPath, overwrite: true);
            }
            else if (!File.Exists(entry.OriginalPath) && !Directory.Exists(entry.OriginalPath))
            {
                continue;
            }

            History.Record(entry.OriginalPath, entry.PlainText, entry.OriginalUsedUtc, entry.AppName);
            var rel = History.StoredKeyFor(entry.OriginalPath);
            if (rel is null) continue;
            var loaded = Note.Load(entry.OriginalPath, rel, entry.OriginalUsedUtc.ToLocalTime(), entry.WasPinned, entry.AppName);
            if (loaded is null) continue;

            if (entry.WasOnLine)
            {
                _lineItems.RemoveAll(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
                int insertAt = Math.Clamp(entry.OldScreenIndex, 0, _lineItems.Count);
                _lineItems.Insert(insertAt, loaded);
                SortPinnedBeforeLoosePreservingRelativeOrder();
            }

            restoredPaths.Add(full);
            firstRestored ??= loaded;
        }

        if (restoredPaths.Count == 0) return false;

        TrimToCapacity(protectPaths: restoredPaths);
        SaveLineState();
        RefreshCurrentView();
        if (BasketOpen) LoadBasket();

        if (!BasketOpen && firstRestored is not null)
        {
            PickSingle(FindInShown(Path.GetFullPath(firstRestored.FilePath)) ?? firstRestored);
        }
        return true;
    }

    // MARK: Basket List (T-043 / T-059)

    public void OpenBasket()
    {
        if (BasketOpen) return;
        BasketOpen = true;
        _selectedPaths.Clear();
        _anchorPath = null;
        Picked = null;
        LoadBasket();
        BasketPicked = FirstBasketNote();
        RefreshCurrentView();
    }

    public void CloseBasket()
    {
        if (!BasketOpen) return;
        BasketOpen = false;
        _basketGroups.Clear();
        _basketKindCounts.Clear();
        BasketMatchCount = 0;
        BasketPicked = null;
        RefreshCurrentView();
    }

    public bool MoveBasketPick(int step)
    {
        var flat = FlattenBasketNotes();
        if (flat.Count == 0)
        {
            if (step < 0) return false;
            BasketPicked = null;
            return true;
        }
        int idx = BasketPicked is null
            ? -1
            : flat.FindIndex(n => string.Equals(Path.GetFullPath(n.FilePath), Path.GetFullPath(BasketPicked.FilePath), StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            if (step < 0) return false;
            BasketPicked = flat[0];
            return true;
        }
        int next = idx + step;
        if (next < 0)
        {
            return false;
        }
        BasketPicked = flat[Math.Min(flat.Count - 1, next)];
        return true;
    }

    public void MoveBasketGroup(int step)
    {
        if (_basketGroups.Count == 0) return;
        int currentGroupIdx = -1;
        if (BasketPicked is not null)
        {
            var pickedFull = Path.GetFullPath(BasketPicked.FilePath);
            currentGroupIdx = _basketGroups.FindIndex(g =>
                g.Rows.Any(r => string.Equals(Path.GetFullPath(r.Note.FilePath), pickedFull, StringComparison.OrdinalIgnoreCase)));
        }
        int targetIdx = currentGroupIdx < 0
            ? (step < 0 ? _basketGroups.Count - 1 : 0)
            : Math.Clamp(currentGroupIdx + step, 0, _basketGroups.Count - 1);
        BasketPicked = _basketGroups[targetIdx].Rows.FirstOrDefault()?.Note;
    }

    public void VerticalArrow(int step)
    {
        if (BasketOpen)
        {
            if (!MoveBasketPick(step))
            {
                CloseBasket();
                PickDefault();
            }
        }
        else if (step > 0 && BasketCount > 0)
        {
            OpenBasket();
        }
    }

    public static string BasketGroupTitle(DateTime usedLocal, DateTime? nowLocal = null)
    {
        var now = nowLocal ?? DateTime.Now;
        var today = now.Date;
        if (usedLocal.Date >= today) return Localization.L("Today");
        if (usedLocal.Date >= today.AddDays(-1)) return Localization.L("Yesterday");
        if (usedLocal.Date >= today.AddDays(-7)) return Localization.L("This week");
        return Localization.IsChinese
            ? usedLocal.ToString("yyyy年M月", CultureInfo.GetCultureInfo("zh-CN"))
            : usedLocal.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-US"));
    }

    public void LoadBasket(DateTime? nowLocal = null)
    {
        var onLine = OnLinePathSet();
        var words = SearchText.Words(_query);
        var allEntries = History.AllEntries();
        var allBasketEntries = allEntries
            .Where(e => !onLine.Contains(Path.GetFullPath(e.FullPath)))
            .ToList();

        BasketCount = allBasketEntries.Count;

        IReadOnlyList<History.StoredEntry> candidateEntries;
        if (words.Count == 0)
        {
            candidateEntries = allBasketEntries;
        }
        else
        {
            var found = History.Search(words, onLine.ToList(), limit: int.MaxValue);
            candidateEntries = found.Entries;
        }

        var rows = new List<BasketRow>(candidateEntries.Count);
        _basketKindCounts.Clear();
        _basketKindCounts[KindFilter.Text] = 0;
        _basketKindCounts[KindFilter.Link] = 0;
        _basketKindCounts[KindFilter.Image] = 0;
        _basketKindCounts[KindFilter.File] = 0;

        foreach (var entry in candidateEntries)
        {
            var usedDt = DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.UsedUnix * 1000)).LocalDateTime;
            var note = Note.Load(entry.FullPath, entry.RelativePath, usedDt, pinned: false, appName: entry.AppName);
            if (note is null) continue;

            var filterBucket = note.Kind switch
            {
                ItemKind.Code => KindFilter.Text,
                ItemKind.Video => KindFilter.Image,
                ItemKind.Link => KindFilter.Link,
                ItemKind.Image => KindFilter.Image,
                ItemKind.File => KindFilter.File,
                _ => KindFilter.Text
            };
            _basketKindCounts[filterBucket] = _basketKindCounts[filterBucket] + 1;
            rows.Add(new BasketRow(note, usedDt));
        }

        BasketMatchCount = rows.Count;
        var filtered = _kindFilter == KindFilter.All
            ? rows
            : rows.Where(r => r.Note.MatchesFilter(_kindFilter)).ToList();

        _basketGroups.Clear();
        string? currentTitle = null;
        var currentRows = new List<BasketRow>();
        foreach (var row in filtered)
        {
            var title = BasketGroupTitle(row.UsedLocal, nowLocal);
            if (currentTitle is not null && !string.Equals(title, currentTitle, StringComparison.Ordinal))
            {
                _basketGroups.Add(new BasketGroup(currentTitle, currentRows));
                currentRows = [];
            }
            currentTitle = title;
            currentRows.Add(row);
        }
        if (currentTitle is not null && currentRows.Count > 0)
        {
            _basketGroups.Add(new BasketGroup(currentTitle, currentRows));
        }

        var flat = FlattenBasketNotes();
        if (BasketPicked is null ||
            !flat.Any(n => string.Equals(Path.GetFullPath(n.FilePath), Path.GetFullPath(BasketPicked.FilePath), StringComparison.OrdinalIgnoreCase)))
        {
            BasketPicked = flat.FirstOrDefault();
        }
        OnPropertyChanged(nameof(BasketGroups));
        OnPropertyChanged(nameof(BasketKindCounts));
    }

    // MARK: Internals

    private void RefreshCurrentView()
    {
        var allEntries = History.AllEntries();
        var onLine = OnLinePathSet();
        TotalCount = allEntries.Count;
        BasketCount = allEntries.Count(e => !onLine.Contains(Path.GetFullPath(e.FullPath)));

        if (BasketOpen)
        {
            IReadOnlyList<Note> visibleLine = _lineItems;
            UpdateShownCollection(visibleLine);
            MatchCount = visibleLine.Count;
            _scrollOffset = Math.Clamp(_scrollOffset, 0.0, MaxScrollOffset());
            LoadBasket();
            return;
        }

        var words = SearchText.Words(_query);
        IReadOnlyList<Note> visible;
        if (words.Count == 0)
        {
            visible = _kindFilter == KindFilter.All
                ? _lineItems
                : _lineItems.Where(n => n.MatchesFilter(_kindFilter)).ToList();
            MatchCount = visible.Count;
        }
        else
        {
            var found = History.Search(words, onLine.ToList(), limit: 200);
            var matchedBasket = new List<Note>(found.Entries.Count);
            foreach (var entry in found.Entries)
            {
                var used = DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.UsedUnix * 1000)).LocalDateTime;
                var loaded = Note.Load(entry.FullPath, entry.RelativePath, used, pinned: false, appName: entry.AppName);
                if (loaded is not null && loaded.MatchesFilter(_kindFilter))
                {
                    matchedBasket.Add(loaded);
                }
            }
            var matchedOnLine = _lineItems
                .Where(n => found.OnLine.Contains(Path.GetFullPath(n.FilePath)) && n.MatchesFilter(_kindFilter))
                .ToList();

            var combined = new List<Note>(matchedOnLine.Count + matchedBasket.Count);
            combined.AddRange(matchedOnLine);
            combined.AddRange(matchedBasket);
            visible = combined;
            MatchCount = combined.Count;
        }

        UpdateShownCollection(visible);
        if (IsSearching || _kindFilter != KindFilter.All)
        {
            _scrollOffset = 0.0;
        }
        else
        {
            _scrollOffset = Math.Clamp(_scrollOffset, 0.0, MaxScrollOffset());
        }

        if (Picked is null || FindInShown(Path.GetFullPath(Picked.FilePath)) is null)
        {
            PickDefault();
        }
        else
        {
            var current = FindInShown(Path.GetFullPath(Picked.FilePath));
            if (current is not null)
            {
                Picked = current;
            }
        }
    }

    private void UpdateShownCollection(IReadOnlyList<Note> visible)
    {
        ShownItems.Clear();
        foreach (var n in visible)
        {
            ShownItems.Add(n);
        }
    }

    private void TrimToCapacity(HashSet<string>? protectPaths = null)
    {
        int limit = EffectiveMaxItems;
        while (_lineItems.Count > limit)
        {
            int victim = -1;
            for (int i = _lineItems.Count - 1; i >= 0; i--)
            {
                var item = _lineItems[i];
                if (item.Pinned) continue;
                var full = Path.GetFullPath(item.FilePath);
                if (protectPaths is not null && protectPaths.Contains(full)) continue;
                victim = i;
                break;
            }
            if (victim < 0) break;
            _lineItems.RemoveAt(victim);
        }
    }

    private void SortPinnedBeforeLoosePreservingRelativeOrder()
    {
        var pinned = _lineItems.Where(x => x.Pinned).ToList();
        var loose = _lineItems.Where(x => !x.Pinned).ToList();
        _lineItems.Clear();
        _lineItems.AddRange(pinned);
        _lineItems.AddRange(loose);
    }

    private void PushUndoStep(List<UndoEntry> entries)
    {
        _undoSteps.Add(new UndoStep(entries));
        if (_undoSteps.Count > MaxUndoSteps)
        {
            var dropped = _undoSteps[0];
            _undoSteps.RemoveAt(0);
            foreach (var e in dropped.Entries)
            {
                if (e.StashedPath is not null && File.Exists(e.StashedPath))
                {
                    try { File.Delete(e.StashedPath); } catch { }
                }
            }
        }
        OnPropertyChanged(nameof(CanPutBack));
    }

    private HashSet<string> OnLinePathSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in _lineItems)
        {
            set.Add(Path.GetFullPath(n.FilePath));
        }
        return set;
    }

    private void PickSingle(Note? note)
    {
        Picked = note;
        _selectedPaths.Clear();
        if (note is not null)
        {
            var full = Path.GetFullPath(note.FilePath);
            _selectedPaths.Add(full);
            _anchorPath = full;
        }
        else
        {
            _anchorPath = null;
        }
        OnPropertyChanged(nameof(SelectedPaths));
    }

    private void SelectRangeBetween(string anchorPath, string targetPath, Note activeNote)
    {
        int i1 = IndexInShown(anchorPath);
        int i2 = IndexInShown(targetPath);
        if (i1 < 0 || i2 < 0)
        {
            PickSingle(activeNote);
            return;
        }
        int start = Math.Min(i1, i2);
        int end = Math.Max(i1, i2);
        _selectedPaths.Clear();
        for (int i = start; i <= end; i++)
        {
            _selectedPaths.Add(Path.GetFullPath(ShownItems[i].FilePath));
        }
        Picked = activeNote;
        OnPropertyChanged(nameof(SelectedPaths));
    }

    private int IndexInShown(string path)
    {
        var full = Path.GetFullPath(path);
        for (int i = 0; i < ShownItems.Count; i++)
        {
            if (string.Equals(Path.GetFullPath(ShownItems[i].FilePath), full, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private Note? FindInShown(string fullPath)
    {
        int idx = IndexInShown(fullPath);
        return idx >= 0 ? ShownItems[idx] : null;
    }

    private void EnsureIndexVisible(int shownIndex)
    {
        if (_keepOnLine is null || ShownItems.Count <= _visibleCapacity) return;
        double spacing = ScreenPlacement.CardSpacing;
        int pinned = !IsSearching && KindFilter == KindFilter.All ? PinnedCount : 0;
        bool anchorPinned = pinned > 0 && pinned < _visibleCapacity - 1;
        if (anchorPinned && shownIndex < pinned)
        {
            return;
        }

        double leftEdge = (anchorPinned ? pinned : 0) * spacing;
        double rightEdge = (_visibleCapacity - 1) * spacing;
        double cardPos = shownIndex * spacing - _scrollOffset;
        if (cardPos < leftEdge)
        {
            ScrollOffset = Math.Clamp(shownIndex * spacing - leftEdge, 0.0, MaxScrollOffset());
        }
        else if (cardPos > rightEdge)
        {
            ScrollOffset = Math.Clamp(shownIndex * spacing - rightEdge, 0.0, MaxScrollOffset());
        }
    }

    private List<Note> FlattenBasketNotes() =>
        _basketGroups.SelectMany(g => g.Rows.Select(r => r.Note)).ToList();

    private Note? FirstBasketNote() =>
        _basketGroups.FirstOrDefault()?.Rows.FirstOrDefault()?.Note;

    private (SavedLineState? State, bool AllowFallbackPersist) ReadLineState()
    {
        if (!File.Exists(_lineStatePath))
        {
            return (null, true);
        }
        try
        {
            var json = File.ReadAllText(_lineStatePath, Utf8NoBom);
            return (JsonSerializer.Deserialize<SavedLineState>(json, JsonOptions), true);
        }
        catch (JsonException)
        {
            // 状态 JSON 损坏时允许按历史重建，不留死文件
            return (null, true);
        }
        catch (IOException)
        {
            // 文件瞬时被占用时不覆盖磁盘上的 LineState.json，保留用户收藏记录
            return (null, false);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, false);
        }
    }

    private void SaveLineState()
    {
        var dir = Path.GetDirectoryName(_lineStatePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var model = new SavedLineState
        {
            Items = _lineItems.Select(n => new SavedSlot
            {
                Path = History.StoredKeyFor(n.FilePath) ?? n.FilePath,
                Pinned = n.Pinned
            }).ToList()
        };
        var tmp = _lineStatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model, JsonOptions), Utf8NoBom);
        File.Move(tmp, _lineStatePath, overwrite: true);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class SavedLineState
    {
        public List<SavedSlot> Items { get; set; } = [];
    }

    private sealed class SavedSlot
    {
        public string Path { get; set; } = string.Empty;
        public bool Pinned { get; set; }
    }

    private sealed record UndoStep(List<UndoEntry> Entries);

    private sealed record UndoEntry(
        string OriginalPath,
        string? StashedPath,
        string PlainText,
        string? AppName,
        DateTime OriginalUsedUtc,
        bool WasOnLine,
        bool WasPinned,
        int OldScreenIndex,
        bool IsPinChange);
}
