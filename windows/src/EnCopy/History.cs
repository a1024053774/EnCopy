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

    public sealed record KeptEntry(
        string FullPath,
        string RelativePath,
        double UsedUnix,
        string? AppName,
        bool Shrunk,
        double? TtlSeconds);

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

    public static byte[] PictureHash(byte[] pngBytes)
    {
        var prefix = "picture:"u8;
        var buf = new byte[prefix.Length + pngBytes.Length];
        prefix.CopyTo(buf);
        Buffer.BlockCopy(pngBytes, 0, buf, prefix.Length, pngBytes.Length);
        return SHA256.HashData(buf);
    }

    public static byte[] FileListHash(IReadOnlyList<string> rawPaths)
    {
        var sorted = FileList.NormalizePaths(rawPaths)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        var payload = "files:" + string.Join('\n', sorted);
        return SHA256.HashData(Encoding.UTF8.GetBytes(payload));
    }

    public string? FindPictureHolding(byte[] pngBytes)
    {
        var hash = PictureHash(pngBytes);
        var paths = new List<string>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT path FROM item WHERE hash = @hash AND shrunk = 0";
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
            if (File.Exists(abs)) return abs;
            RemoveByRelativePath(rel);
        }
        return null;
    }

    public string? FindFileListHolding(IReadOnlyList<string> rawPaths)
    {
        var hash = FileListHash(rawPaths);
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
            if (File.Exists(abs)) return abs;
            RemoveByRelativePath(rel);
        }
        return null;
    }

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

    public void Record(
        string fullPath,
        string? plainText = null,
        DateTime? usedAt = null,
        string? appName = null,
        double? ttlSeconds = null)
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
                INSERT INTO item (path, hash, modified, used, app, ttl) VALUES (@path, @hash, @mod, @used, @app, @ttl)
                ON CONFLICT(path) DO UPDATE SET hash = excluded.hash, modified = excluded.modified, used = excluded.used,
                    app = COALESCE(excluded.app, item.app), ttl = COALESCE(excluded.ttl, item.ttl)
                """;
            upsert.Parameters.AddWithValue("@path", storagePath);
            upsert.Parameters.AddWithValue("@hash", hash);
            upsert.Parameters.AddWithValue("@mod", modified);
            upsert.Parameters.AddWithValue("@used", used);
            upsert.Parameters.AddWithValue("@app", (object?)appName ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@ttl", ttlSeconds.HasValue ? ttlSeconds.Value : DBNull.Value);
            upsert.ExecuteNonQuery();
        }

        long id = GetRowIdInTransaction(tx, storagePath);
        ReplaceItemTextInTransaction(tx, id, folded);
        tx.Commit();
    }

    public void RecordImage(
        string fullPath,
        string? ocrWords = null,
        byte[]? pngBytesForHash = null,
        DateTime? usedAt = null,
        string? appName = null,
        double? ttlSeconds = null)
    {
        var storagePath = StoredKeyFor(fullPath);
        if (storagePath is null || !ExistsOnDisk(fullPath)) return;

        var modified = File.Exists(fullPath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)).ToUnixTimeMilliseconds() / 1000.0
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var used = usedAt.HasValue
            ? new DateTimeOffset(usedAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var hash = pngBytesForHash is not null
            ? PictureHash(pngBytesForHash)
            : SHA256.HashData(Encoding.UTF8.GetBytes("image:" + storagePath));

        using var tx = _db.BeginTransaction();
        using (var upsert = _db.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO item (path, hash, modified, used, app, ttl) VALUES (@path, @hash, @mod, @used, @app, @ttl)
                ON CONFLICT(path) DO UPDATE SET modified = excluded.modified, used = excluded.used,
                    app = COALESCE(excluded.app, item.app), ttl = COALESCE(excluded.ttl, item.ttl)
                """;
            upsert.Parameters.AddWithValue("@path", storagePath);
            upsert.Parameters.AddWithValue("@hash", hash);
            upsert.Parameters.AddWithValue("@mod", modified);
            upsert.Parameters.AddWithValue("@used", used);
            upsert.Parameters.AddWithValue("@app", (object?)appName ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@ttl", ttlSeconds.HasValue ? ttlSeconds.Value : DBNull.Value);
            upsert.ExecuteNonQuery();
        }

        long id = GetRowIdInTransaction(tx, storagePath);
        bool hasWords;
        using (var checkCmd = _db.CreateCommand())
        {
            checkCmd.Transaction = tx;
            checkCmd.CommandText = "SELECT COUNT(*) FROM item_text WHERE rowid = @id AND text != ''";
            checkCmd.Parameters.AddWithValue("@id", id);
            hasWords = ((long)(checkCmd.ExecuteScalar() ?? 0L)) > 0;
        }
        bool shrunk;
        using (var shrunkCmd = _db.CreateCommand())
        {
            shrunkCmd.Transaction = tx;
            shrunkCmd.CommandText = "SELECT shrunk FROM item WHERE id = @id";
            shrunkCmd.Parameters.AddWithValue("@id", id);
            shrunk = Convert.ToInt64(shrunkCmd.ExecuteScalar() ?? 0L) != 0;
        }

        // 已识别出的图中文字绝不被空串覆盖，缩成缩略图后的照片也始终保留原图识别词（History.swift:350）
        if (!(hasWords && (string.IsNullOrEmpty(ocrWords) || shrunk)))
        {
            var indexText = string.IsNullOrWhiteSpace(ocrWords)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : $"{ocrWords} {Path.GetFileNameWithoutExtension(fullPath)}";
            ReplaceItemTextInTransaction(tx, id, SearchText.Fold(indexText));
        }
        tx.Commit();
    }

    public void RecordFileList(
        string manifestPath,
        string? appName = null,
        double? ttlSeconds = null,
        DateTime? usedAt = null)
    {
        if (!FileList.IsManifestPath(manifestPath) || !File.Exists(manifestPath)) return;
        var storagePath = StoredKeyFor(manifestPath);
        if (storagePath is null) return;

        var files = FileList.ReadPaths(manifestPath);
        var hash = FileListHash(files);
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(manifestPath)).ToUnixTimeMilliseconds() / 1000.0;
        var used = usedAt.HasValue
            ? new DateTimeOffset(usedAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        using var tx = _db.BeginTransaction();
        using (var upsert = _db.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO item (path, hash, modified, used, app, ttl) VALUES (@path, @hash, @mod, @used, @app, @ttl)
                ON CONFLICT(path) DO UPDATE SET hash = excluded.hash, modified = excluded.modified, used = excluded.used,
                    app = COALESCE(excluded.app, item.app), ttl = COALESCE(excluded.ttl, item.ttl)
                """;
            upsert.Parameters.AddWithValue("@path", storagePath);
            upsert.Parameters.AddWithValue("@hash", hash);
            upsert.Parameters.AddWithValue("@mod", modified);
            upsert.Parameters.AddWithValue("@used", used);
            upsert.Parameters.AddWithValue("@app", (object?)appName ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@ttl", ttlSeconds.HasValue ? ttlSeconds.Value : DBNull.Value);
            upsert.ExecuteNonQuery();
        }

        long id = GetRowIdInTransaction(tx, storagePath);
        ReplaceItemTextInTransaction(tx, id, SearchText.Fold(FileList.Words(files)));
        tx.Commit();
    }

    public bool Contains(string fullPath)
    {
        var key = StoredKeyFor(fullPath);
        if (key is null) return false;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM item WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", key);
        return ((long)(cmd.ExecuteScalar() ?? 0L)) > 0;
    }

    public string? AppOf(string fullPath)
    {
        var key = StoredKeyFor(fullPath);
        if (key is null) return null;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT app FROM item WHERE path = @path AND app IS NOT NULL";
        cmd.Parameters.AddWithValue("@path", key);
        var raw = cmd.ExecuteScalar();
        return raw is null or DBNull ? null : Convert.ToString(raw, CultureInfo.InvariantCulture);
    }

    public void SetApp(string fullPath, string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return;
        var key = StoredKeyFor(fullPath);
        if (key is null) return;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE item SET app = @app WHERE path = @path";
        cmd.Parameters.AddWithValue("@app", appName);
        cmd.Parameters.AddWithValue("@path", key);
        cmd.ExecuteNonQuery();
    }

    public double? GetTtl(string fullPath)
    {
        var key = StoredKeyFor(fullPath);
        if (key is null) return null;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT ttl FROM item WHERE path = @path AND ttl IS NOT NULL";
        cmd.Parameters.AddWithValue("@path", key);
        var raw = cmd.ExecuteScalar();
        return raw is null or DBNull ? null : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
    }

    public void SetTtl(string fullPath, double? ttlSeconds)
    {
        var key = StoredKeyFor(fullPath);
        if (key is null) return;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE item SET ttl = @ttl WHERE path = @path";
        cmd.Parameters.AddWithValue("@ttl", ttlSeconds.HasValue ? ttlSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@path", key);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 重复复制同一内容时更新其有效时长：永久保留过的条目不会因再次作为验证码复制而开始倒计时（History.swift:223）。
    /// </summary>
    public void RenewTtl(string fullPath, double? ttlSeconds)
    {
        if (ttlSeconds is null || GetTtl(fullPath) is not null)
        {
            SetTtl(fullPath, ttlSeconds);
        }
    }

    public IReadOnlyList<string> ExpiredPaths(DateTime nowUtc)
    {
        double nowSec = new DateTimeOffset(nowUtc.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0;
        var list = new List<string>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path FROM item WHERE ttl IS NOT NULL AND used + ttl <= @now";
        cmd.Parameters.AddWithValue("@now", nowSec);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(AbsolutePath(reader.GetString(0)));
        }
        return list;
    }

    public IReadOnlyList<KeptEntry> KeptEntries() => QueryEntriesWithMeta("WHERE ttl IS NULL");

    public IReadOnlyList<KeptEntry> AllEntriesWithMeta() => QueryEntriesWithMeta("");

    public string? WordsOf(string fullPath)
    {
        var key = StoredKeyFor(fullPath);
        if (key is null) return null;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT item_text.text FROM item_text JOIN item ON item.id = item_text.rowid WHERE item.path = @path";
        cmd.Parameters.AddWithValue("@path", key);
        var raw = cmd.ExecuteScalar();
        return raw is null or DBNull ? null : Convert.ToString(raw, CultureInfo.InvariantCulture);
    }

    public void MarkShrunk(string fullPath) => SetShrunk(fullPath, true);

    public void SetShrunk(string fullPath, bool shrunk)
    {
        var key = StoredKeyFor(fullPath);
        if (key is null) return;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE item SET shrunk = @shrunk WHERE path = @path";
        cmd.Parameters.AddWithValue("@shrunk", shrunk ? 1 : 0);
        cmd.Parameters.AddWithValue("@path", key);
        cmd.ExecuteNonQuery();
    }

    public bool IsShrunk(string fullPath)
    {
        var key = StoredKeyFor(fullPath);
        if (key is null) return false;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT shrunk FROM item WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", key);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    private IReadOnlyList<KeptEntry> QueryEntriesWithMeta(string whereClause)
    {
        var list = new List<KeptEntry>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT path, used, shrunk, app, ttl FROM item {whereClause} ORDER BY used DESC, id DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rel = reader.GetString(0);
            var abs = AbsolutePath(rel);
            double used = reader.GetDouble(1);
            bool shrunk = reader.GetInt64(2) != 0;
            var app = reader.IsDBNull(3) ? null : reader.GetString(3);
            double? ttl = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            list.Add(new KeptEntry(abs, rel, used, app, shrunk, ttl));
        }
        return list;
    }

    private long GetRowIdInTransaction(SqliteTransaction tx, string storagePath)
    {
        using var idCmd = _db.CreateCommand();
        idCmd.Transaction = tx;
        idCmd.CommandText = "SELECT id FROM item WHERE path = @path";
        idCmd.Parameters.AddWithValue("@path", storagePath);
        long id = (long)(idCmd.ExecuteScalar() ?? 0L);
        if (id == 0) throw new InvalidOperationException("Row missing after upsert.");
        return id;
    }

    private void ReplaceItemTextInTransaction(SqliteTransaction tx, long rowId, string foldedText)
    {
        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM item_text WHERE rowid = @id";
            del.Parameters.AddWithValue("@id", rowId);
            del.ExecuteNonQuery();
        }
        using (var ins = _db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO item_text (rowid, text) VALUES (@id, @text)";
            ins.Parameters.AddWithValue("@id", rowId);
            ins.Parameters.AddWithValue("@text", foldedText);
            ins.ExecuteNonQuery();
        }
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
