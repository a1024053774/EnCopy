import AppKit
import Vision

/// The words on the non-text items that hang, so the line can be searched.
/// A screenshot's or a recording's are read once with the system's text
/// recognition when it hangs, on this Mac and in the background, and kept
/// on disk with the date of the file they came from. A note's words are
/// the history's, not this index's.
///
/// Recognition settings follow JOHN-AMAO's search in #3.
@MainActor
final class SearchIndex {
    private struct Entry: Codable {
        var modified: Double
        var text: String
    }

    private var entries: [String: Entry] = [:]
    private var reading: Set<String> = []
    private var saveWork: DispatchWorkItem?
    private let queue = DispatchQueue(label: "app.encopy.read-text", qos: .utility)
    /// Called when new words arrive, so a search under way sees them.
    var onChange: () -> Void = {}
    /// The words read from one picture, when they are ready.
    var onRead: (URL, String) -> Void = { _, _ in }

    private static let file = DataFolder.url.appendingPathComponent("SearchIndex.json")

    init() {
        if let data = try? Data(contentsOf: Self.file),
           let saved = try? JSONDecoder().decode([String: Entry].self, from: data) {
            entries = saved
        }
    }

    /// Keeps the words of exactly what hangs: reads what is new or changed,
    /// forgets what left. Cheap when nothing changed.
    func update(_ urls: [URL]) {
        let live = Set(urls.map(\.path))
        let before = entries.count
        entries = entries.filter { live.contains($0.key) }
        if entries.count != before { scheduleSave() }
        for url in urls where !isText(url) { read(url) }
    }

    private func read(_ url: URL) {
        let path = url.path
        guard !reading.contains(path), let modified = Note.modificationDate(url)?.timeIntervalSince1970 else { return }
        if entries[path]?.modified == modified { return }
        reading.insert(path)
        queue.async { [weak self] in
            let text = Self.recognize(url)
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.reading.remove(path)
                    self.entries[path] = Entry(modified: modified, text: Self.fold(text ?? ""))
                    self.onRead(url, text ?? "")
                    self.scheduleSave()
                    self.onChange()
                }
            }
        }
    }

    /// The text in a picture, or in a recording's first frame.
    nonisolated private static func recognize(_ url: URL) -> String? {
        // A file is found by its name and its folder, not by reading a picture of it.
        let name = url.lastPathComponent + " " + url.deletingLastPathComponent().lastPathComponent
        if isFileList(url) { return FileList.words(FileList.paths(url)) }
        if isFile(url) { return name }
        // A picture or video copied in Finder is found by its name too, as
        // in Finder; EnCopy's own screenshots only by what they show (review 2, on #4).
        let own = url.standardizedFileURL.path.hasPrefix(DataFolder.url.standardizedFileURL.path + "/")
        let named = own ? nil : name
        guard let image = makeThumbnail(url, maxPixels: 2400)?
            .cgImage(forProposedRect: nil, context: nil, hints: nil) else { return named }
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        request.automaticallyDetectsLanguage = true
        do {
            try VNImageRequestHandler(cgImage: image).perform([request])
        } catch {
            return nil
        }
        let seen = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
        return named.map { $0 + "\n" + seen } ?? seen
    }

    /// Whether an item has every word of the search, ignoring case, accents
    /// and width: "menu" finds "Menú". Only non-text items are asked: a
    /// note's words are the history's.
    func matches(_ url: URL, _ words: [String]) -> Bool {
        let text = entries[url.path]?.text ?? ""
        return words.allSatisfy { text.contains($0) }
    }

    /// The words of a search, folded the same way as what is searched.
    static func words(_ query: String) -> [String] {
        fold(query).split(whereSeparator: \.isWhitespace).map(String.init)
    }

    nonisolated static func fold(_ text: String) -> String {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil)
    }

    /// Written a moment after the last change, private to the user, like the
    /// screenshots it comes from.
    private func scheduleSave() {
        saveWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, let data = try? JSONEncoder().encode(self.entries) else { return }
            try? FileManager.default.createDirectory(at: Self.file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? data.write(to: Self.file, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: Self.file.path)
        }
        saveWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1, execute: work)
    }
}
