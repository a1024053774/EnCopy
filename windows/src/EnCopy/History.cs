using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace EnCopy;

public static class SearchText
{
    public static IReadOnlyList<string> Words(string query) =>
        Fold(query)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 大小写、重音与全半角折叠（对齐 Mac 版 SearchIndex.fold）："Menú" / "ＭＥＮＵ" 均折成 "menu"。
    /// </summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var compat = text.Normalize(NormalizationForm.FormKC);
        var decomposed = compat.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}

/// <summary>
/// 全部剪贴板历史条目与 FTS5 trigram 全文索引（对应 Mac 版 History.swift）。
/// </summary>
public sealed class History : IDisposable
{
    public sealed class Found
    {
        public HashSet<string> OnLine { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Basket { get; } = [];
        public List<StoredEntry> Entries { get; } = [];
        public int BasketTotal { get; set; }
    }

    public sealed record StoredEntry(string FullPath, string RelativePath, double UsedUnix, string? AppName);

    private readonly string _rootDir;
    private readonly SqliteConnection _db;

    public History(string? databasePath = null)
    {
        var dbPath = databasePath ?? DataFolder.HistoryDatabasePath;
        _rootDir = Path.GetDirectoryName(Path.GetFullPath(dbPath))
            ?? throw new InvalidOperationException("History database path must have a directory.");
        Directory.CreateDirectory(_rootDir);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        _db.Open();

        Execute("""
            CREATE TABLE IF NOT EXISTS item (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                hash BLOB NOT NULL,
                modified REAL NOT NULL,
                used REAL NOT NULL,
                app TEXT,
                ttl REAL,
                shrunk INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS item_hash ON item(hash);
            CREATE VIRTUAL TABLE IF NOT EXISTS item_text USING fts5(text, tokenize='trigram');
            """);
    }

    public void Dispose() => _db.Dispose();

    public long Count
    {
        get
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM item";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public static byte[] TextHash(string plainText) => SHA256.HashData(Encoding.UTF8.GetBytes(plainText));

    public string? NoteHolding(string plainText)
    {
        var hash = TextHash(plainText);
        var paths = new List<string>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT path FROM item WHERE hash = @hash";
            cmd.Parameters.AddWithValue("@hash", hash);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                paths.Add(reader.GetString(0));
            }
        }

        foreach (var rel in paths)
        {
            var abs = AbsolutePath(rel);
            if (!ExistsOnDisk(abs))
            {
                RemoveByRelativePath(rel);
                continue;
            }
            return abs;
        }
        return null;
    }

    public void Record(string fullPath, string? plainText = null, DateTime? usedAt = null, string? appName = null)
    {
        var storagePath = StoredKeyFor(fullPath);
        if (storagePath is null || !ExistsOnDisk(fullPath)) return;

        var text = plainText ?? Note.ReadPlainText(fullPath) ?? Path.GetFileName(fullPath);

        var modified = File.Exists(fullPath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)).ToUnixTimeMilliseconds() / 1000.0
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var used = usedAt.HasValue
            ? new DateTimeOffset(usedAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        var hash = TextHash(text);
        var folded = SearchText.Fold(text);

        using var tx = _db.BeginTransaction();
        using (var upsert = _db.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO item (path, hash, modified, used, app) VALUES (@path, @hash, @mod, @used, @app)
                ON CONFLICT(path) DO UPDATE SET hash = excluded.hash, modified = excluded.modified, used = excluded.used,
                    app = COALESCE(excluded.app, item.app)
                """;
            upsert.Parameters.AddWithValue("@path", storagePath);
            upsert.Parameters.AddWithValue("@hash", hash);
            upsert.Parameters.AddWithValue("@mod", modified);
            upsert.Parameters.AddWithValue("@used", used);
            upsert.Parameters.AddWithValue("@app", (object?)appName ?? DBNull.Value);
            upsert.ExecuteNonQuery();
        }

        long id;
        using (var idCmd = _db.CreateCommand())
        {
            idCmd.Transaction = tx;
            idCmd.CommandText = "SELECT id FROM item WHERE path = @path";
            idCmd.Parameters.AddWithValue("@path", storagePath);
            id = (long)(idCmd.ExecuteScalar() ?? 0L);
        }
        if (id == 0)
        {
            throw new InvalidOperationException("Row missing after upsert.");
        }

        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM item_text WHERE rowid = @id";
            del.Parameters.AddWithValue("@id", id);
            del.ExecuteNonQuery();
        }
        using (var ins = _db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO item_text (rowid, text) VALUES (@id, @text)";
            ins.Parameters.AddWithValue("@id", id);
            ins.Parameters.AddWithValue("@text", folded);
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void Use(string fullPath, DateTime? usedAt = null)
    {
        var key = StoredKeyFor(fullPath);
        if (key is null) return;
        var used = usedAt.HasValue
            ? new DateTimeOffset(usedAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE item SET used = @used WHERE path = @path";
        cmd.Parameters.AddWithValue("@used", used);
        cmd.Parameters.AddWithValue("@path", key);
        cmd.ExecuteNonQuery();
    }

    public void Remove(string fullPath)
    {
        var key = StoredKeyFor(fullPath);
        if (key is null) return;
        RemoveByRelativePath(key);
    }

    public IReadOnlyList<StoredEntry> RecentEntries(int limit = 200)
    {
        var list = new List<StoredEntry>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path, used, app FROM item ORDER BY used DESC, id DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rel = reader.GetString(0);
            var abs = AbsolutePath(rel);
            if (ExistsOnDisk(abs))
            {
                var app = reader.IsDBNull(2) ? null : reader.GetString(2);
                list.Add(new StoredEntry(abs, rel, reader.GetDouble(1), app));
            }
        }
        return list;
    }

    public IReadOnlyList<StoredEntry> AllEntries() => RecentEntries(limit: int.MaxValue);

    public Found Search(IReadOnlyList<string> words, IReadOnlyList<string> onLinePaths, int limit)
    {
        var found = new Found();
        var longWords = words.Where(w => w.EnumerateRunes().Count() >= 3).ToList();
        var shortWords = words.Where(w => w.EnumerateRunes().Count() < 3 && w.Length > 0).ToList();
        if (longWords.Count == 0 && shortWords.Count == 0)
        {
            return found;
        }

        var conditions = new List<string>();
        var parameters = new List<string>();
        if (longWords.Count > 0)
        {
            var matchQuery = string.Join(" ", longWords.Select(w => $"\"{w.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
            conditions.Add("item_text MATCH @p0");
            parameters.Add(matchQuery);
        }
        foreach (var sw in shortWords)
        {
            string pName = $"@p{parameters.Count}";
            conditions.Add($"instr(item_text.text, {pName}) > 0");
            parameters.Add(sw);
        }

        var sql = $"""
            SELECT item.path, item.used, item.app
            FROM item_text
            JOIN item ON item.id = item_text.rowid
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY item.used DESC, item.id DESC
            """;

        var onLineSet = new HashSet<string>(onLinePaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = sql;
            for (int i = 0; i < parameters.Count; i++)
            {
                cmd.Parameters.AddWithValue($"@p{i}", parameters[i]);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rel = reader.GetString(0);
                var usedUnix = reader.GetDouble(1);
                var app = reader.IsDBNull(2) ? null : reader.GetString(2);
                var abs = AbsolutePath(rel);
                var full = Path.GetFullPath(abs);
                if (onLineSet.Contains(full))
                {
                    found.OnLine.Add(full);
                }
                else if (found.Basket.Count < limit)
                {
                    if (ExistsOnDisk(abs))
                    {
                        found.BasketTotal++;
                        found.Basket.Add(abs);
                        found.Entries.Add(new StoredEntry(abs, rel, usedUnix, app));
                    }
                    else
                    {
                        stale.Add(rel);
                    }
                }
                else
                {
                    found.BasketTotal++;
                }
            }
        }

        foreach (var rel in stale)
        {
            RemoveByRelativePath(rel);
        }

        return found;
    }

    public string? RelativePath(string fullPath)
    {
        var baseFull = Path.GetFullPath(_rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathFull = Path.GetFullPath(fullPath);
        var prefix = baseFull + Path.DirectorySeparatorChar;
        if (!pathFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return pathFull[prefix.Length..].Replace('\\', '/');
    }

    public string? StoredKeyFor(string fullPath) =>
        RelativePath(fullPath) ?? (Path.IsPathRooted(fullPath) ? Path.GetFullPath(fullPath) : null);

    public string AbsolutePath(string storedPath)
    {
        if (Path.IsPathRooted(storedPath))
        {
            return storedPath;
        }
        var parts = storedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Aggregate(_rootDir, Path.Combine);
    }

    private static bool ExistsOnDisk(string fullPath) => File.Exists(fullPath) || Directory.Exists(fullPath);

    private void RemoveByRelativePath(string rel)
    {
        using var tx = _db.BeginTransaction();
        using (var delFts = _db.CreateCommand())
        {
            delFts.Transaction = tx;
            delFts.CommandText = "DELETE FROM item_text WHERE rowid IN (SELECT id FROM item WHERE path = @path)";
            delFts.Parameters.AddWithValue("@path", rel);
            delFts.ExecuteNonQuery();
        }
        using (var delItem = _db.CreateCommand())
        {
            delItem.Transaction = tx;
            delItem.CommandText = "DELETE FROM item WHERE path = @path";
            delItem.Parameters.AddWithValue("@path", rel);
            delItem.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
