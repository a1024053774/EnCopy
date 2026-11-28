using System.IO;
using System.Windows.Input;

namespace EnCopy;

/// <summary>
/// 快速查看会话：只保存当前预览列表、位置与开闭状态；
/// 打开后 ←/→ 沿当前绳子或篮子的屏幕顺序行走，Ctrl+Y、Esc、Enter 关闭且全部由预览层吞掉。
/// </summary>
public sealed class QuickLookSession
{
    private QuickLookSession(IReadOnlyList<Note> items, int index)
    {
        Items = items;
        Index = index;
        IsOpen = true;
    }

    public IReadOnlyList<Note> Items { get; }
    public int Index { get; private set; }
    public bool IsOpen { get; private set; }
    public Note Current => Items[Index];

    public static QuickLookSession? Open(Line line, Note? target = null, bool? useBasket = null)
    {
        bool basket = useBasket ?? line.BasketOpen;
        IReadOnlyList<Note> items = basket
            ? line.BasketGroups.SelectMany(g => g.Rows.Select(r => r.Note)).ToList()
            : line.ShownItems.ToList();
        if (items.Count == 0) return null;

        target ??= basket ? line.BasketPicked : line.Picked;
        int index = 0;
        if (target is not null)
        {
            var full = Path.GetFullPath(target.FilePath);
            int found = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(Path.GetFullPath(items[i].FilePath), full, StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }
            if (found >= 0) index = found;
        }
        return new QuickLookSession(items, index);
    }

    public bool HandleKey(Key key, ModifierKeys modifiers, out bool closeRequested)
    {
        closeRequested = false;
        if (!IsOpen) return false;

        bool closes =
            (modifiers == ModifierKeys.Control && key == Key.Y) ||
            (modifiers == ModifierKeys.None && key is Key.Escape or Key.Enter or Key.Return);
        if (closes)
        {
            Close();
            closeRequested = true;
            return true;
        }

        if (modifiers == ModifierKeys.None && key is Key.Left or Key.Right)
        {
            int step = key == Key.Left ? -1 : 1;
            Index = Math.Clamp(Index + step, 0, Items.Count - 1);
            return true;
        }

        return false;
    }

    public void Close()
    {
        IsOpen = false;
    }
}

public enum QuickLookContentKind
{
    Image,
    Video,
    Text,
    Files,
    Missing
}

public sealed record QuickLookFileEntry(
    string Path,
    string Name,
    long? SizeBytes,
    bool Exists,
    bool IsDirectory);

public sealed record QuickLookContent(
    QuickLookContentKind Kind,
    string Title,
    string? MediaPath,
    string Text,
    IReadOnlyList<QuickLookFileEntry> Files);

/// <summary>
/// 把一张纸解析成预览窗内容模型；窗口只负责绘制，文件存在性与大小状态在这里统一判定。
/// </summary>
public static class QuickLookContentFactory
{
    public static QuickLookContent For(Note note)
    {
        var title = !string.IsNullOrWhiteSpace(note.SummaryLine)
            ? note.SummaryLine
            : FileList.NameOf(note.FilePath);

        if (note.Kind is ItemKind.Image or ItemKind.Video)
        {
            if (File.Exists(note.FilePath))
            {
                return new QuickLookContent(
                    note.Kind == ItemKind.Image ? QuickLookContentKind.Image : QuickLookContentKind.Video,
                    title,
                    note.FilePath,
                    string.Empty,
                    []);
            }
            return new QuickLookContent(QuickLookContentKind.Missing, title, null, string.Empty, []);
        }

        if (note.Kind == ItemKind.File)
        {
            var targets = note.FileTargets.Count > 0 ? note.FileTargets : [note.FilePath];
            var rows = new List<QuickLookFileEntry>(targets.Count);
            foreach (var target in targets)
            {
                bool isFile = File.Exists(target);
                bool isDir = Directory.Exists(target);
                rows.Add(new QuickLookFileEntry(
                    target,
                    FileList.NameOf(target),
                    isFile ? SafeFileLength(target) : null,
                    isFile || isDir,
                    isDir));
            }
            return new QuickLookContent(QuickLookContentKind.Files, title, null, string.Empty, rows);
        }

        return new QuickLookContent(QuickLookContentKind.Text, title, null, note.FullText, []);
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0L;
        }
        catch (UnauthorizedAccessException)
        {
            return 0L;
        }
    }
}
