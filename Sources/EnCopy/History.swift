import AppKit
import CryptoKit
import SQLite3

/// Everything EnCopy keeps as text, on the line and in the basket: one row per note file in a SQLite
/// database, its words in an FTS5 index. The files stay in the notes folder; the line knows what
/// hangs, and the basket is the rest.
@MainActor
final class History {
    struct Found {
        /// Notes on the line that match, as the line gave them.
        var onLine: Set<URL> = []
        /// Matches in the basket, the most recently used first, at most `limit`.
        var basket: [URL] = []
        /// How many notes in the basket match in all.
        var basketTotal = 0
    }

    private struct Failure: Error, LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    private let file: URL
    /// Every caller is on the main actor; deinit only needs the handle.
    nonisolated(unsafe) private var db: OpaquePointer?

    init(file: URL = DataFolder.url.appendingPathComponent("History.sqlite")) {
        self.file = file
        let fm = FileManager.default
        try? fm.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        if !fm.fileExists(atPath: file.path) {
            // It holds what the user copied: only they can read it.
            fm.createFile(atPath: file.path, contents: nil, attributes: [.posixPermissions: 0o600])
        }
        var handle: OpaquePointer?
        guard sqlite3_open(file.path, &handle) == SQLITE_OK, let handle else {
            let message = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "out of memory"
            fatalError("EnCopy cannot open its history: \(message)")
        }
        db = handle
        for sql in [
            """
            CREATE TABLE IF NOT EXISTS item (
                id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, hash BLOB NOT NULL,
                modified REAL NOT NULL, used REAL NOT NULL);
            """,
            "CREATE INDEX IF NOT EXISTS item_hash ON item(hash)",
            "CREATE VIRTUAL TABLE IF NOT EXISTS item_text USING fts5(text, tokenize='trigram')",
        ] {
            var error: UnsafeMutablePointer<Int8>?
            guard sqlite3_exec(db, sql, nil, nil, &error) == SQLITE_OK else {
                let message = error.map { String(cString: $0) } ?? "unknown"
                sqlite3_free(error)
                fatalError("EnCopy cannot set up its history: \(message)")
            }
        }
        // Histories from before the source app was kept get the column now.
        let columns = (try? strings("SELECT name FROM pragma_table_info('item')", bind: { _ in })) ?? []
        if !columns.contains("app") {
            attempt("add app column") { try run("ALTER TABLE item ADD COLUMN app TEXT", bind: { _ in }) }
        }
        // Seconds an item stays after its last use, for the hourly app rule and verification codes.
        if !columns.contains("ttl") {
            attempt("add ttl column") { try run("ALTER TABLE item ADD COLUMN ttl REAL", bind: { _ in }) }
        }
    }

    deinit { sqlite3_close(db) }

    /// How many notes are kept, on the line and in the basket.
    var count: Int {
        do { return Int(try scalar("SELECT COUNT(*) FROM item")) }
        catch {
            log.error("History count failed: \(error.localizedDescription, privacy: .public)")
            return 0
        }
    }

    /// The note already kept for exactly these words, if any.
    func note(holding text: String) -> URL? {
        let hash = Data(SHA256.hash(data: Data(text.utf8)))
        let paths: [String]
        do {
            paths = try strings("SELECT path FROM item WHERE hash = ?", bind: { bind($0, 1, hash) })
        } catch {
            log.error("History lookup failed: \(error.localizedDescription, privacy: .public)")
            return nil
        }
        for path in paths {
            let url = absoluteURL(path)
            guard FileManager.default.fileExists(atPath: url.path) else {
                remove(path: path)
                continue
            }
            if let existing = row(path: path),
               let modified = Note.modificationDate(url)?.timeIntervalSince1970,
               existing.modified != modified {
                // Edited since the row was written: read it again and accept
                // it only if it still holds these words.
                record(url)
                guard let fresh = row(path: path), fresh.hash == hash else { continue }
            }
            return url
        }
        return nil
    }

    /// Keeps a note's row current: adds it, or reads its words again after an edit.
    /// `text` is what was copied, when it is known.
    func record(_ url: URL, text: String? = nil, app: String? = nil, ttl: TimeInterval? = nil) {
        guard isText(url), let path = relativePath(url),
              FileManager.default.fileExists(atPath: url.path),
              let modified = Note.modificationDate(url)?.timeIntervalSince1970 else { return }
        if let existing = row(path: path), existing.modified == modified { return }
        guard let plain = text ?? readText(url) else { return }
        let hash = Data(SHA256.hash(data: Data(plain.utf8)))
        let used = (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate)?
            .timeIntervalSince1970 ?? Date().timeIntervalSince1970
        let folded = SearchIndex.fold(plain)
        attempt("record") {
            try transaction {
                try run("""
                    INSERT INTO item (path, hash, modified, used, app, ttl) VALUES (?, ?, ?, ?, ?, ?)
                    ON CONFLICT(path) DO UPDATE SET hash = excluded.hash, modified = excluded.modified,
                        app = COALESCE(excluded.app, item.app), ttl = COALESCE(excluded.ttl, item.ttl)
                    """, bind: { stmt in
                        bind(stmt, 1, path)
                        bind(stmt, 2, hash)
                        bind(stmt, 3, modified)
                        bind(stmt, 4, used)
                        if let app { bind(stmt, 5, app) } else { sqlite3_bind_null(stmt, 5) }
                        if let ttl { bind(stmt, 6, ttl) } else { sqlite3_bind_null(stmt, 6) }
                    })
                let id = try scalar("SELECT id FROM item WHERE path = ?", bind: { bind($0, 1, path) })
                guard id != 0 else { throw Failure(message: "the note's row is missing after the upsert") }
                try run("DELETE FROM item_text WHERE rowid = ?", bind: { bind($0, 1, id) })
                try run("INSERT INTO item_text (rowid, text) VALUES (?, ?)", bind: { stmt in
                    bind(stmt, 1, id)
                    bind(stmt, 2, folded)
                })
            }
        }
    }

    /// The bundle id of the app a note was copied from, when it was known.
    func app(of url: URL) -> String? {
        guard let path = relativePath(url) else { return nil }
        return (try? strings("SELECT app FROM item WHERE path = ? AND app IS NOT NULL", bind: { bind($0, 1, path) }))?.first
    }

    /// How long an item stays after its last use, when it does not stay for good.
    func ttl(of url: URL) -> TimeInterval? {
        guard let path = relativePath(url), let row = try? strings("SELECT ttl FROM item WHERE path = ? AND ttl IS NOT NULL", bind: { bind($0, 1, path) }).first else { return nil }
        return TimeInterval(row)
    }

    /// Items whose time ran out by `now`: their last use plus their ttl.
    func expired(at now: Date) -> [URL] {
        let paths = (try? strings("SELECT path FROM item WHERE ttl IS NOT NULL AND used + ttl <= ?", bind: { bind($0, 1, now.timeIntervalSince1970) })) ?? []
        return paths.map(absoluteURL)
    }

    /// That app's name as Finder shows it, or nil when it is not on this Mac.
    static func appName(_ bundleID: String) -> String? {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else { return nil }
        let name = FileManager.default.displayName(atPath: url.path)
        return name.hasSuffix(".app") ? String(name.dropLast(4)) : name
    }

    /// A picture EnCopy keeps (T-030): a row of its own, with the words read
    /// from it once they are ready. Nil text keeps whatever words it has.
    func recordImage(_ url: URL, text: String?) {
        guard !isText(url), let path = relativePath(url), FileManager.default.fileExists(atPath: url.path),
              let modified = Note.modificationDate(url)?.timeIntervalSince1970 else { return }
        let hash = Data(SHA256.hash(data: Data(("image:" + path).utf8)))
        let used = (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate)?
            .timeIntervalSince1970 ?? Date().timeIntervalSince1970
        attempt("record image") {
            try transaction {
                try run("""
                    INSERT INTO item (path, hash, modified, used) VALUES (?, ?, ?, ?)
                    ON CONFLICT(path) DO UPDATE SET modified = excluded.modified
                    """, bind: { stmt in
                        bind(stmt, 1, path)
                        bind(stmt, 2, hash)
                        bind(stmt, 3, modified)
                        bind(stmt, 4, used)
                    })
                let id = try scalar("SELECT id FROM item WHERE path = ?", bind: { bind($0, 1, path) })
                guard id != 0 else { throw Failure(message: "the picture's row is missing after the upsert") }
                let hasWords = try scalar("SELECT COUNT(*) FROM item_text WHERE rowid = ?", bind: { bind($0, 1, id) }) > 0
                guard text != nil || !hasWords else { return }
                try run("DELETE FROM item_text WHERE rowid = ?", bind: { bind($0, 1, id) })
                try run("INSERT INTO item_text (rowid, text) VALUES (?, ?)", bind: { stmt in
                    bind(stmt, 1, id)
                    bind(stmt, 2, SearchIndex.fold(text ?? ""))
                })
            }
        }
    }

    /// Used: pasted or copied out of EnCopy, or the same words copied again.
    func use(_ url: URL) {
        guard let path = relativePath(url) else { return }
        attempt("use") {
            try run("UPDATE item SET used = ? WHERE path = ?", bind: { stmt in
                bind(stmt, 1, Date().timeIntervalSince1970)
                bind(stmt, 2, path)
            })
        }
    }

    /// Taken down, or its file is gone: out of the history.
    func remove(_ url: URL) {
        guard let path = relativePath(url) else { return }
        remove(path: path)
    }

    private func remove(path: String) {
        attempt("remove") {
            try transaction {
                try run("DELETE FROM item_text WHERE rowid IN (SELECT id FROM item WHERE path = ?)",
                        bind: { bind($0, 1, path) })
                try run("DELETE FROM item WHERE path = ?", bind: { bind($0, 1, path) })
            }
        }
    }

    /// The words already folded. Words of three characters or more go to the
    /// trigram index as quoted phrases; shorter ones — two Chinese
    /// characters are common — are substring checks on the folded text.
    func search(_ words: [String], onLine: [URL], limit: Int) -> Found {
        var found = Found()
        let long = words.filter { $0.unicodeScalars.count >= 3 }
        let short = words.filter { $0.unicodeScalars.count < 3 }
        guard !long.isEmpty || !short.isEmpty else { return found }
        var conditions: [String] = []
        var params: [String] = []
        if !long.isEmpty {
            let match = long.map { "\"\($0.replacingOccurrences(of: "\"", with: "\"\""))\"" }
                .joined(separator: " ")
            conditions.append("item_text MATCH ?")
            params.append(match)
        }
        for word in short {
            conditions.append("instr(item_text.text, ?) > 0")
            params.append(word)
        }
        let sql = """
            SELECT item.path FROM item_text JOIN item ON item.id = item_text.rowid
            WHERE \(conditions.joined(separator: " AND "))
            ORDER BY item.used DESC, item.id DESC
            """
        let onLineByPath = Dictionary(onLine.map {
            ($0.standardizedFileURL.path, $0)
        }, uniquingKeysWith: { first, _ in first })
        var stale: [String] = []
        do {
            let stmt = try prepare(sql)
            defer { sqlite3_finalize(stmt) }
            for (index, param) in params.enumerated() { bind(stmt, Int32(index + 1), param) }
            var step = sqlite3_step(stmt)
            while step == SQLITE_ROW {
                if let cString = sqlite3_column_text(stmt, 0) {
                    let path = String(cString: cString)
                    let url = absoluteURL(path)
                    if let passed = onLineByPath[url.standardizedFileURL.path] {
                        found.onLine.insert(passed)
                    } else if found.basket.count < limit {
                        // Only a row about to be appended is checked: its
                        // file may be gone. The rest are counted as they are.
                        if FileManager.default.fileExists(atPath: url.path) {
                            found.basketTotal += 1
                            found.basket.append(url)
                        } else {
                            stale.append(path)
                        }
                    } else {
                        found.basketTotal += 1
                    }
                }
                step = sqlite3_step(stmt)
            }
            if step != SQLITE_DONE {
                log.error("History search failed: \(String(cString: sqlite3_errmsg(self.db)), privacy: .public)")
            }
        } catch {
            log.error("History search failed: \(error.localizedDescription, privacy: .public)")
            return found
        }
        for path in stale { remove(path: path) }
        return found
    }

    // MARK: Rows and files

    private struct Row {
        let hash: Data
        let modified: Double
    }

    /// The note's whole plain text: a styled note reads as its words.
    private func readText(_ url: URL) -> String? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        if url.pathExtension.lowercased() == "rtf" {
            return NSAttributedString(rtf: data, documentAttributes: nil)?.string
        }
        return String(decoding: data, as: UTF8.self)
    }

    /// `Notes/Text ….txt`, relative to the data folder.
    private func relativePath(_ url: URL) -> String? {
        let base = DataFolder.url.standardizedFileURL.path
        let path = url.standardizedFileURL.path
        guard path.hasPrefix(base + "/") else { return nil }
        return String(path.dropFirst(base.count + 1))
    }

    private func absoluteURL(_ path: String) -> URL {
        path.split(separator: "/").reduce(DataFolder.url) { $0.appendingPathComponent(String($1)) }
    }

    private func row(path: String) -> Row? {
        guard let stmt = try? prepare("SELECT hash, modified FROM item WHERE path = ?") else { return nil }
        defer { sqlite3_finalize(stmt) }
        bind(stmt, 1, path)
        guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
        var hash = Data()
        if let bytes = sqlite3_column_blob(stmt, 0) {
            hash = Data(bytes: bytes, count: Int(sqlite3_column_bytes(stmt, 0)))
        }
        return Row(hash: hash, modified: sqlite3_column_double(stmt, 1))
    }

    // MARK: SQLite

    private func attempt(_ what: String, _ work: () throws -> Void) {
        do { try work() }
        catch { log.error("History \(what, privacy: .public) failed: \(error.localizedDescription, privacy: .public)") }
    }

    private func transaction(_ body: () throws -> Void) throws {
        try run("BEGIN IMMEDIATE")
        do {
            try body()
            try run("COMMIT")
        } catch {
            try? run("ROLLBACK")
            throw error
        }
    }

    private func prepare(_ sql: String) throws -> OpaquePointer {
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else {
            throw Failure(message: String(cString: sqlite3_errmsg(db)))
        }
        return stmt
    }

    private func run(_ sql: String, bind applyBind: (OpaquePointer) -> Void = { _ in }) throws {
        let stmt = try prepare(sql)
        defer { sqlite3_finalize(stmt) }
        applyBind(stmt)
        guard sqlite3_step(stmt) == SQLITE_DONE else {
            throw Failure(message: String(cString: sqlite3_errmsg(db)))
        }
    }

    private func scalar(_ sql: String, bind applyBind: (OpaquePointer) -> Void = { _ in }) throws -> Int64 {
        let stmt = try prepare(sql)
        defer { sqlite3_finalize(stmt) }
        applyBind(stmt)
        guard sqlite3_step(stmt) == SQLITE_ROW else { return 0 }
        return sqlite3_column_int64(stmt, 0)
    }

    private func strings(_ sql: String, bind applyBind: (OpaquePointer) -> Void = { _ in }) throws -> [String] {
        let stmt = try prepare(sql)
        defer { sqlite3_finalize(stmt) }
        applyBind(stmt)
        var out: [String] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            if let cString = sqlite3_column_text(stmt, 0) { out.append(String(cString: cString)) }
        }
        return out
    }

    private func bind(_ stmt: OpaquePointer, _ index: Int32, _ text: String) {
        sqlite3_bind_text(stmt, index, text, -1, Self.transient)
    }

    private func bind(_ stmt: OpaquePointer, _ index: Int32, _ blob: Data) {
        _ = blob.withUnsafeBytes { sqlite3_bind_blob(stmt, index, $0.baseAddress, Int32($0.count), Self.transient) }
    }

    private func bind(_ stmt: OpaquePointer, _ index: Int32, _ value: Double) {
        sqlite3_bind_double(stmt, index, value)
    }

    private func bind(_ stmt: OpaquePointer, _ index: Int32, _ value: Int64) {
        sqlite3_bind_int64(stmt, index, value)
    }

    /// SQLITE_TRANSIENT: SQLite copies what it is given.
    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
}
