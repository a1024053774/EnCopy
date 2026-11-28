using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace EnCopy;

public sealed record BasketRow(Note Note, DateTime UsedLocal, bool MatchedImageText = false);

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
    public Clearing Clearing { get; }
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
    private string? _basketExpandedPath;
    private string? _basketSubPickedFile;
    private bool _showsSourceApp = true;
    private LineTheme _theme = LineTheme.Paper;
    private readonly HashSet<string> _heldPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _matchedImageTextPaths = new(StringComparer.OrdinalIgnoreCase);

    public LineTheme Theme
    {
        get => _theme;
        set { if (_theme != value) { _theme = value; OnPropertyChanged(); } }
    }

    public bool ShowsSourceApp
    {
        get => _showsSourceApp;
        set { if (_showsSourceApp != value) { _showsSourceApp = value; OnPropertyChanged(); } }
    }

    public string? BasketExpandedPath => _basketExpandedPath;
    public string? BasketSubPickedFile => _basketSubPickedFile;

    public IReadOnlySet<string> HeldPaths => _heldPaths;

    public bool IsImageTextMatch(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath) && _matchedImageTextPaths.Contains(Path.GetFullPath(filePath));

    public void RefreshAfterIndexUpdate()
    {
        RefreshCurrentView();
        OnPropertyChanged(nameof(ShownItems));
    }

    public void SetHeld(string filePath, bool held)
    {
        var full = Path.GetFullPath(filePath);
        bool changed = held ? _heldPaths.Add(full) : _heldPaths.Remove(full);
        if (!changed) return;
        if (held && _selectedPaths.Remove(full))
        {
            OnPropertyChanged(nameof(SelectedPaths));
        }
        OnPropertyChanged(nameof(HeldPaths));
    }

    public IReadOnlyList<Note> Neighbours(string filePath, bool toTheLeft)
    {
        int idx = IndexInShown(filePath);
        if (idx < 0) return [];
        var list = new List<Note>();
        if (toTheLeft)
        {
            for (int i = 0; i < idx; i++)
            {
                if (!ShownItems[i].Pinned) list.Add(ShownItems[i]);
            }
        }
        else
        {
            for (int i = idx + 1; i < ShownItems.Count; i++)
            {
                if (!ShownItems[i].Pinned) list.Add(ShownItems[i]);
            }
        }
        return list;
    }

    public bool TakeDownNeighbours(string filePath, bool toTheLeft)
    {
        var targets = Neighbours(filePath, toTheLeft);
        return TakeDownNotes(targets);
    }

    public void PruneMissingFromLine(DateTime? minRecycleDeletedUtc = null)
    {
        var missing = _lineItems.Where(n => !File.Exists(n.FilePath) && !Directory.Exists(n.FilePath)).ToList();
        if (missing.Count == 0) return;

        var preIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _lineItems.Count; i++)
        {
            preIndices[Path.GetFullPath(_lineItems[i].FilePath)] = i;
        }

        var cutoffUtc = minRecycleDeletedUtc ?? DateTime.UtcNow.AddSeconds(-15);
        var recycledUndoEntries = new List<UndoEntry>();
        bool heldChanged = false;
        foreach (var item in missing)
        {
            var full = Path.GetFullPath(item.FilePath);
            if (!item.Pinned && DataFolder.IsMovablePrivateItem(item.FilePath) &&
                RecycleBinInterop.TryFindRecycledFile(full, cutoffUtc) is { } recycledPath)
            {
                recycledUndoEntries.Add(new UndoEntry(
                    OriginalPath: item.FilePath,
                    StashedPath: recycledPath,
                    PlainText: item.FullText,
                    AppName: item.AppName,
                    OriginalUsedUtc: item.UsedAt.ToUniversalTime(),
                    WasOnLine: true,
                    WasPinned: false,
                    OldScreenIndex: preIndices.GetValueOrDefault(full, 0),
                    IsPinChange: false,
                    InSystemRecycleBin: true,
                    OcrWords: History.OcrWordsOf(item.FilePath),
                    WasShrunk: History.IsShrunk(item.FilePath),
                    OriginalTtl: History.GetTtl(item.FilePath)));
            }

            if (_heldPaths.Remove(full)) heldChanged = true;
            if (DataFolder.Contains(item.FilePath))
            {
                History.Remove(item.FilePath);
            }
            _lineItems.RemoveAll(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
        }

        if (recycledUndoEntries.Count > 0)
        {
            PushUndoStep(recycledUndoEntries);
        }
        if (heldChanged)
        {
            OnPropertyChanged(nameof(HeldPaths));
        }
        SaveLineState();
        RefreshCurrentView();
        if (BasketOpen) LoadBasket();
    }

    public IReadOnlyList<string> SaveToDesktop(IReadOnlyList<Note> notes, string? desktopFolderOverride = null)
    {
        var desktop = desktopFolderOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop)) return [];
        Directory.CreateDirectory(desktop);

        var written = new List<string>();
        foreach (var note in notes)
        {
            var sources = note.FileTargets.Count > 0 ? note.FileTargets : [note.FilePath];
            foreach (var src in sources)
            {
                if (!File.Exists(src)) continue;
                var dest = NextFreePath(Path.Combine(desktop, Path.GetFileName(src)));
                File.Copy(src, dest, overwrite: false);
                written.Add(dest);
            }
        }
        return written;
    }

    private static string NextFreePath(string candidate)
    {
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var dir = Path.GetDirectoryName(candidate) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);
        int n = 2;
        string next;
        do
        {
            next = Path.Combine(dir, $"{baseName} {n}{ext}");
            n++;
        } while (File.Exists(next) || Directory.Exists(next));
        return next;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Line(History history, int? keepOnLine = null, string? lineStatePath = null, Clearing? clearing = null)
    {
        History = history;
        Clearing = clearing ?? new Clearing();
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
        var note = Note.Load(filePath, rel, DateTime.Now, pinned: false, appName: History.AppOf(filePath));
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
        var latestApp = History.AppOf(filePath);

        int idx = _lineItems.FindIndex(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            var cur = _lineItems[idx];
            var updated = Note.Load(cur.FilePath, cur.RelativePath, stampLocal, pinned: cur.Pinned, appName: latestApp ?? cur.AppName)
                          ?? cur.WithUsedAt(stampLocal);
            // 收藏的与拿在手上的留在原位（T-019 / CONTEXT.md）
            if (updated.Pinned || _heldPaths.Contains(full))
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
            if (rel is not null && Note.Load(filePath, rel, stampLocal, pinned: false, appName: latestApp) is { } reclaimed)
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
            return ResolveActiveBasketNote() is { } bn ? [bn] : [];
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
            if (ResolveActiveBasketNote() is { } basketNote)
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
            if (nextPinned)
            {
                // 收藏上绳的验证码或按小时过期项转为长期保留（CONTEXT.md：收藏了就不过期）
                History.SetTtl(current.FilePath, null);
            }
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
        History.SetTtl(filePath, null);
        var entry = History.AllEntries().FirstOrDefault(e => string.Equals(Path.GetFullPath(e.FullPath), full, StringComparison.OrdinalIgnoreCase));
        var usedLocal = entry is not null
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.UsedUnix * 1000)).LocalDateTime
            : DateTime.Now;
        var loaded = Note.Load(filePath, rel, usedLocal, pinned: true, appName: entry?.AppName);
        if (loaded is null) return;

        int pinIdx = PinnedCount;
        _lineItems.Insert(pinIdx, loaded);
        var displaced = TrimToCapacity(protectPaths: [full]);

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
                IsPinChange: true,
                DisplacedLoosePaths: displaced.Select(d => d.FilePath).ToList())
        ]);

        SaveLineState();
        RefreshCurrentView();
    }

    // MARK: Take Down (Ctrl+Backspace / Delete) & Put Back (Ctrl+Z)

    public bool TakeDownPicked() => TakeDownNotes(GetSelectedNotesInLineOrder());

    public bool TakeDownNotes(IReadOnlyList<Note> targets, bool sendToRecycleBin = false)
    {
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

        // 两阶段提交：私有项常规取下移入 DiscardedFolder；移入系统回收站走 IFileOperation(FOF_ALLOWUNDO) 直接记 $Recycle.Bin 路径不留副本
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
                bool inSystemRecycleBin = false;
                bool isPrivate = DataFolder.Contains(note.FilePath);
                bool canRecycle = sendToRecycleBin && !wasPinned && DataFolder.IsMovablePrivateItem(note.FilePath);

                if (sendToRecycleBin)
                {
                    if (!canRecycle || !File.Exists(note.FilePath) ||
                        !RecycleBinInterop.MoveFileToRecycleBin(note.FilePath, out var recycledPath) ||
                        string.IsNullOrEmpty(recycledPath))
                    {
                        RollbackMovedToStash(movedToStash);
                        return false;
                    }
                    stashed = recycledPath;
                    inSystemRecycleBin = true;
                    movedToStash.Add((note.FilePath, recycledPath));
                }
                else if (File.Exists(note.FilePath) && isPrivate)
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
                    IsPinChange: false,
                    InSystemRecycleBin: inSystemRecycleBin,
                    OcrWords: History.OcrWordsOf(note.FilePath),
                    WasShrunk: History.IsShrunk(note.FilePath),
                    OriginalTtl: History.GetTtl(note.FilePath)));
            }
        }
        catch (IOException)
        {
            RollbackMovedToStash(movedToStash);
            return false;
        }

        if (prepared.Count == 0) return false;
        bool heldChanged = false;
        foreach (var entry in prepared)
        {
            var full = Path.GetFullPath(entry.OriginalPath);
            if (_heldPaths.Remove(full)) heldChanged = true;
            History.Remove(entry.OriginalPath);
            if (entry.WasOnLine)
            {
                _lineItems.RemoveAll(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
            }
        }
        if (heldChanged)
        {
            OnPropertyChanged(nameof(HeldPaths));
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
                    else if (entry.DisplacedLoosePaths is { Count: > 0 } displacedPaths)
                    {
                        var allEntries = History.AllEntries();
                        var byFullPath = new Dictionary<string, History.StoredEntry>(StringComparer.OrdinalIgnoreCase);
                        foreach (var histEntry in allEntries)
                        {
                            byFullPath[Path.GetFullPath(histEntry.FullPath)] = histEntry;
                        }
                        var onLine = OnLinePathSet();
                        int maxToRestore = displacedPaths.Count;
                        int restoredCount = 0;

                        bool TryAppendLoose(History.StoredEntry histEntry, string histFull)
                        {
                            if (restoredCount >= maxToRestore || _lineItems.Count >= EffectiveMaxItems)
                            {
                                return false;
                            }
                            var usedLocal = DateTimeOffset.FromUnixTimeMilliseconds((long)(histEntry.UsedUnix * 1000)).LocalDateTime;
                            if (Note.Load(histEntry.FullPath, histEntry.RelativePath, usedLocal, pinned: false, appName: histEntry.AppName) is not { } restoredLoose)
                            {
                                return false;
                            }
                            _lineItems.Add(restoredLoose);
                            restoredPaths.Add(histFull);
                            restoredCount++;
                            return true;
                        }

                        double? maxDisplacedUnix = null;
                        foreach (var displacedPath in displacedPaths)
                        {
                            var displacedFull = Path.GetFullPath(displacedPath);
                            if (byFullPath.TryGetValue(displacedFull, out var dEntry))
                            {
                                maxDisplacedUnix = maxDisplacedUnix is { } m ? Math.Max(m, dEntry.UsedUnix) : dEntry.UsedUnix;
                            }
                        }

                        // 若pin之后又有更新的卡被新复制挤进篮子，先按最近使用时间补回这些更新卡，再恢复被该次pin挤下的卡
                        if (maxDisplacedUnix is { } cutoffUnix)
                        {
                            foreach (var histEntry in allEntries)
                            {
                                if (restoredCount >= maxToRestore || _lineItems.Count >= EffectiveMaxItems) break;
                                if (histEntry.UsedUnix <= cutoffUnix) break;
                                var histFull = Path.GetFullPath(histEntry.FullPath);
                                if (string.Equals(histFull, full, StringComparison.OrdinalIgnoreCase) || !onLine.Add(histFull))
                                {
                                    continue;
                                }
                                TryAppendLoose(histEntry, histFull);
                            }
                        }

                        foreach (var displacedPath in displacedPaths)
                        {
                            if (restoredCount >= maxToRestore || _lineItems.Count >= EffectiveMaxItems) break;
                            var displacedFull = Path.GetFullPath(displacedPath);
                            if (string.Equals(displacedFull, full, StringComparison.OrdinalIgnoreCase) ||
                                !onLine.Add(displacedFull) ||
                                !byFullPath.TryGetValue(displacedFull, out var histEntry))
                            {
                                continue;
                            }
                            TryAppendLoose(histEntry, displacedFull);
                        }

                        // 若当时确实挤下过卡片，且被挤掉的卡在撤销前已被重新 Use() 拉回或取下，至多补回 maxToRestore 张篮子卡
                        foreach (var histEntry in allEntries)
                        {
                            if (restoredCount >= maxToRestore || _lineItems.Count >= EffectiveMaxItems) break;
                            var histFull = Path.GetFullPath(histEntry.FullPath);
                            if (string.Equals(histFull, full, StringComparison.OrdinalIgnoreCase) || !onLine.Add(histFull))
                            {
                                continue;
                            }
                            TryAppendLoose(histEntry, histFull);
                        }
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
                if (entry.InSystemRecycleBin)
                {
                    RecycleBinInterop.CleanupCompanionMetadataFile(entry.StashedPath);
                }
            }
            else if (!File.Exists(entry.OriginalPath) && !Directory.Exists(entry.OriginalPath))
            {
                continue;
            }

            if (Clearing.IsTextFile(entry.OriginalPath))
            {
                History.Record(entry.OriginalPath, entry.PlainText, entry.OriginalUsedUtc, entry.AppName, entry.OriginalTtl);
            }
            else if (FileList.IsManifestPath(entry.OriginalPath))
            {
                History.RecordFileList(entry.OriginalPath, entry.AppName, entry.OriginalTtl, entry.OriginalUsedUtc);
            }
            else
            {
                byte[]? pngBytes = null;
                if (string.Equals(Path.GetExtension(entry.OriginalPath), ".png", StringComparison.OrdinalIgnoreCase) &&
                    !entry.WasShrunk && File.Exists(entry.OriginalPath))
                {
                    try { pngBytes = File.ReadAllBytes(entry.OriginalPath); } catch (IOException) { }
                }
                History.RecordImage(entry.OriginalPath, entry.OcrWords, pngBytes, entry.OriginalUsedUtc, entry.AppName, entry.OriginalTtl);
                if (entry.WasShrunk)
                {
                    History.SetShrunk(entry.OriginalPath, true);
                }
            }
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
        _basketExpandedPath = null;
        _basketSubPickedFile = null;
        RefreshCurrentView();
    }

    public IReadOnlyList<string>? GetStackFiles(string? manifestPath)
    {
        if (!FileList.IsManifestPath(manifestPath)) return null;
        var present = FileList.PresentPaths(manifestPath!);
        return present.Count > 1 ? present : null;
    }

    public bool ToggleBasketStack(string? targetManifestPath = null)
    {
        var target = targetManifestPath ?? BasketPicked?.FilePath;
        if (string.IsNullOrWhiteSpace(target) || GetStackFiles(target) is null)
        {
            return false;
        }

        var full = Path.GetFullPath(target);
        if (BasketPicked is null || !string.Equals(Path.GetFullPath(BasketPicked.FilePath), full, StringComparison.OrdinalIgnoreCase))
        {
            var match = FlattenBasketNotes().FirstOrDefault(n => string.Equals(Path.GetFullPath(n.FilePath), full, StringComparison.OrdinalIgnoreCase));
            if (match is not null) BasketPicked = match;
        }

        if (_basketExpandedPath is not null && string.Equals(Path.GetFullPath(_basketExpandedPath), full, StringComparison.OrdinalIgnoreCase))
        {
            _basketExpandedPath = null;
            _basketSubPickedFile = null;
        }
        else
        {
            _basketExpandedPath = full;
            _basketSubPickedFile = null;
        }
        OnPropertyChanged(nameof(BasketExpandedPath));
        OnPropertyChanged(nameof(BasketSubPickedFile));
        OnPropertyChanged(nameof(BasketGroups));
        return true;
    }

    public bool PickBasketSubFile(string stackManifestPath, string filePath)
    {
        var files = GetStackFiles(stackManifestPath);
        if (files is null || !files.Any(f => string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        var stackFull = Path.GetFullPath(stackManifestPath);
        var match = FlattenBasketNotes().FirstOrDefault(n => string.Equals(Path.GetFullPath(n.FilePath), stackFull, StringComparison.OrdinalIgnoreCase));
        if (match is not null) BasketPicked = match;
        _basketExpandedPath = stackFull;
        _basketSubPickedFile = files.First(f => string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(BasketExpandedPath));
        OnPropertyChanged(nameof(BasketSubPickedFile));
        OnPropertyChanged(nameof(BasketGroups));
        return true;
    }

    public void SelectBasketRow(Note note)
    {
        BasketPicked = note;
        if (_basketSubPickedFile is not null)
        {
            _basketSubPickedFile = null;
            OnPropertyChanged(nameof(BasketSubPickedFile));
            OnPropertyChanged(nameof(BasketGroups));
        }
    }

    public Note? ResolveActiveBasketNote()
    {
        if (BasketPicked is null || BasketPicked.IsMovedAway)
        {
            return null;
        }
        if (_basketSubPickedFile is { } subFile &&
            _basketExpandedPath is not null &&
            string.Equals(Path.GetFullPath(BasketPicked.FilePath), Path.GetFullPath(_basketExpandedPath), StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(subFile) || Directory.Exists(subFile)))
        {
            var singleManifest = History.FindFileListHolding([subFile]) ?? FileList.Make([subFile]);
            if (singleManifest is not null)
            {
                if (!History.Contains(singleManifest))
                {
                    History.RecordFileList(singleManifest, appName: BasketPicked.AppName);
                }
                var rel = History.StoredKeyFor(singleManifest) ?? singleManifest;
                return Note.Load(singleManifest, rel, DateTime.Now, pinned: false, appName: BasketPicked.AppName);
            }
        }
        return BasketPicked;
    }

    // MARK: TTL Expiry, Retention & Manual Cleanup (T-061)

    /// <summary>
    /// 清理到点的短效条目（10 分钟验证码与「一小时后清」应用）：
    /// 直接从磁盘、绳子与数据库移除，绝不进「最近清掉」；外部原文件绝不删除，已收藏项不过期。
    /// </summary>
    public int ClearExpired(DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var expired = History.ExpiredPaths(now);
        if (expired.Count == 0) return 0;

        int removedCount = 0;
        bool heldChanged = false;
        foreach (var raw in expired)
        {
            var full = Path.GetFullPath(raw);
            int lineIdx = _lineItems.FindIndex(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
            if (lineIdx >= 0 && _lineItems[lineIdx].Pinned)
            {
                History.SetTtl(full, null);
                continue;
            }

            if (DataFolder.Contains(full) && File.Exists(full))
            {
                try
                {
                    File.Delete(full);
                }
                catch (IOException)
                {
                    // 文件被临时占用时保留数据库行，待下一轮到期检查重试删除，防止私密文件孤立残留
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
            }

            if (_heldPaths.Remove(full)) heldChanged = true;
            if (lineIdx >= 0)
            {
                _lineItems.RemoveAt(lineIdx);
            }
            History.Remove(full);
            removedCount++;
        }

        if (removedCount > 0)
        {
            if (heldChanged) OnPropertyChanged(nameof(HeldPaths));
            SaveLineState();
            RefreshCurrentView();
            if (BasketOpen) LoadBasket();
        }
        return removedCount;
    }

    public RetentionStatus GetRetentionStatus(DateTime? nowUtc = null, long capBytes = Retention.DefaultCapBytes) =>
        Retention.GetStatus(History, OnLinePathSet(), nowUtc, capBytes);

    public RetentionPlan RunRetention(DateTime? nowUtc = null, long capBytes = Retention.DefaultCapBytes)
    {
        var plan = Retention.Run(History, Clearing, OnLinePathSet(), nowUtc, capBytes);
        if (plan.ExpiredPaths.Count > 0 || plan.ShrunkPaths.Count > 0 || plan.MissingPaths.Count > 0)
        {
            RefreshCurrentView();
            if (BasketOpen) LoadBasket();
        }
        return plan;
    }

    public CleanupPreview PreviewCleanup(CleanupFilter filter, DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        double nowSec = new DateTimeOffset(now).ToUnixTimeMilliseconds() / 1000.0;
        var onLine = OnLinePathSet();

        var words = SearchText.Words(filter.QueryText);
        HashSet<string>? matchedQueryPaths = null;
        if (words.Count > 0)
        {
            var search = History.Search(words, [], limit: int.MaxValue);
            matchedQueryPaths = new HashSet<string>(search.Basket.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        }

        var candidates = new List<string>();
        var staleToPrune = new List<string>();
        long estimatedBytes = 0;

        foreach (var item in History.KeptEntries())
        {
            var full = Path.GetFullPath(item.FullPath);
            if (!filter.IncludePinnedAndOnLine && onLine.Contains(full))
            {
                continue;
            }
            if (filter.OlderThanDays is { } minDays && (nowSec - item.UsedUnix) <= minDays * 86400.0)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(filter.App))
            {
                if (string.IsNullOrWhiteSpace(item.AppName) ||
                    !string.Equals(SourceApp.NormalizeRuleKey(item.AppName), SourceApp.NormalizeRuleKey(filter.App), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            if (matchedQueryPaths is not null && !matchedQueryPaths.Contains(full))
            {
                continue;
            }

            if (!File.Exists(full) && !Directory.Exists(full))
            {
                staleToPrune.Add(full);
                continue;
            }

            if (filter.Kind != KindFilter.All)
            {
                var usedDt = DateTimeOffset.FromUnixTimeMilliseconds((long)(item.UsedUnix * 1000.0)).LocalDateTime;
                var loaded = Note.Load(full, item.RelativePath, usedDt, pinned: false, appName: item.AppName);
                if (loaded is null || !loaded.MatchesFilter(filter.Kind))
                {
                    continue;
                }
            }

            candidates.Add(full);
            if (DataFolder.Contains(full) && File.Exists(full))
            {
                estimatedBytes += Retention.SafeFileLength(full);
            }
        }

        foreach (var stale in staleToPrune)
        {
            History.Remove(stale);
        }

        return new CleanupPreview(candidates, estimatedBytes);
    }

    public ClearedBatch? CleanUp(IReadOnlyList<string> candidatePaths, DateTime? nowUtc = null)
    {
        if (candidatePaths.Count == 0) return null;
        bool lineChanged = false;
        bool heldChanged = false;
        foreach (var p in candidatePaths)
        {
            var full = Path.GetFullPath(p);
            if (_heldPaths.Remove(full)) heldChanged = true;
            if (_lineItems.RemoveAll(x => string.Equals(Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                lineChanged = true;
            }
        }

        var batch = Clearing.Clear(candidatePaths, reason: "manual", History, nowUtc);
        if (heldChanged) OnPropertyChanged(nameof(HeldPaths));
        if (lineChanged) SaveLineState();
        RefreshCurrentView();
        if (BasketOpen) LoadBasket();
        return batch;
    }

    public IReadOnlyList<string> PutBackClearedBatch(string batchId)
    {
        var restored = Clearing.PutBack(batchId, History);
        if (restored.Count > 0)
        {
            RefreshCurrentView();
            if (BasketOpen) LoadBasket();
        }
        return restored;
    }

    public bool MoveBasketPick(int step)
    {
        // 若当前选中的文件叠已展开（Ctrl+↓），↑↓ 先在展开的子文件间逐行行走（对齐 Line.swift:352）
        if (_basketExpandedPath is { } exp &&
            BasketPicked is not null &&
            string.Equals(Path.GetFullPath(BasketPicked.FilePath), Path.GetFullPath(exp), StringComparison.OrdinalIgnoreCase) &&
            GetStackFiles(exp) is { Count: > 1 } stackFiles)
        {
            if (_basketSubPickedFile is { } sub)
            {
                int subIdx = -1;
                for (int i = 0; i < stackFiles.Count; i++)
                {
                    if (string.Equals(stackFiles[i], sub, StringComparison.OrdinalIgnoreCase))
                    {
                        subIdx = i;
                        break;
                    }
                }
                if (subIdx >= 0)
                {
                    if (subIdx + step < 0)
                    {
                        _basketSubPickedFile = null;
                        OnPropertyChanged(nameof(BasketSubPickedFile));
                        OnPropertyChanged(nameof(BasketGroups));
                        return true;
                    }
                    if (subIdx + step < stackFiles.Count)
                    {
                        _basketSubPickedFile = stackFiles[subIdx + step];
                        OnPropertyChanged(nameof(BasketSubPickedFile));
                        OnPropertyChanged(nameof(BasketGroups));
                        return true;
                    }
                    _basketSubPickedFile = null;
                    OnPropertyChanged(nameof(BasketSubPickedFile));
                }
            }
            else if (step > 0)
            {
                _basketSubPickedFile = stackFiles[0];
                OnPropertyChanged(nameof(BasketSubPickedFile));
                OnPropertyChanged(nameof(BasketGroups));
                return true;
            }
        }

        if (_basketSubPickedFile is not null)
        {
            _basketSubPickedFile = null;
            OnPropertyChanged(nameof(BasketSubPickedFile));
        }

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
        _basketSubPickedFile = null;
        OnPropertyChanged(nameof(BasketSubPickedFile));
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
            bool matchedImgText = words.Count > 0 && note.Kind == ItemKind.Image && History.HasImageTextHit(note.FilePath, words);
            rows.Add(new BasketRow(note, usedDt, matchedImgText));
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

        _matchedImageTextPaths.Clear();
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
            foreach (var item in combined)
            {
                if (item.Kind == ItemKind.Image && History.HasImageTextHit(item.FilePath, words))
                {
                    _matchedImageTextPaths.Add(Path.GetFullPath(item.FilePath));
                }
            }
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

    private List<Note> TrimToCapacity(HashSet<string>? protectPaths = null)
    {
        var displaced = new List<Note>();
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
            displaced.Add(_lineItems[victim]);
            _lineItems.RemoveAt(victim);
        }
        displaced.Reverse();
        return displaced;
    }

    private void SortPinnedBeforeLoosePreservingRelativeOrder()
    {
        var pinned = _lineItems.Where(x => x.Pinned).ToList();
        var loose = _lineItems.Where(x => !x.Pinned).ToList();
        _lineItems.Clear();
        _lineItems.AddRange(pinned);
        _lineItems.AddRange(loose);
    }

    private static void RollbackMovedToStash(List<(string Original, string Stashed)> movedToStash)
    {
        foreach (var (orig, stash) in movedToStash)
        {
            if (File.Exists(stash) && !File.Exists(orig))
            {
                try
                {
                    File.Move(stash, orig, overwrite: false);
                    RecycleBinInterop.CleanupCompanionMetadataFile(stash);
                }
                catch
                {
                    // 回滚尽力恢复已搬移项
                }
            }
        }
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
                if (!e.InSystemRecycleBin && e.StashedPath is not null && File.Exists(e.StashedPath))
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
        bool IsPinChange,
        IReadOnlyList<string>? DisplacedLoosePaths = null,
        bool InSystemRecycleBin = false,
        string? OcrWords = null,
        bool WasShrunk = false,
        double? OriginalTtl = null);
}

/// <summary>
/// 通过 COM IFileOperation + FOF_ALLOWUNDO 将私有文件移入系统回收站，
/// 并由 IFileOperationProgressSink.PostDeleteItem 精确捕获 $Recycle.Bin 落地路径供 Ctrl+Z 还原（不留重复副本）。
/// </summary>
public static class RecycleBinInterop
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofxAddUndoRecord = 0x20000000;
    private const uint TsfDeleteRecycleIfPossible = 0x0080;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int EAbort = unchecked((int)0x80004004);

    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hrResult);
        [PreserveSig] int PreRenameItem(uint dwFlags, IShellItem? psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostRenameItem(uint dwFlags, IShellItem? psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrRename, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreMoveItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostMoveItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrMove, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreCopyItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostCopyItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem? psiItem);
        [PreserveSig] int PostDeleteItem(uint dwFlags, IShellItem? psiItem, int hrDelete, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreNewItem(uint dwFlags, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostNewItem(uint dwFlags, IShellItem? psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewlyCreated);
        [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise([MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink pfops, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object popd);
        void SetProperties([MarshalAs(UnmanagedType.Interface)] object pproparray);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object punkItems);
        void RenameItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);
        void RenameItems([MarshalAs(UnmanagedType.Interface)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);
        void MoveItems([MarshalAs(UnmanagedType.Interface)] object punkItems, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder);
        void CopyItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);
        void CopyItems([MarshalAs(UnmanagedType.Interface)] object punkItems, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder);
        void DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);
        void DeleteItems([MarshalAs(UnmanagedType.Interface)] object punkItems);
        void NewItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }

    private sealed class RecycleProgressSink : IFileOperationProgressSink
    {
        public string? RecycledPath { get; private set; }

        public int StartOperations() => 0;
        public int FinishOperations(int hrResult) => 0;
        public int PreRenameItem(uint dwFlags, IShellItem? psiItem, string? pszNewName) => 0;
        public int PostRenameItem(uint dwFlags, IShellItem? psiItem, string? pszNewName, int hrRename, IShellItem? psiNewlyCreated) => 0;
        public int PreMoveItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName) => 0;
        public int PostMoveItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated) => 0;
        public int PreCopyItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName) => 0;
        public int PostCopyItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated) => 0;

        public int PreDeleteItem(uint dwFlags, IShellItem? psiItem)
        {
            // 若目标卷禁用了回收站（即将永久删除而非进回收站），中止操作以退回 DiscardedFolder 保护文件
            return (dwFlags & TsfDeleteRecycleIfPossible) != 0 ? 0 : EAbort;
        }

        public int PostDeleteItem(uint dwFlags, IShellItem? psiItem, int hrDelete, IShellItem? psiNewlyCreated)
        {
            if (hrDelete >= 0 && psiNewlyCreated is not null)
            {
                psiNewlyCreated.GetDisplayName(SigdnFileSysPath, out var path);
                RecycledPath = path;
            }
            return 0;
        }

        public int PreNewItem(uint dwFlags, IShellItem? psiDestinationFolder, string? pszNewName) => 0;
        public int PostNewItem(uint dwFlags, IShellItem? psiDestinationFolder, string? pszNewName, string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewlyCreated) => 0;
        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    private static bool _simulateFailureForChecks;

    public static void SetSimulateFailureForChecks(bool fail)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(DataFolder.ChecksRunnerEnv), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SetSimulateFailureForChecks is only allowed inside the isolated check runner.");
        }
        _simulateFailureForChecks = fail;
    }

    public static bool MoveFileToRecycleBin(string path, out string? recycledPath)
    {
        recycledPath = null;
        if (_simulateFailureForChecks) return false;
        if (!File.Exists(path)) return false;

        var full = Path.GetFullPath(path);
        var startedUtc = DateTime.UtcNow;
        Guid iid = IidShellItem;
        if (SHCreateItemFromParsingName(full, IntPtr.Zero, ref iid, out var shellItem) < 0 || shellItem is null)
        {
            return false;
        }

        var opType = Type.GetTypeFromCLSID(ClsidFileOperation);
        if (opType is null) return false;

        object? rawOp = null;
        try
        {
            rawOp = Activator.CreateInstance(opType);
            if (rawOp is not IFileOperation fileOp) return false;

            var sink = new RecycleProgressSink();
            fileOp.SetOperationFlags(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofxAddUndoRecord);
            fileOp.DeleteItem(shellItem, sink);
            fileOp.PerformOperations();
            if (fileOp.GetAnyOperationsAborted() || File.Exists(full))
            {
                return false;
            }

            recycledPath = !string.IsNullOrWhiteSpace(sink.RecycledPath) && File.Exists(sink.RecycledPath)
                ? sink.RecycledPath
                : TryFindRecycledFile(full, startedUtc);
            return !string.IsNullOrWhiteSpace(recycledPath) && File.Exists(recycledPath);
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (rawOp is not null && Marshal.IsComObject(rawOp))
            {
                Marshal.ReleaseComObject(rawOp);
            }
            if (Marshal.IsComObject(shellItem))
            {
                Marshal.ReleaseComObject(shellItem);
            }
        }
    }

    public static string? TryFindRecycledFile(string originalFullPath, DateTime minDeletedUtc)
    {
        if (string.IsNullOrWhiteSpace(originalFullPath)) return null;
        var full = Path.GetFullPath(originalFullPath);
        var root = Path.GetPathRoot(full);
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(sid)) return null;

        var recycleDir = Path.Combine(root, "$Recycle.Bin", sid);
        if (!Directory.Exists(recycleDir)) return null;

        IEnumerable<string> metaFiles;
        try
        {
            metaFiles = Directory.EnumerateFiles(recycleDir, "$I*")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var floorUtc = minDeletedUtc.AddSeconds(-5);
        foreach (var iFile in metaFiles)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(iFile) < floorUtc) continue;
                var bytes = File.ReadAllBytes(iFile);
                if (!TryParseRecycleMetadata(bytes, out var origFromMeta, out var deletedUtc)) continue;
                if (deletedUtc != DateTime.MinValue && deletedUtc < floorUtc) continue;
                if (!string.Equals(origFromMeta, full, StringComparison.OrdinalIgnoreCase)) continue;

                var metaName = Path.GetFileName(iFile);
                if (metaName.Length < 3) continue;
                var rFile = Path.Combine(recycleDir, "$R" + metaName[2..]);
                if (File.Exists(rFile))
                {
                    return rFile;
                }
            }
            catch (IOException)
            {
                // 跳过并发写入中的回收站元数据项
            }
            catch (UnauthorizedAccessException)
            {
                // 跳过无权限读取的项
            }
        }
        return null;
    }

    public static void CleanupCompanionMetadataFile(string recycledDataPath)
    {
        if (string.IsNullOrWhiteSpace(recycledDataPath)) return;
        var name = Path.GetFileName(recycledDataPath);
        var dir = Path.GetDirectoryName(recycledDataPath);
        if (string.IsNullOrEmpty(dir) || name.Length < 3 || !name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var iFile = Path.Combine(dir, "$I" + name[2..]);
        if (File.Exists(iFile))
        {
            try { File.Delete(iFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TryParseRecycleMetadata(byte[] bytes, out string originalFullPath, out DateTime deletedUtc)
    {
        originalFullPath = string.Empty;
        deletedUtc = DateTime.MinValue;
        if (bytes.Length < 28) return false;

        long version = BitConverter.ToInt64(bytes, 0);
        long fileTime = BitConverter.ToInt64(bytes, 16);
        if (fileTime > 0)
        {
            try { deletedUtc = DateTime.FromFileTimeUtc(fileTime); } catch (ArgumentOutOfRangeException) { }
        }

        string raw;
        if (version == 2)
        {
            int charCount = BitConverter.ToInt32(bytes, 24);
            if (charCount <= 0 || charCount > 32768 || 28 + charCount * 2 > bytes.Length) return false;
            raw = Encoding.Unicode.GetString(bytes, 28, charCount * 2);
        }
        else if (version == 1 && bytes.Length >= 544)
        {
            raw = Encoding.Unicode.GetString(bytes, 24, 520);
        }
        else
        {
            return false;
        }

        int nul = raw.IndexOf('\0');
        if (nul >= 0) raw = raw[..nul];
        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal)) raw = raw[4..];
        if (string.IsNullOrWhiteSpace(raw)) return false;

        try
        {
            originalFullPath = Path.GetFullPath(raw);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
