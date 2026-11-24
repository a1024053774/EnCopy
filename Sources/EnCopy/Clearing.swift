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
            guard let path = history.relative(url) else { continue }
            // Already gone, deleted in Finder say: only its row is left to clear.
            guard fm.fileExists(atPath: url.path) else { history.remove(url); continue }
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
        var left: [Entry] = []
        for entry in batch.entries {
            let from = dir.appendingPathComponent(entry.path)
            guard fm.fileExists(atPath: from.path) else { continue }
            // A name taken in the meantime: the next free one, never over it.
            let to = Self.free(history.absolute(entry.path))
            do {
                try fm.createDirectory(at: to.deletingLastPathComponent(), withIntermediateDirectories: true)
                try fm.moveItem(at: from, to: to)
            } catch {
                // It stays in the batch, to try again (review #2).
                left.append(entry)
                log.error("Could not put back \(entry.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
                continue
            }
            if isText(to) {
                history.record(to, app: entry.app)
            } else if isFileList(to) {
                history.recordFileList(to, app: entry.app)
            } else {
                history.recordImage(to, text: entry.words ?? "")
                history.setApp(to, entry.app)
            }
            history.setUsed(to, Date(timeIntervalSince1970: entry.used))
            if entry.shrunk { history.setShrunk(to, true) }
            back.append(to)
        }
        if left.isEmpty {
            try? fm.removeItem(at: dir)
        } else {
            var rest = batch
            rest.entries = left
            try? JSONEncoder().encode(rest).write(to: dir.appendingPathComponent("batch.json"), options: .atomic)
        }
        return back
    }

    /// `url`, or the same name with " 2", " 3"… when that is taken.
    private static func free(_ url: URL) -> URL {
        let folder = url.deletingLastPathComponent(), name = url.lastPathComponent
        let base = (name as NSString).deletingPathExtension, ext = (name as NSString).pathExtension
        var candidate = url, n = 2
        while FileManager.default.fileExists(atPath: candidate.path) {
            candidate = folder.appendingPathComponent(ext.isEmpty ? "\(base) \(n)" : "\(base) \(n).\(ext)")
            n += 1
        }
        return candidate
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
    nonisolated static let defaultCap: Int64 = 1 << 30
    nonisolated static let thumbnailPixels = 640

    struct Result: Equatable {
        var expired: [URL] = []
        var shrunk: [URL] = []
        /// Originals already gone, deleted in Finder say: only their rows are left.
        var missing: [URL] = []
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
            } else if isFileList(item.url) {
                // Files copied in Finder (T-049): a year, like text; their
                // list goes as soon as every file it names is gone.
                if age > textDays * 86400 || FileList.present(item.url).isEmpty { result.expired.append(item.url) }
            } else if image {
                guard FileManager.default.fileExists(atPath: item.url.path) else {
                    result.missing.append(item.url)
                    continue
                }
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
                    now: Date = Date(), cap: Int64 = defaultCap, inBackground: Bool = false) -> Result {
        let result = plan(history: history, onLine: onLine, isKeptImage: isKeptImage, now: now, cap: cap)
        for url in result.missing { history.remove(url) }
        if inBackground {
            // Decoding and encoding big pictures stays off the main thread
            // (review #15). Writing back happens on the main thread, and only
            // for what is still there: a picture cleared meanwhile stays
            // cleared (review 2 #5).
            let urls = result.shrunk
            DispatchQueue.global(qos: .utility).async {
                let made = urls.compactMap { url in smallerData(url).map { (url, $0) } }
                DispatchQueue.main.async {
                    for (url, data) in made where history.contains(url) && FileManager.default.fileExists(atPath: url.path) {
                        if write(data, to: url) { history.markShrunk(url) }
                    }
                }
            }
        } else {
            for url in result.shrunk { shrink(url, history: history) }
        }
        if !result.expired.isEmpty { clearing.clear(result.expired, reason: "expired", history: history, now: now) }
        clearing.purge(now: now)
        return result
    }

    /// The original is replaced by a thumbnail at the same path. It cannot come back.
    static func shrink(_ url: URL, history: History) {
        if smaller(url) { history.markShrunk(url) }
    }

    /// Writes the thumbnail over the original, in the original's own format
    /// (a JPEG stays a JPEG). Safe off the main thread. Returns whether it did.
    nonisolated static func smaller(_ url: URL) -> Bool {
        guard let data = smallerData(url) else { return false }
        return write(data, to: url)
    }

    /// The thumbnail, encoded in the original's own format. Safe off the main thread.
    nonisolated static func smallerData(_ url: URL) -> Data? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let type = CGImageSourceGetType(source),
              let thumb = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceCreateThumbnailWithTransform: true,
                  kCGImageSourceThumbnailMaxPixelSize: thumbnailPixels,
              ] as CFDictionary) else { return nil }
        let data = NSMutableData()
        guard let output = CGImageDestinationCreateWithData(data, type, 1, nil) else { return nil }
        CGImageDestinationAddImage(output, thumb, nil)
        guard CGImageDestinationFinalize(output) else { return nil }
        return data as Data
    }

    nonisolated static func write(_ data: Data, to url: URL) -> Bool {
        do {
            try data.write(to: url, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            return true
        } catch {
            log.error("Could not shrink \(url.lastPathComponent, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return false
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
        // The words through the full-text index, once, not row by row (review #15).
        let matching: Set<String>? = words.isEmpty ? nil
            : Set(history.search(words, onLine: [], limit: max(1, history.count)).basket.map { $0.standardizedFileURL.path })
        let picked = history.kept().filter { item in
            guard !onLine.contains(item.url.standardizedFileURL.path) else { return false }
            if let days = olderThanDays, now.timeIntervalSince(item.used) <= days * 86400 { return false }
            if let app, item.app != app { return false }
            if let kind {
                // Text or link from the words kept, not by reading each note (review 2, on #15).
                let actual: Line.Kind = isText(item.url) ? (Note.isLink(item.url, words: history.words(of: item.url)) ? .link : .text)
                    : isKeptImage(item.url) ? .image : .file
                if actual != kind { return false }
            }
            if let matching, !matching.contains(item.url.standardizedFileURL.path) { return false }
            return true
        }
        // What is gone from the disk is not listed as going to Recently
        // Cleared; its row goes now (review 2, on #13).
        var present: [History.Kept] = []
        for item in picked {
            if FileManager.default.fileExists(atPath: item.url.path) { present.append(item) } else { history.remove(item.url) }
        }
        return present.sorted { $0.used > $1.used }.map(\.url)
    }
}
