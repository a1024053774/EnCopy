using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace EnCopy;

/// <summary>
/// EnCopy 核心状态模型：维护历史条目、即时搜索过滤与选中光标（T-058 先以列表呈现）。
/// </summary>
public sealed class Line : INotifyPropertyChanged
{
    public History History { get; }
    public ObservableCollection<Note> ShownItems { get; } = [];

    private readonly List<Note> _all = [];
    private string _query = string.Empty;
    private Note? _picked;
    private int _matchCount;
    private int _totalCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Line(History history)
    {
        History = history;
        ReloadFromHistory();
    }

    public string Query
    {
        get => _query;
        set
        {
            var next = value ?? string.Empty;
            if (_query == next) return;
            _query = next;
            OnPropertyChanged();
            RecomputeShown();
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

    public bool IsSearching => SearchText.Words(_query).Count > 0;

    public void ReloadFromHistory()
    {
        _all.Clear();
        foreach (var entry in History.RecentEntries(limit: 500))
        {
            var note = Note.Load(entry.FullPath, entry.RelativePath, DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.UsedUnix * 1000)).LocalDateTime);
            if (note is not null)
            {
                _all.Add(note);
            }
        }
        RecomputeShown();
    }

    public void AddNew(string filePath)
    {
        var rel = History.RelativePath(filePath);
        if (rel is null) return;
        var note = Note.Load(filePath, rel, DateTime.Now);
        if (note is null) return;

        _all.RemoveAll(x => string.Equals(x.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        _all.Insert(0, note);
        RecomputeShown();
        PickNewest();
    }

    public void Use(string filePath, DateTime? usedAt = null)
    {
        var stampUtc = (usedAt ?? DateTime.UtcNow).ToUniversalTime();
        var stampLocal = stampUtc.ToLocalTime();
        var idx = _all.FindIndex(x => string.Equals(x.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            var updated = _all[idx].WithUsedAt(stampLocal);
            if (!updated.Pinned)
            {
                _all.RemoveAt(idx);
                var insertAt = _all.FindIndex(x => !x.Pinned);
                if (insertAt < 0) insertAt = _all.Count;
                _all.Insert(insertAt, updated);
            }
            else
            {
                _all[idx] = updated;
            }
        }
        History.Use(filePath, stampUtc);
        RecomputeShown();
    }

    public void PickNewest()
    {
        Picked = ShownItems.FirstOrDefault();
    }

    public void MovePick(int step)
    {
        if (ShownItems.Count == 0)
        {
            Picked = null;
            return;
        }
        if (Picked is null)
        {
            Picked = ShownItems[0];
            return;
        }
        int idx = -1;
        for (int i = 0; i < ShownItems.Count; i++)
        {
            if (string.Equals(ShownItems[i].FilePath, Picked.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
        {
            Picked = ShownItems[0];
            return;
        }
        int next = Math.Clamp(idx + step, 0, ShownItems.Count - 1);
        Picked = ShownItems[next];
    }

    public void RecomputeShown()
    {
        TotalCount = _all.Count;
        var words = SearchText.Words(_query);
        IReadOnlyList<Note> visible;
        if (words.Count == 0)
        {
            visible = _all;
            MatchCount = _all.Count;
        }
        else
        {
            var found = History.Search(words, [], limit: 200);
            var byPath = _all.ToDictionary(n => Path.GetFullPath(n.FilePath), n => n, StringComparer.OrdinalIgnoreCase);
            var matches = new List<Note>(found.Entries.Count);
            foreach (var entry in found.Entries)
            {
                var fullPath = Path.GetFullPath(entry.FullPath);
                if (byPath.TryGetValue(fullPath, out var existing))
                {
                    matches.Add(existing);
                }
                else if (Note.Load(entry.FullPath, entry.RelativePath, DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.UsedUnix * 1000)).LocalDateTime) is { } loaded)
                {
                    matches.Add(loaded);
                }
            }
            visible = matches;
            MatchCount = found.BasketTotal;
        }

        ShownItems.Clear();
        foreach (var n in visible)
        {
            ShownItems.Add(n);
        }

        if (Picked is null || !ShownItems.Any(n => string.Equals(n.FilePath, Picked.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            Picked = ShownItems.FirstOrDefault();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
