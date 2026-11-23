import AppKit

/// 「最近清掉」 (recently cleared): what expiry or a manual cleanup took away
/// waits here for seven days, in batches, and a whole batch can go back.
/// Files move into a folder of their own; each batch keeps a small manifest
/// with what the history knew, so putting it back restores the rows too.
@MainActor
final class Clearing {
    struct Entry: Codable {
        var path: String
        var used: Double
        var app: String?
        var words: String?
        var shrunk: Bool
    }

    struct Batch: Codable {
        var id: String
        var date: Date
        /// "expired" or "manual".
        var reason: String
        var entries: [Entry]
    }

    static let keepDays: Double = 7
    let folder: URL

    init(folder: URL = DataFolder.url.appendingPathComponent("Recently Cleared", isDirectory: true)) {
        self.folder = folder
    }

    /// Moves these items out of the history into a new batch. Returns it, or
    /// nil when nothing could move.
    @discardableResult
    func clear(_ urls: [URL], reason: String, history: History, now: Date = Date()) -> Batch? {
        let id = String(Int(now.timeIntervalSince1970 * 1000)) + "-" + UUID().uuidString.prefix(6)
        let dir = folder.appendingPathComponent(id, isDirectory: true)
        let fm = FileManager.default
        let kept = Dictionary(history.kept().map { ($0.url.standardizedFileURL.path, $0) }, uniquingKeysWith: { a, _ in a })
        var entries: [Entry] = []
        for url in urls {
            guard let path = history.relative(url), fm.fileExists(atPath: url.path) else { continue }
            let target = dir.appendingPathComponent(path)
            do {
                try fm.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
                let info = kept[url.standardizedFileURL.path]
                let entry = Entry(path: path, used: (info?.used ?? now).timeIntervalSince1970, app: info?.app,
                                  words: isText(url) ? nil : history.words(of: url), shrunk: info?.shrunk ?? false)
                try fm.moveItem(at: url, to: target)
                history.remove(url)
                entries.append(entry)
            } catch {
                log.error("Could not clear \(url.lastPathComponent, privacy: .public): \(error.localizedDescription, privacy: .public)")
            }
        }
        guard !entries.isEmpty else { try? fm.removeItem(at: dir); return nil }
        let batch = Batch(id: id, date: now, reason: reason, entries: entries)
        do {
            try JSONEncoder().encode(batch).write(to: dir.appendingPathComponent("batch.json"), options: .atomic)
            try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: dir.path)
        } catch {
            log.error("Could not write a cleared batch: \(error.localizedDescription, privacy: .public)")
        }
        return batch
    }

    /// Every batch still kept, newest first.
    func batches() -> [Batch] {
        let dirs = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        return dirs.compactMap { dir in
            (try? Data(contentsOf: dir.appendingPathComponent("batch.json"))).flatMap { try? JSONDecoder().decode(Batch.self, from: $0) }
        }.sorted { $0.date > $1.date }
    }

    /// The whole batch goes back where it was, rows and all. Returns what came back.
    @discardableResult
    func putBack(_ id: String, history: History) -> [URL] {
        let dir = folder.appendingPathComponent(id, isDirectory: true)
        guard let batch = batches().first(where: { $0.id == id }) else { return [] }
        let fm = FileManager.default
        var back: [URL] = []
        for entry in batch.entries {
            let from = dir.appendingPathComponent(entry.path)
            let to = history.absolute(entry.path)
            guard fm.fileExists(atPath: from.path), !fm.fileExists(atPath: to.path) else { continue }
            do {
                try fm.createDirectory(at: to.deletingLastPathComponent(), withIntermediateDirectories: true)
                try fm.moveItem(at: from, to: to)
            } catch { continue }
            if isText(to) { history.record(to, app: entry.app) } else { history.recordImage(to, text: entry.words ?? "") }
            history.setUsed(to, Date(timeIntervalSince1970: entry.used))
            if entry.shrunk { history.setShrunk(to, true) }
            back.append(to)
        }
        try? fm.removeItem(at: dir)
        return back
    }

    /// Batches older than seven days are deleted for good.
    func purge(now: Date = Date()) {
        for batch in batches() where now.timeIntervalSince(batch.date) > Self.keepDays * 86400 {
            try? FileManager.default.removeItem(at: folder.appendingPathComponent(batch.id, isDirectory: true))
        }
    }
}

/// T-004's rules, run once a day and at launch: text and thumbnails stay a
/// year after their last use; a picture's original shrinks to a thumbnail
/// after 30 days, or sooner, oldest first, when originals pass the cap.
/// Whatever hangs on the line is left alone. Expired items go to 「最近清掉」.
@MainActor
enum Retention {
    static let textDays: Double = 365
    static let originalDays: Double = 30
    static let defaultCap: Int64 = 1 << 30
    static let thumbnailPixels = 640

    struct Result: Equatable {
        var expired: [URL] = []
        var shrunk: [URL] = []
    }

    /// What the rules would do now, without doing it (the settings show this).
    static func plan(history: History, onLine: Set<String>, isKeptImage: (URL) -> Bool,
                     now: Date = Date(), cap: Int64 = defaultCap) -> Result {
        var result = Result()
        var originals: [(url: URL, used: Date, size: Int64)] = []
        for item in history.kept() where !onLine.contains(item.url.standardizedFileURL.path) {
            let age = now.timeIntervalSince(item.used)
            let image = isKeptImage(item.url)
            if isText(item.url) || (image && item.shrunk) {
                if age > textDays * 86400 { result.expired.append(item.url) }
            } else if image {
                if age > originalDays * 86400 {
                    result.shrunk.append(item.url)
                } else {
                    let size = (try? item.url.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init) ?? 0
                    originals.append((item.url, item.used, size))
                }
            }
        }
        var total = originals.reduce(0) { $0 + $1.size }
        for original in originals.sorted(by: { $0.used < $1.used }) where total > cap {
            result.shrunk.append(original.url)
            total -= original.size
        }
        return result
    }

    @discardableResult
    static func run(history: History, clearing: Clearing, onLine: Set<String>, isKeptImage: (URL) -> Bool,
                    now: Date = Date(), cap: Int64 = defaultCap) -> Result {
        let result = plan(history: history, onLine: onLine, isKeptImage: isKeptImage, now: now, cap: cap)
        for url in result.shrunk { shrink(url, history: history) }
        if !result.expired.isEmpty { clearing.clear(result.expired, reason: "expired", history: history, now: now) }
        clearing.purge(now: now)
        return result
    }

    /// The original is replaced by a thumbnail at the same path. It cannot come back.
    static func shrink(_ url: URL, history: History) {
        guard let thumb = makeThumbnail(url, maxPixels: thumbnailPixels),
              let tiff = thumb.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
              let png = rep.representation(using: .png, properties: [:]) else { return }
        do {
            try png.write(to: url, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            history.markShrunk(url)
        } catch {
            log.error("Could not shrink \(url.lastPathComponent, privacy: .public): \(error.localizedDescription, privacy: .public)")
        }
    }
}

/// T-005's manual cleanup: pick by kind, age, source app and the words it
/// holds; see what would go; clear it into 「最近清掉」. What hangs on the line
/// (pinned included) is never picked.
@MainActor
struct CleanupFilter: Equatable {
    var kind: Line.Kind?
    /// Last used more than this many days ago; nil is any time.
    var olderThanDays: Double?
    /// A source app's bundle id; nil is any app.
    var app: String?
    var text = ""

    func candidates(history: History, onLine: Set<String>, isKeptImage: (URL) -> Bool, now: Date = Date()) -> [URL] {
        let words = SearchIndex.words(text)
        return history.kept().filter { item in
            guard !onLine.contains(item.url.standardizedFileURL.path) else { return false }
            if let days = olderThanDays, now.timeIntervalSince(item.used) <= days * 86400 { return false }
            if let app, item.app != app { return false }
            if let kind {
                let actual: Line.Kind = isText(item.url) ? (Note.cached(item.url)?.link != nil ? .link : .text)
                    : isKeptImage(item.url) ? .image : .file
                if actual != kind { return false }
            }
            if !words.isEmpty {
                let have = history.words(of: item.url) ?? ""
                if !words.allSatisfy({ have.contains($0) }) { return false }
            }
            return true
        }.sorted { $0.used > $1.used }.map(\.url)
    }
}
