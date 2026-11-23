import AppKit
import AVFoundation
import SwiftUI
import UniformTypeIdentifiers
import Combine
import os

let log = Logger(subsystem: "app.encopy.EnCopy", category: "line")

/// One screenshot hanging on the line.
struct Pegged: Identifiable, Equatable {
    let id = UUID()
    let url: URL
    var thumb: NSImage
    /// Every photo hangs a little crooked, like on a real line.
    let tilt = Double.random(in: -2.5...2.5)
    var falling = false
    /// Still flying in from where it was captured; the card waits hidden.
    var flying = false
    /// Pinned, left of the knot: it stays until you take it down.
    var kept = false
    /// Taken off the line to hold in front of you. Its clip waits empty
    /// until it comes back.
    var held = false
    /// Hung from another app with Hang on EnCopy, rather than pinned
    /// from the photos that came and went. Always pinned.
    var byHand = false
    /// A screen recording rather than a screenshot.
    var isRecording: Bool { EnCopy.isRecording(url) }

    static func == (a: Pegged, b: Pegged) -> Bool {
        a.id == b.id && a.falling == b.falling && a.flying == b.flying && a.kept == b.kept && a.byHand == b.byHand && a.held == b.held && a.thumb === b.thumb
    }
}

/// The line itself: what hangs on it and what you can do with each item.
/// The files never move. The line is only a view onto them.
@MainActor
final class Line: ObservableObject {
    @Published private(set) var items: [Pegged] = []
    @Published private(set) var gust = 0
    @Published private(set) var copiedIDs: Set<UUID> = []
    @Published private(set) var selectedIDs: Set<UUID> = []
    private var copyGeneration = 0
    @Published var draggingID: UUID?
    @Published var pressedID: UUID?
    /// Whether the line has slid down into view.
    @Published var revealed = false {
        didSet {
            guard revealed != oldValue else { return }
            gustWork?.cancel()
            gustWork = nil
            if revealed { scheduleGust(); refreshNotes() }
        }
    }

    /// Card frames in window coordinates, reported by the views. The panel
    /// uses them to only catch clicks over photos and let the rest through.
    var hitRects: [UUID: CGRect] = [:]

    var maxItems = 8
    /// How many photos fit across the screen. With Keep on line set, the
    /// line can hold more, and the rest are reached by scrolling.
    var visibleCount = 8
    /// How far the line is scrolled towards older photos, in points; 0 shows
    /// the newest.
    @Published var scroll: CGFloat = 0

    /// Keep on line, from the menu bar: how many photos the line keeps, or
    /// nil for as many as fit across the screen.
    nonisolated static let keepChoices = [25, 50, 100]
    nonisolated static var keepOnLine: Int? {
        get {
            let n = UserDefaults.standard.integer(forKey: "keepOnLine")
            return keepChoices.contains(n) ? n : nil
        }
        set { UserDefaults.standard.set(newValue ?? 0, forKey: "keepOnLine") }
    }


    var soundOn: Bool {
        get { !UserDefaults.standard.bool(forKey: "soundOff") }
        set { UserDefaults.standard.set(!newValue, forKey: "soundOff") }
    }

    var liveCount: Int { items.filter { !$0.falling }.count }

    // MARK: Searching

    /// The words of what hangs, read once per item.
    let search = SearchIndex()
    /// What is typed in the search field under the line.
    @Published var query = "" {
        didSet { lastKeyAt = Date(); recomputeSearch() }
    }

    /// When a key last acted on the line: typing, picking, Tab, ↓. The
    /// breeze waits until the keyboard has been quiet a while.
    var lastKeyAt = Date.distantPast

    /// How the line came down this time: ⌥C or sliding past the top edge.
    @Published var cameBySummon = false
    /// Bumped each time the line comes down: the papers sway as it lands.
    @Published private(set) var landing = 0
    func landed(bySummon: Bool) {
        cameBySummon = bySummon
        landing += 1
        bounceRope(from: Motion.ropeTaut, bySummon ? Motion.ropeSummon : Motion.ropeSlide)
    }

    /// How far the rope sags, 1 at rest; animated, it bounces (T-038).
    @Published private(set) var sagScale: CGFloat = 1

    /// The rope pulled to `taut`, then let go into its sag.
    private func bounceRope(from taut: CGFloat, _ spring: Animation) {
        guard !Motion.reduced else { return }
        var still = Transaction()
        still.disablesAnimations = true
        withTransaction(still) { sagScale = taut }
        DispatchQueue.main.async { withAnimation(spring) { self.sagScale = 1 } }
    }

    /// The line slid to follow the pick: which way the papers swing, and a
    /// count to tell one slide from the next.
    struct Slide: Equatable { var count = 0; var degrees: Double = 0 }
    @Published private(set) var slide = Slide()
    /// When the arrows last moved the pick: a held key repeats faster than
    /// `Motion.keyRepeat`, and slides without swinging.
    var lastPickMove = Date.distantPast
    var isSearching: Bool { !SearchIndex.words(query).isEmpty }

    /// What the search found in the basket, oldest first: cards that are
    /// not on the line.
    @Published private(set) var basketCards: [Pegged] = []
    /// The last search's answer for the line's own notes.
    private var searchFound = History.Found()
    /// Everything that matches, on the line and in the basket, shown or not.
    private(set) var matchCount = 0
    /// Everything there is to search: what hangs plus what is in the basket.
    private(set) var totalCount = 0

    /// What hangs, narrowed by the search. A photo that is falling stays,
    /// so its fall is not cut short. While searching, the basket's matches
    /// hang to the left of the line's own.
    var shown: [Pegged] {
        let words = SearchIndex.words(query)
        let fits = { (p: Pegged) in self.kindFilter == nil || self.kind(of: p.url) == self.kindFilter }
        // On a shelf of the basket (↓), its cards stand in for the line's,
        // searched or not: a search on a shelf searches that shelf (review #9).
        if shelf != .line { return basketCards.filter(fits) }
        guard !words.isEmpty else { return kindFilter == nil ? items : items.filter { $0.falling || fits($0) } }
        return basketCards.filter(fits) + items.filter { $0.falling || (matches($0) && fits($0)) }
    }

    /// ↓ while summoned walks the basket by when things were last used (T-033).
    enum Shelf: Int, CaseIterable { case line, today, yesterday, week, earlier }

    @Published var shelf: Shelf = .line {
        didSet { if shelf != oldValue { loadShelf() } }
    }

    /// ↓ goes one shelf further back, ↑ one nearer; stops at either end.
    func moveShelf(_ step: Int) {
        lastKeyAt = Date()
        let was = shelf
        shelf = Shelf(rawValue: min(Shelf.allCases.count - 1, max(0, shelf.rawValue + step))) ?? .line
        if shelf != was { bounceRope(from: Motion.shelfTaut, Motion.ropeSummon) }
    }

    /// The time span a shelf covers, by last use: today, yesterday, the rest
    /// of the past week, and everything before.
    func span(of shelf: Shelf, now: Date = Date()) -> (from: Date?, to: Date?) {
        let start = Calendar.current.startOfDay(for: now)
        let day: TimeInterval = 86400
        switch shelf {
        case .line: return (nil, nil)
        case .today: return (start, nil)
        case .yesterday: return (start.addingTimeInterval(-day), start)
        case .week: return (start.addingTimeInterval(-7 * day), start.addingTimeInterval(-day))
        case .earlier: return (nil, start.addingTimeInterval(-7 * day))
        }
    }

    /// The shelf's cards: basket items in its span, newest at the right, as
    /// many as fit across the screen.
    private func loadShelf(now: Date = Date()) {
        guard shelf != .line else { basketCards = []; recomputeSearch(); return }
        // Searching: the search moves to this shelf.
        if isSearching { recomputeSearch(); return }
        let (from, to) = span(of: shelf, now: now)
        let onLine = onLinePaths
        let picked = history.kept().filter { item in
            !onLine.contains(item.url.standardizedFileURL.path)
                && from.map { item.used >= $0 } ?? true && to.map { item.used < $0 } ?? true
                && FileManager.default.fileExists(atPath: item.url.path)
        }.sorted { $0.used > $1.used }.prefix(visibleCount)
        basketCards = picked.reversed().compactMap { item in
            basketCards.first { $0.url == item.url } ?? makeThumbnail(item.url).map { Pegged(url: item.url, thumb: $0) }
        }
    }

    /// What kind of thing an item is, for Tab while summoned.
    enum Kind { case text, link, image, file }

    /// Tab while summoned narrows the line to one kind; nil shows them all.
    @Published var kindFilter: Kind?

    func kind(of url: URL) -> Kind {
        if isFile(url) { return .file }
        guard isText(url) else { return .image }
        return Note.cached(url)?.link != nil ? .link : .text
    }

    /// Tab: all → text → links → images → all; ⇧Tab goes the other way.
    func cycleKind(_ step: Int) {
        lastKeyAt = Date()
        let order: [Kind?] = [nil, .text, .link, .image, .file]
        let i = order.firstIndex(of: kindFilter) ?? 0
        kindFilter = order[(i + step + order.count) % order.count]
    }

    /// Whether a line item matches the words being searched: a note through
    /// the history's answer, anything else through the index, as before.
    private func matches(_ item: Pegged) -> Bool {
        if isText(item.url) { return searchFound.onLine.contains(item.url) }
        return search.matches(item.url, SearchIndex.words(query))
    }

    /// The search's basket half and counters, kept by the last query: run
    /// when the query changes, when the line changes under a search, and
    /// when the index finishes reading. The line's own matches come first;
    /// the basket fills the room left across the screen.
    private func recomputeSearch() {
        let words = SearchIndex.words(query)
        // On a shelf with nothing typed, the shelf's own cards (review #9).
        if words.isEmpty, shelf != .line { loadShelf(); return }
        let onShelf = shelf != .line
        // Everything on the line that the history also keeps: notes and EnCopy's own pictures.
        let liveText = items.filter { !$0.falling && (isText($0.url) || isKeptImage($0.url)) }.map(\.url)
        var lineMatches = 0
        if words.isEmpty {
            basketCards = []
            searchFound = History.Found()
            lineMatches = liveCount
        } else {
            var found = history.search(words, onLine: liveText, limit: onShelf ? 1000 : visibleCount)
            if onShelf {
                // Only what was last used in the shelf's span, and nothing from the line.
                let (from, to) = span(of: shelf)
                let used = Dictionary(history.kept().map { ($0.url.standardizedFileURL.path, $0.used) }, uniquingKeysWith: { a, _ in a })
                found.basket = found.basket.filter { url in
                    guard let date = used[url.standardizedFileURL.path] else { return false }
                    return from.map { date >= $0 } ?? true && to.map { date < $0 } ?? true
                }
                found.basketTotal = found.basket.count
            }
            searchFound = found
            lineMatches = onShelf ? 0 : items.filter { !$0.falling && matches($0) }.count
            let room = max(0, visibleCount - lineMatches)
            var cards: [Pegged] = []
            // Found.basket is newest first; the cards hang oldest of them
            // left, newest next to the line. A card already shown keeps
            // its identity, so it does not animate again.
            for url in found.basket.prefix(room).reversed() {
                if let kept = basketCards.first(where: { $0.url == url }) {
                    cards.append(kept)
                } else if let thumb = makeThumbnail(url) {
                    cards.append(Pegged(url: url, thumb: thumb))
                }
            }
            basketCards = cards
        }
        matchCount = lineMatches + searchFound.basketTotal
        totalCount = liveCount + max(0, history.count - liveText.count)
    }

    /// A card the search shows from the basket: kept, not on the line.
    func isInBasket(_ id: UUID) -> Bool {
        basketCards.contains { $0.id == id }
    }

    /// A basket card used again — copied, pasted, taken down — comes back
    /// onto the line as the newest, keeping its card and everything it
    /// knows. Not searching, there is no basket to reclaim from.
    private func reclaim(_ id: UUID, makingRoom: Bool = true) {
        guard let i = basketCards.firstIndex(where: { $0.id == id }) else { return }
        let item = basketCards.remove(at: i)
        // Like hang: sweep fallen, the newest end, room made when asked.
        sweepFallen()
        items.append(item)
        scroll = 0
        history.use(item.url)
        if makingRoom, liveCount > maxItems, let oldest = oldestLoose {
            letGo(oldest.id)
        }
        save()
    }

    /// A picture EnCopy keeps in its own folder — a screenshot or a copied
    /// image — goes into the history like a note, and into the basket when
    /// it falls off a full line (T-030).
    func isKeptImage(_ url: URL) -> Bool {
        // Optional on purpose: the checks stand in an Inbox whose folder may be unset.
        let folder: URL? = Inbox.folder
        guard !isText(url), !isRecording(url), !isFile(url), let folder else { return false }
        return url.standardizedFileURL.path.hasPrefix(folder.standardizedFileURL.path + "/")
    }

    /// Gives the history its row for what is kept: a note's words, or a
    /// picture's row, whose words come when they have been read.
    private func keep(_ url: URL) {
        if isText(url) { history.record(url) } else if isKeptImage(url) { history.recordImage(url, text: nil) }
    }

    /// Photos hung by hand. They hang together at the left end of the line,
    /// before the knot, and never fall to make room.
    var keptCount: Int { items.filter { !$0.falling && $0.kept }.count }

    /// How many photos at the start of the line are pinned, the empty
    /// places of pinned ones that just came down included.
    var pinnedLead: Int { items.prefix { $0.kept }.count }

    /// How many of what is shown hang pinned left of the knot: none while
    /// searching or on a shelf of the basket, where there is no knot.
    var layoutPinned: Int { isSearching || shelf != .line ? 0 : shown.prefix { $0.kept }.count }

    /// The oldest photo that can fall: the first one past the knot.
    private var oldestLoose: Pegged? { items.first { !$0.falling && !$0.kept && !$0.held } }

    private let storeKey = "pegged"
    private let pasteboard: NSPasteboard
    private let desktop: URL
    /// Every text note EnCopy keeps, on the line and in the basket.
    let history: History

    init(pasteboard: NSPasteboard = .general,
         desktop: URL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Desktop"),
         history: History? = nil) {
        self.pasteboard = pasteboard
        self.desktop = desktop
        self.history = history ?? History()
        let kept = self.history
        Note.sourceOf = { [weak kept] url in kept?.app(of: url).flatMap(History.appName) }
        Note.clearsOf = { [weak kept] url in kept?.ttl(of: url).map(Line.clearsText) }
        restore()
        // The words read from a kept picture go into the history, so the
        // basket finds it after it falls off the line.
        search.onRead = { [weak self] url, text in
            guard let self, self.isKeptImage(url) else { return }
            self.history.recordImage(url, text: text)
        }
        search.onChange = { [weak self] in
            self?.recomputeSearch()
            self?.objectWillChange.send()
        }
        // What already hangs is read once, a moment after launch.
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in self?.indexForSearch() }
    }

    private func indexForSearch() {
        search.update(items.filter { !$0.falling }.map(\.url))
    }

    // MARK: Hanging and dropping

    @discardableResult
    func hang(_ url: URL, quietly: Bool = false, flying: Bool = false) -> UUID? {
        guard !items.contains(where: { $0.url == url && !$0.falling }),
              let thumb = makeThumbnail(url) else { return nil }
        var item = Pegged(url: url, thumb: thumb)
        item.flying = flying
        // The line moves anyway for a new photo, so the gaps close with it.
        sweepFallen()
        items.append(item)
        keep(url)
        scroll = 0
        // A full line lets the oldest photo fall off the far end. Only one: a
        // line hung on a wider screen keeps its length here instead of losing
        // several photos to a single capture.
        if liveCount > maxItems, let oldest = oldestLoose {
            letGo(oldest.id)
        }
        save()
        if !quietly { play("Tink", volume: 0.35) }
        return item.id
    }

    /// The capture has reached the line: the real card takes over.
    func land(_ id: UUID) {
        guard let i = items.firstIndex(where: { $0.id == id }) else { return }
        items[i].flying = false
    }

    /// The same words copied again: the item goes back to the head of the line, from wherever it
    /// was. One that is pinned or held stays where you put it.
    @discardableResult func rehang(_ url: URL) -> UUID? {
        history.use(url)
        guard let item = items.first(where: { $0.url == url && !$0.falling }) else {
            return hang(url)
        }
        if moveToHead([item.id]) { scroll = 0 }
        return item.id
    }

    /// Used — pasted, copied out, or the same words copied again — items
    /// go to the head of the line, several in the order they hung. What
    /// is pinned or held stays where you put it. Returns whether any moved.
    @discardableResult private func moveToHead(_ ids: [UUID]) -> Bool {
        let ids = Set(ids)
        let moving = items.filter { ids.contains($0.id) && !$0.falling && !$0.kept && !$0.held }
        guard !moving.isEmpty else { return false }
        let movingIDs = Set(moving.map(\.id))
        items.removeAll { movingIDs.contains($0.id) }
        items.append(contentsOf: moving)
        save()
        return true
    }

    /// Called just before a photo starts falling, so the fall can be drawn
    /// over the whole screen.
    var onFall: ((Pegged) -> Void)?

    /// Hold, from a photo's menu: the app takes it off the line.
    var onHold: ((UUID) -> Void)?
    /// A click on a held photo's faded trace: it comes back.
    var onHangBack: ((UUID) -> Void)?

    func drop(_ id: UUID, quietly: Bool = false) {
        guard let i = items.firstIndex(where: { $0.id == id }), !items[i].falling else { return }
        selectedIDs.remove(id)
        onFall?(items[i])
        items[i].falling = true
        hitRects[id] = nil
        save()
        if !quietly { play("Pop", volume: 0.25) }
        lastFall = Date()
    }

    /// When the last photo started falling. A fallen photo keeps its empty
    /// place while the pointer stays on the line, so the next cross stays
    /// under it while discarding several in a row.
    private var lastFall = Date.distantPast

    /// The pointer left the line: the gaps close, once the last photo has
    /// finished falling.
    func closeGaps() {
        guard items.contains(where: { $0.falling }),
              Date().timeIntervalSince(lastFall) >= 0.6 else { return }
        sweepFallen()
    }

    /// The fallen photos leave their places and the rest close up.
    private func sweepFallen() {
        items.removeAll { $0.falling }
    }

    /// "Take everything down": every photo past the knot goes the way of its
    /// corner cross. What you hung by hand stays.
    func clear() {
        takeDown(items.filter { !$0.falling && !$0.kept && !$0.held })
    }

    /// The photos hung before (left) or after (right) this one.
    /// Left and right as they hang on screen (T-035): pinned ones first, then
    /// the newest, older ones further right. Nearest first.
    func neighbours(of id: UUID, toTheLeft: Bool) -> [Pegged] {
        let live = items.filter { !$0.falling }
        let pinned = live.prefix { $0.kept }.count
        let onScreen = Array(live.prefix(pinned)) + live.dropFirst(pinned).reversed()
        guard let i = onScreen.firstIndex(where: { $0.id == id }) else { return [] }
        let side = toTheLeft ? Array(onScreen[..<i].reversed()) : Array(onScreen[(i + 1)...])
        return side.filter { !$0.kept && !$0.held }
    }

    /// Takes down everything on one side of a photo. Each one goes the way
    /// of its own corner button: to the Trash from EnCopy's folder, and
    /// only off the line from anywhere else.
    func takeDown(_ id: UUID, toTheLeft: Bool) {
        takeDown(neighbours(of: id, toTheLeft: toTheLeft))
    }

    /// One after another, and only the first makes a sound. They are one
    /// step, so Put back brings them all back at once.
    private func takeDown(_ live: [Pegged]) {
        let step = UUID()
        for (n, item) in live.enumerated() {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.06 * Double(n)) { [weak self] in
                self?.discard(item.id, quietly: n > 0, step: step)
            }
        }
    }

    /// What a card says about clearing by itself.
    nonisolated static func clearsText(_ ttl: TimeInterval) -> String {
        ttl <= 600 ? L("Clears in 10 minutes") : L("Clears in an hour")
    }

    /// Copies under an app's hourly rule, and verification codes, whose time ran
    /// out since their last use: deleted outright, not to the Trash and not kept anywhere.
    func clearExpired(now: Date = Date()) {
        let gone = history.expired(at: now)
        guard !gone.isEmpty else { return }
        for url in gone {
            // Pinned stays, and never expires (review #5).
            if items.contains(where: { $0.url == url && $0.kept }) { history.setTTL(url, nil); continue }
            if let item = items.first(where: { $0.url == url && !$0.falling }) { drop(item.id, quietly: true) }
            history.remove(url)
            try? FileManager.default.removeItem(at: url)
        }
        if isSearching { recomputeSearch() }
    }

    /// 「最近清掉」, where expired and cleaned-up items wait seven days.
    lazy var clearing = Clearing()

    /// The paths of what hangs now: the retention rules and the cleanup panel leave them alone.
    var onLinePaths: Set<String> { Set(items.filter { !$0.falling }.map { $0.url.standardizedFileURL.path }) }

    /// What the cleanup panel would clear with this filter.
    func cleanupCandidates(_ filter: CleanupFilter, now: Date = Date()) -> [URL] {
        filter.candidates(history: history, onLine: onLinePaths, isKeptImage: isKeptImage, now: now)
    }

    /// Clears them into 「最近清掉」 as one batch.
    @discardableResult
    func cleanUp(_ urls: [URL], now: Date = Date()) -> Clearing.Batch? {
        // The panel listed these a while ago: what has come back onto the
        // line since stays (review #13).
        let onLine = onLinePaths
        let batch = clearing.clear(urls.filter { !onLine.contains($0.standardizedFileURL.path) },
                                   reason: "manual", history: history, now: now)
        if isSearching { recomputeSearch() }
        return batch
    }

    /// Already in the history: a file put back from Recently Cleared, not a
    /// screenshot just taken (review #7).
    func isKnown(_ url: URL) -> Bool { history.contains(url) }

    /// A batch from 「最近清掉」 goes back into the basket.
    func putBack(batch id: String) {
        clearing.putBack(id, history: history)
        if isSearching { recomputeSearch() }
    }

    /// The originals cap from the cleanup panel's retention settings.
    nonisolated static var originalsCap: Int64 {
        get { let v = UserDefaults.standard.object(forKey: "originalsCap") as? Int64 ?? 0; return v > 0 ? v : Retention.defaultCap }
        set { UserDefaults.standard.set(newValue, forKey: "originalsCap") }
    }

    /// T-004's daily rules: see `Retention`. Nothing on the line is touched.
    @discardableResult
    func runRetention(now: Date = Date(), cap: Int64 = Line.originalsCap) -> Retention.Result {
        let onLine = Set(items.filter { !$0.falling }.map { $0.url.standardizedFileURL.path })
        let result = Retention.run(history: history, clearing: clearing, onLine: onLine, isKeptImage: isKeptImage, now: now, cap: cap)
        if isSearching { recomputeSearch() }
        return result
    }

    /// Photos whose file was deleted or moved away fall off by themselves.
    func prune() {
        for item in items where !item.falling && !FileManager.default.fileExists(atPath: item.url.path) {
            history.remove(item.url)
            drop(item.id, quietly: true)
        }
    }

    // MARK: Selecting photos

    /// A selection follows the line's order, not the order of the clicks.
    var selectedItems: [Pegged] { actionItems(Array(selectedIDs)) }

    private func actionItems(_ ids: [UUID]) -> [Pegged] {
        let ids = Set(ids)
        return items.filter { ids.contains($0.id) && !$0.falling && !$0.held }
    }

    func toggleSelection(_ id: UUID) {
        guard !actionItems([id]).isEmpty else { return }
        if !selectedIDs.insert(id).inserted { selectedIDs.remove(id) }
    }

    func clearSelection() { selectedIDs.removeAll() }

    // MARK: Picking with the keyboard

    /// Set while the line is summoned: the one the arrow keys rest on.
    @Published private(set) var pickedID: UUID?

    /// What Return pastes. If the one picked has left the line or the
    /// search, the newest stands in.
    var picked: Pegged? {
        guard pickedID != nil else { return nil }
        let pool = pickable
        return pool.first { $0.id == pickedID } ?? pool.last
    }

    /// What can be picked: what is shown, minus what is falling or held.
    private var pickable: [Pegged] { shown.filter { !$0.falling && !$0.held } }

    /// What can be picked, left to right as it hangs, for the arrows.
    private var pickableInPlace: [Pegged] {
        let all = shown, pinned = layoutPinned
        return (Array(all.prefix(pinned)) + all.dropFirst(pinned).reversed()).filter { !$0.falling && !$0.held }
    }

    /// Summoned: the newest pickable is picked.
    func pickNewest() {
        pickedID = pickable.last?.id
        followPick()
    }

    /// -1 is ←, +1 is →, as they hang on screen; stops at either end, no wrap.
    /// The last arrow press came as a held key's repeat.
    private var held = false

    /// ← is -1 and → is +1: the pick moves the way the arrow points on screen.
    func movePick(_ step: Int) {
        let now = Date()
        held = now.timeIntervalSince(lastPickMove) < Motion.keyRepeat
        lastPickMove = now
        lastKeyAt = now
        let pool = pickableInPlace
        guard let current = picked, let i = pool.firstIndex(where: { $0.id == current.id }) else { return }
        pickedID = pool[min(pool.count - 1, max(0, i + step))].id
        followPick()
    }

    func clearPick() { pickedID = nil }

    /// On a line that scrolls, the picked card is kept in view.
    private func followPick() {
        let all = shown, pinned = layoutPinned
        guard Line.keepOnLine != nil, all.count > visibleCount, !isSearching, shelf == .line,
              let item = picked, let index = all.firstIndex(where: { $0.id == item.id }) else { return }
        let anchored = Layout.anchorsPinned(pinned, visible: visibleCount) ? pinned : 0
        guard index >= anchored else { return }
        let place = CGFloat(Layout.slot(index, count: all.count, pinned: pinned))
        let low = max(0, place - CGFloat(visibleCount - 1)) * Layout.spacing
        let high = (place - CGFloat(anchored)) * Layout.spacing
        let next = min(max(scroll, low), high)
        guard next != scroll else { return }
        // More scroll moves the papers left; they lag and swing right.
        if !held && !Motion.reduced {
            slide = Slide(count: slide.count + 1, degrees: next > scroll ? -Motion.slideSway : Motion.slideSway)
        }
        withAnimation(Motion.slide) { scroll = next }
    }

    // MARK: Actions on photos

    /// `plain`: words only, no styling (⇧⏎). A photo has no words and copies as usual.
    func copy(_ id: UUID, plain: Bool = false) {
        reclaim(id)
        guard let item = items.first(where: { $0.id == id && !$0.falling }) else { return }
        copyPhotos([item], plain: plain)
    }

    func copy(_ ids: [UUID]) {
        copyPhotos(actionItems(ids))
    }

    /// Copying is using: what is copied goes to the head of the line, so
    /// the next ⌥C picks what the clipboard holds. Pasting copies first,
    /// so it goes the same way.
    private func copyPhotos(_ photos: [Pegged], plain: Bool = false) {
        guard !photos.isEmpty else { return }
        putOnClipboard(photos.map(\.url), plain: plain)
        for url in photos.map(\.url) { history.use(url) }
        moveToHead(photos.map(\.id))
        flashCopied(photos)
    }

    private func flashCopied(_ photos: [Pegged]) {
        copiedIDs = Set(photos.map(\.id))
        copyGeneration += 1
        let generation = copyGeneration
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [weak self] in
            if self?.copyGeneration == generation { self?.copiedIDs = [] }
        }
    }

    /// The path, written as a terminal writes a file dropped on it, so it
    /// pastes whole into a shell or a coding agent. Several go one after
    /// another, a space apart.
    func copyPath(_ ids: [UUID]) {
        let photos = actionItems(ids)
        guard !photos.isEmpty else { return }
        let entry = NSPasteboardItem()
        entry.setString(photos.map { shellEscaped($0.url.path) }.joined(separator: " "), forType: .string)
        // EnCopy's own, so the text is not hung as a note.
        entry.setData(Data(), forType: selfCopyType)
        let pb = pasteboard
        pb.clearContents()
        pb.writeObjects([entry])
        flashCopied(photos)
    }

    private func shellEscaped(_ path: String) -> String {
        let special = Set(" \t\n\\'\"`$&|;<>()[]{}*?!#~")
        return path.reduce(into: "") { out, c in
            if special.contains(c) { out.append("\\") }
            out.append(c)
        }
    }

    /// The files last put on the clipboard, and the clipboard's change count
    /// right after. While the count is the same, nothing else has been copied.
    private var onClipboard: (urls: [URL], change: Int)?

    private func putOnClipboard(_ urls: [URL], plain: Bool = false) {
        // Several notes paste as one text, a blank line apart: most apps
        // paste only the first item of a copy.
        if urls.count > 1, urls.allSatisfy(isText) {
            let entry = NSPasteboardItem()
            let words = urls.compactMap { Note.cached($0).map { [$0.title, $0.body].filter { !$0.isEmpty }.joined(separator: "\n") } }
            entry.setString(words.joined(separator: "\n\n"), forType: .string)
            entry.setData(Data(), forType: selfCopyType)
            let pb = pasteboard
            pb.clearContents()
            pb.writeObjects([entry])
            onClipboard = (urls, pb.changeCount)
            return
        }
        let entries = urls.map { url in
            let entry = NSPasteboardItem()
            // A note is its words, styled and plain, marked as EnCopy's
            // own so it is not hung again.
            if isText(url) {
                if url.pathExtension.lowercased() == "rtf", let data = try? Data(contentsOf: url) {
                    let data = lightPageRTF(data)
                    if !plain { entry.setData(data, forType: .rtf) }
                    if let plain = NSAttributedString(rtf: data, documentAttributes: nil)?.string {
                        entry.setString(plain, forType: .string)
                    }
                } else if let text = try? String(contentsOf: url, encoding: .utf8) {
                    entry.setString(text, forType: .string)
                }
                entry.setData(Data(), forType: selfCopyType)
                return entry
            }
            // Each photo has its own image and file representation. Apps
            // that accept several files can paste the whole selection. A
            // file is only its path. Marked as EnCopy's own, so the watcher
            // does not hang it again.
            if !isRecording(url), !isFile(url), let png = pngData(url) { entry.setData(png, forType: .png) }
            entry.setString(url.absoluteString, forType: .fileURL)
            entry.setData(Data(), forType: selfCopyType)
            return entry
        }
        let pb = pasteboard
        pb.clearContents()
        pb.writeObjects(entries)
        onClipboard = (urls, pb.changeCount)
    }

    /// Apps like Finder or a chat window paste the copied file rather than the
    /// picture. When the file moves after a copy, to the Trash or the Desktop,
    /// the clipboard follows it, or those apps would find nothing to paste.
    private func clipboardFollows(_ url: URL, to moved: URL) {
        // A copied note is its words, which do not depend on the file: once
        // moved out of the notes folder it would go back as a bare file.
        guard !isText(url), let onClipboard, onClipboard.urls.contains(url),
              onClipboard.change == pasteboard.changeCount else { return }
        putOnClipboard(onClipboard.urls.map { $0 == url ? moved : $0 })
    }

    /// A double click pastes the item into the front app (T-007); the app
    /// sets this to its paster. Opening moved to the card's menu.
    var onPaste: ((UUID) -> Void)?

    func paste(_ id: UUID) {
        if let onPaste { onPaste(id) } else { copy(id) }
    }

    /// Opens the item: a copied link in the browser, anything else in its app.
    func open(_ id: UUID) {
        guard let item = items.first(where: { $0.id == id }) else { return }
        if isText(item.url), let link = Note.cached(item.url)?.link {
            NSWorkspace.shared.open(link)
        } else {
            NSWorkspace.shared.open(item.url)
        }
    }

    /// The file itself, in its app. This is how a note is edited, a link
    /// included, so one turned into a link by an edit can be edited back.
    func openFile(_ id: UUID) {
        guard let item = items.first(where: { $0.id == id }) else { return }
        NSWorkspace.shared.open(item.url)
    }

    func openLink(_ id: UUID) {
        guard let item = items.first(where: { $0.id == id }), isText(item.url),
              let link = Note.cached(item.url)?.link else { return }
        NSWorkspace.shared.open(link)
    }

    func open(_ ids: [UUID]) {
        for item in actionItems(ids) { open(item.id) }
    }

    /// Pinned screenshots must be unpinned first, just as in their own menu.
    func canDiscard(_ ids: [UUID]) -> Bool {
        let photos = actionItems(ids)
        return !photos.isEmpty && photos.allSatisfy { !$0.kept || $0.byHand }
    }

    /// Several dropped on the Dock's Trash together: one step puts them all
    /// back.
    func trash(_ ids: [UUID]) {
        let step = UUID()
        for (n, id) in ids.enumerated() { trash(id, quietly: n > 0, step: step) }
    }

    func discard(_ ids: [UUID]) {
        guard canDiscard(ids) else { NSSound.beep(); return }
        let photos = actionItems(ids)
        let step = UUID()
        // Finish the whole action before an undo can run. New arrivals are
        // not part of this snapshot, and failed removals stay selected.
        for (n, item) in photos.enumerated() {
            discard(item.id, quietly: n > 0, step: step)
        }
    }

    func pin(_ ids: [UUID]) {
        let step = UUID()
        for (n, item) in actionItems(ids).filter({ !$0.kept }).enumerated() {
            pin(item.id, quietly: n > 0, step: step)
        }
    }

    func unpin(_ ids: [UUID]) {
        let step = UUID()
        // Each photo joins the front of the loose ones, so work backwards.
        for item in actionItems(ids).reversed() { unpin(item.id, step: step) }
    }

    func canSaveToDesktop(_ ids: [UUID]) -> Bool {
        actionItems(ids).contains { canSaveToDesktop($0.id) }
    }

    func canSaveToDesktop(_ id: UUID) -> Bool {
        isInInbox(id) || isOnDesktop(id)
    }

    func saveToDesktop(_ ids: [UUID]) {
        // Inbox files move; files already on the Desktop get another copy.
        let photos = actionItems(ids).filter { canSaveToDesktop($0.id) }
        guard !photos.isEmpty else { return }
        let step = UUID()
        for item in photos { saveToDesktop(item.id, step: step) }
    }

    /// Moves the file to the Trash and takes the photo off the line. When a
    /// drag ends on the Dock's Trash, macOS only reports it: deleting the file
    /// is the source app's job, as Finder does.
    ///
    /// `step` is what Put back brings it back with. Nil when the line lets
    /// a photo go by itself, which is not undone.
    @discardableResult
    func trash(_ id: UUID, quietly: Bool = false, step: UUID? = UUID()) -> Bool {
        guard let item = items.first(where: { $0.id == id }) else { return false }
        let index = liveIndex(of: id)
        do {
            var trashed: NSURL?
            try FileManager.default.trashItem(at: item.url, resultingItemURL: &trashed)
            if let trashed { clipboardFollows(item.url, to: trashed as URL) }
            log.notice("Trashed \(item.url.lastPathComponent, privacy: .public)")
            if soundOn && !quietly { Line.trashSound?.play() }
            // Out of the history before the line saves: the search must
            // not find it again on the way down.
            history.remove(item.url)
            drop(id, quietly: true)
            if let step, let index {
                remember(TakenDown(url: item.url, index: index, trashed: trashed as URL?, kept: item.kept, byHand: item.byHand), in: step)
            }
            return true
        } catch {
            log.error("Could not trash \(item.url.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
            if !quietly { NSSound.beep() }
            return false
        }
    }

    private static let trashSound = NSSound(
        contentsOfFile: "/System/Library/Components/CoreAudio.component/Contents/SharedSupport/SystemSounds/dock/drag to trash.aif",
        byReference: true)

    /// Whether the file lives in EnCopy's own folder. Those are discarded
    /// to the Trash, or the folder would fill up with forgotten screenshots.
    /// Files anywhere else, like the Desktop, stay where they are.
    func isInInbox(_ id: UUID) -> Bool {
        guard let item = items.first(where: { $0.id == id }) ?? basketCards.first(where: { $0.id == id })
        else { return false }
        // Copied text and links are EnCopy's own files too.
        return item.url.standardizedFileURL.path.hasPrefix(Inbox.folder.standardizedFileURL.path + "/")
            || NoteFolder.contains(item.url)
    }

    /// What dragging this card out may do with its file (review #10). Only
    /// EnCopy's own files on the line can be moved into a folder or dropped
    /// on the Trash; a file from Finder, or a card from the basket, gives a
    /// copy and the original stays where it is.
    func dragOperations(for id: UUID) -> NSDragOperation {
        if isInBasket(id) { return .copy }
        return isInInbox(id) ? [.copy, .move, .delete] : .copy
    }

    func isOnDesktop(_ id: UUID) -> Bool {
        guard let item = items.first(where: { $0.id == id }) else { return false }
        return item.url.deletingLastPathComponent().standardizedFileURL == desktop.standardizedFileURL
    }

    /// What the button in a photo's corner does.
    enum Corner {
        /// A file in EnCopy's folder: to the Trash.
        case trash
        /// A file anywhere else, or hung by hand: off the line, file untouched.
        case hide
        /// Pinned from the photos that come and go: back among them.
        case unpin
    }

    func corner(of id: UUID) -> Corner {
        guard let item = items.first(where: { $0.id == id }) ?? basketCards.first(where: { $0.id == id })
        else { return .hide }
        if item.kept { return item.byHand ? .hide : .unpin }
        return isInInbox(id) ? .trash : .hide
    }

    /// The button in the corner.
    func pressCorner(_ id: UUID) {
        if corner(of: id) == .unpin { unpin(id) } else { discard(id) }
    }

    /// The corner cross and "Take down" both end up here. A pinned photo is
    /// never sent to the Trash: it only leaves the line.
    func discard(_ id: UUID, quietly: Bool = false, step: UUID = UUID()) {
        reclaim(id, makingRoom: false)
        if isInInbox(id), items.first(where: { $0.id == id })?.kept == false {
            trash(id, quietly: quietly, step: step)
        } else if let item = items.first(where: { $0.id == id && !$0.falling }), let index = liveIndex(of: id) {
            history.remove(item.url)
            drop(id, quietly: quietly)
            remember(TakenDown(url: item.url, index: index, trashed: nil, kept: item.kept, byHand: item.byHand), in: step)
        }
    }

    /// Moves along a line that holds more than fit, from a scroll gesture.
    func scroll(by delta: CGFloat) {
        let most = CGFloat(max(0, items.count - visibleCount)) * Layout.spacing
        let next = min(most, max(0, scroll + delta))
        if next != scroll { scroll = next }
    }

    /// After Keep on line is lowered, the oldest photos past it go the way
    /// of a full line.
    func trim() {
        while liveCount > maxItems, let oldest = oldestLoose {
            letGo(oldest.id)
        }
        scroll = 0
    }

    /// The oldest photo falling off a full line. Text and EnCopy's own pictures
    /// fall into the basket, file and row kept (T-030). Anything else from
    /// EnCopy's folder, a recording, goes to the Trash; files from elsewhere
    /// only leave the line. If the Trash refuses one, it still leaves the line.
    private func letGo(_ id: UUID) {
        if let url = items.first(where: { $0.id == id })?.url, isText(url) || isKeptImage(url) {
            drop(id, quietly: true)
            return
        }
        if isInInbox(id), trash(id, quietly: true, step: nil) { return }
        drop(id, quietly: true)
    }

    /// An exported screenshot keeps its macOS tag. Desktop watchers must
    /// not mistake that saved file for a new capture, even after its undo expires.
    /// Identify the file too, so a later namesake is still picked up.
    private struct DesktopSave: Equatable {
        let volume: UInt64
        let file: UInt64
        let created: Date?

        init?(_ url: URL) {
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
                  let volume = attributes[.systemNumber] as? NSNumber,
                  let file = attributes[.systemFileNumber] as? NSNumber else { return nil }
            self.volume = volume.uint64Value
            self.file = file.uint64Value
            created = attributes[.creationDate] as? Date
        }
    }

    private var desktopSaves: [URL: DesktopSave] = [:]

    func isDesktopSave(_ url: URL) -> Bool {
        let url = url.standardizedFileURL
        guard let saved = desktopSaves[url] else { return false }
        guard DesktopSave(url) == saved else {
            desktopSaves[url] = nil
            return false
        }
        return true
    }

    /// Move an inbox screenshot to the Desktop, or save another copy of a
    /// file already there. Every destination gets a name that is still free.
    func saveToDesktop(_ id: UUID, step: UUID = UUID()) {
        guard canSaveToDesktop(id),
              let item = items.first(where: { $0.id == id && !$0.falling }),
              let index = liveIndex(of: id) else { return }
        let copying = !isInInbox(id)
        let target = uniqueURL(in: desktop, for: item.url.lastPathComponent)
        do {
            if copying {
                try FileManager.default.copyItem(at: item.url, to: target)
            } else {
                try FileManager.default.moveItem(at: item.url, to: target)
                clipboardFollows(item.url, to: target)
                history.remove(item.url)
            }
            let identity = DesktopSave(target)
            desktopSaves[target.standardizedFileURL] = identity
            drop(id, quietly: true)
            remember(TakenDown(url: item.url, index: index, trashed: nil,
                               kept: item.kept, byHand: item.byHand,
                               moved: copying ? nil : target, copied: copying ? target : nil,
                               copyIdentity: copying ? identity : nil), in: step)
        } catch {
            log.error("Could not save to Desktop: \(error.localizedDescription, privacy: .public)")
            NSSound.beep()
        }
    }

    private func uniqueURL(in folder: URL, for name: String) -> URL {
        let base = (name as NSString).deletingPathExtension
        let ext = (name as NSString).pathExtension
        var candidate = folder.appendingPathComponent(name)
        var n = 2
        while FileManager.default.fileExists(atPath: candidate.path) {
            candidate = folder.appendingPathComponent("\(base) \(n)").appendingPathExtension(ext)
            n += 1
        }
        return candidate
    }

    /// Long press: open the photo in the system Markup editor. Markup does
    /// not edit video, so a recording opens in the trimming editor instead.
    /// What holding a card, or Markup, does (review #1). Only a picture is
    /// marked up, in place; a PDF, a folder, an app or any other file from
    /// Finder is shown in Quick Look, never rewritten or opened.
    enum EditAction { case open, trim, markup, quickLook }

    func editAction(for id: UUID) -> EditAction {
        guard let item = items.first(where: { $0.id == id }) else { return .quickLook }
        if isText(item.url) { return .open }
        if item.isRecording { return .trim }
        return isPicture(item.url) ? .markup : .quickLook
    }

    func canMarkUp(_ id: UUID) -> Bool { editAction(for: id) == .markup }

    /// A picture Markup can draw on and save as it was: not a PDF, whose
    /// pages it would flatten into one image.
    private func isPicture(_ url: URL) -> Bool {
        guard !isFile(url), let type = UTType(filenameExtension: url.pathExtension) else { return false }
        return type.conforms(to: .image) && !type.conforms(to: .pdf)
    }

    func markup(_ id: UUID) {
        guard let item = items.first(where: { $0.id == id }) else { return }
        switch editAction(for: id) {
        // Holding a note opens it to edit, as holding a photo marks it up.
        case .open: NSWorkspace.shared.open(item.url)
        case .trim: Trim.shared.edit(item.url, size: item.thumb.size)
        case .markup: Markup.shared.edit(item.url)
        case .quickLook: quickLook(id)
        }
    }

    /// After editing, the photo on the line shows the new version.
    func reloadThumbnail(for url: URL) {
        guard let i = items.firstIndex(where: { $0.url == url && !$0.falling }),
              let thumb = makeThumbnail(url) else { return }
        items[i].thumb = thumb
        keep(url)
        indexForSearch()
    }

    /// Quick Look on this photo, with the rest of the line a key press away.
    /// ⌘P while summoned: pinned goes loose, loose gets pinned.
    func togglePin(_ id: UUID) {
        // A basket card comes back onto the line first, then is pinned there.
        if isInBasket(id) { reclaim(id) }
        guard let item = items.first(where: { $0.id == id && !$0.falling }) else { return }
        if item.kept { unpin(id) } else { pin(id) }
    }

    func quickLook(_ id: UUID) {
        let live = items.filter { !$0.falling }
        guard let index = live.firstIndex(where: { $0.id == id }) else { return }
        QuickLook.shared.show(live.map(\.url), at: index)
    }

    /// A note edited in another app shows its new words, on the line and
    /// held in front of you.
    func refreshNotes() {
        for item in items where !item.falling && isText(item.url) {
            guard let cached = Note.peek(item.url),
                  let modified = Note.modificationDate(item.url), modified != cached.modified else { continue }
            reloadThumbnail(for: item.url)
            Held.reload(item.url)
        }
    }

    /// After a change of size, every photo is redrawn sharp at the new one.
    func reloadThumbnails() {
        for i in items.indices where !items[i].falling {
            if let thumb = makeThumbnail(items[i].url) { items[i].thumb = thumb }
        }
    }

    func reveal(_ id: UUID) {
        guard let item = items.first(where: { $0.id == id }) else { return }
        NSWorkspace.shared.activateFileViewerSelecting([item.url])
    }

    func reveal(_ ids: [UUID]) {
        let urls = actionItems(ids).map(\.url)
        guard !urls.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(urls)
    }

    // MARK: Putting back

    /// A photo before an undoable action: its file, position, pin state,
    /// and where the file went if it was moved.
    private struct TakenDown {
        var url: URL
        let index: Int
        let trashed: URL?
        var kept = false
        var byHand = false
        var moved: URL?
        var copied: URL?
        var copyIdentity: DesktopSave?
        var pinChange = false
    }

    /// What was taken down, the latest last. A step holds everything one
    /// action took down. Only kept while the app runs.
    private var steps: [(id: UUID, photos: [TakenDown])] = []
    private static let maxSteps = 20

    var canPutBack: Bool { !steps.isEmpty }
    /// Keeps undo reachable where a menu action just finished, even below
    /// the photo band or after the last photo has been taken down.
    var onUndoableAction: (() -> Void)?

    private func remember(_ photo: TakenDown, in step: UUID) {
        if let i = steps.lastIndex(where: { $0.id == step }) {
            steps[i].photos.append(photo)
        } else {
            steps.append((step, [photo]))
            if steps.count > Self.maxSteps { steps.removeFirst() }
            onUndoableAction?()
        }
    }

    /// Put back, or ⌘Z: restore the previous positions and pin states, and
    /// bring files back from the Trash or Desktop if they were moved.
    func putBack() {
        guard let step = steps.popLast() else { return }
        var back: Set<UUID> = []
        // The last one taken down goes back first, so each finds its place.
        for photo in step.photos.reversed() {
            if photo.pinChange {
                guard let index = items.firstIndex(where: { $0.url == photo.url && !$0.falling }) else { continue }
                var item = items.remove(at: index)
                item.kept = photo.kept
                insert(item, atLiveIndex: photo.index)
                back.insert(item.id)
                continue
            }
            guard let url = restore(photo) else { continue }
            // A saved file may have been hung manually meanwhile. Remove
            // its card only if undo actually moved that file away.
            if let saved = photo.moved ?? photo.copied, !FileManager.default.fileExists(atPath: saved.path) {
                for item in items where item.url.standardizedFileURL == saved.standardizedFileURL && !item.falling {
                    drop(item.id, quietly: true)
                }
            }
            if let existing = items.first(where: { $0.url.standardizedFileURL == url.standardizedFileURL && !$0.falling }) {
                back.insert(existing.id)
                continue
            }
            guard let thumb = makeThumbnail(url) else { continue }
            var item = Pegged(url: url, thumb: thumb)
            item.kept = photo.kept
            item.byHand = photo.byHand
            if let gap = items.firstIndex(where: { $0.falling && $0.url == photo.url }) {
                // Its empty place is still on the line: it hangs back there.
                items[gap] = item
            } else {
                insert(item, atLiveIndex: photo.index)
            }
            keep(url)
            back.insert(item.id)
        }
        guard !back.isEmpty else {
            NSSound.beep()
            return
        }
        // As when something new hangs, a full line lets its oldest photo go,
        // never one that just came back or one you are holding.
        while liveCount > maxItems, let oldest = items.first(where: { !$0.falling && !$0.kept && !$0.held && !back.contains($0.id) }) {
            letGo(oldest.id)
        }
        keepKeptFirst()
        save()
        play("Tink", volume: 0.35)
    }

    private func insert(_ item: Pegged, atLiveIndex index: Int) {
        let live = items.filter { !$0.falling }
        if index < live.count, let i = items.firstIndex(where: { $0.id == live[index].id }) {
            items.insert(item, at: i)
        } else {
            items.append(item)
        }
    }

    /// The photo's file, where it was before. Nil when it is gone: the Trash
    /// was emptied, or the file was moved or deleted since.
    private func restore(_ photo: TakenDown) -> URL? {
        if let copied = photo.copied {
            // The original never moved. Undo removes only the copy we
            // created, not a different file later using that destination.
            if let identity = photo.copyIdentity, DesktopSave(copied) == identity {
                do {
                    var trashed: NSURL?
                    try FileManager.default.trashItem(at: copied, resultingItemURL: &trashed)
                    if let trashed { clipboardFollows(copied, to: trashed as URL) }
                    desktopSaves[copied.standardizedFileURL] = nil
                } catch {
                    log.error("Could not undo Desktop copy: \(error.localizedDescription, privacy: .public)")
                    return nil
                }
            }
            return FileManager.default.fileExists(atPath: photo.url.path) ? photo.url : nil
        }
        guard let moved = photo.moved ?? photo.trashed else {
            return FileManager.default.fileExists(atPath: photo.url.path) ? photo.url : nil
        }
        let url = uniqueURL(in: photo.url.deletingLastPathComponent(), for: photo.url.lastPathComponent)
        do {
            try FileManager.default.moveItem(at: moved, to: url)
            desktopSaves[moved.standardizedFileURL] = nil
            clipboardFollows(moved, to: url)
            // A newer namesake may force a different filename. Earlier
            // actions, such as pinning this photo, must follow that name.
            if url != photo.url {
                for step in steps.indices {
                    for i in steps[step].photos.indices where steps[step].photos[i].url == photo.url {
                        steps[step].photos[i].url = url
                    }
                }
            }
            log.notice("Put back \(url.lastPathComponent, privacy: .public)")
            return url
        } catch {
            log.error("Could not put back \(moved.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }

    /// Where a photo hangs, counting only the photos still on the line.
    private func liveIndex(of id: UUID) -> Int? {
        items.filter { !$0.falling }.firstIndex { $0.id == id }
    }

    // MARK: Hanging by hand

    /// Kept photos hang left of the knot, the rest after it, each side in
    /// its own order.
    private func keepKeptFirst() {
        let kept = items.filter { $0.kept && !$0.falling }
        if items.prefix(kept.count).map(\.id) != kept.map(\.id) {
            items = kept + items.filter { !($0.kept && !$0.falling) }
        }
    }

    /// Pin: the photo joins the kept ones at the left end of the line.
    func pin(_ id: UUID, quietly: Bool = false, step: UUID = UUID()) {
        guard let i = items.firstIndex(where: { $0.id == id && !$0.falling && !$0.kept }),
              let index = liveIndex(of: id) else { return }
        var item = items.remove(at: i)
        let before = TakenDown(url: item.url, index: index, trashed: nil,
                               kept: item.kept, byHand: item.byHand, pinChange: true)
        item.kept = true
        items.insert(item, at: keptCount)
        // Pinned never expires, a code or an hourly app's copy included.
        history.setTTL(item.url, nil)
        save()
        remember(before, in: step)
        if !quietly { play("Tink", volume: 0.35) }
    }

    /// Unpin: it hangs loose again, first past the kept ones, so it is the
    /// next to fall from a full line.
    func unpin(_ id: UUID, step: UUID = UUID()) {
        guard let i = items.firstIndex(where: { $0.id == id && !$0.falling && $0.kept && !$0.byHand }),
              let index = liveIndex(of: id) else { return }
        var item = items.remove(at: i)
        let before = TakenDown(url: item.url, index: index, trashed: nil,
                               kept: item.kept, byHand: item.byHand, pinChange: true)
        item.kept = false
        items.insert(item, at: keptCount)
        save()
        remember(before, in: step)
    }

    /// An image hung from another app, with Hang on EnCopy. Like a
    /// photo from a watched folder, the line only shows the file where it
    /// is: taking it down leaves the file alone. What you hang yourself is
    /// pinned.
    @discardableResult
    func hangKept(_ url: URL) -> Bool {
        // Already on the line: it is pinned where it is.
        if let hanging = items.first(where: { $0.url.standardizedFileURL == url.standardizedFileURL && !$0.falling }) {
            pin(hanging.id)
            return true
        }
        guard let id = hang(url),
              let i = items.firstIndex(where: { $0.id == id }) else { return false }
        var item = items.remove(at: i)
        item.kept = true
        item.byHand = true
        items.insert(item, at: keptCount)
        save()
        return true
    }

    // MARK: Holding

    /// Off the line and into your hand, or back onto its clip.
    func setHeld(_ id: UUID, _ held: Bool) {
        guard let i = items.firstIndex(where: { $0.id == id && !$0.falling }) else { return }
        if held { selectedIDs.remove(id) }
        items[i].held = held
        if !held { items[i].flying = true }
    }

    // MARK: Breeze

    private var gustWork: DispatchWorkItem?

    /// Every so often a little wind moves the line. It is the detail that
    /// makes it feel like an object and not a widget. Tucked away, it rests:
    /// ordering the panel out does not stop SwiftUI's animation work.
    private func scheduleGust() {
        guard revealed else { return }
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.revealed else { return }
            self.gustWork = nil
            if !self.items.isEmpty && self.draggingID == nil
                && Motion.breezes(now: Date(), lastKey: self.lastKeyAt) { self.gust += 1 }
            self.scheduleGust()
        }
        gustWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + .random(in: Motion.breezeEvery), execute: work)
    }

    // MARK: Persistence

    private let keptKey = "kept"
    private let byHandKey = "byHand"

    private func save() {
        let live = items.filter { !$0.falling }
        search.update(live.map(\.url))
        UserDefaults.standard.set(live.map(\.url.path), forKey: storeKey)
        UserDefaults.standard.set(live.filter(\.kept).map(\.url.path), forKey: keptKey)
        UserDefaults.standard.set(live.filter(\.byHand).map(\.url.path), forKey: byHandKey)
        if isSearching { recomputeSearch() }
    }

    /// Everything comes back as it was. This runs before the line knows its
    /// screen, so the capacity is still the default one: hanging through
    /// `hang` would let photos fall off a wide screen's line at every launch.
    private func restore() {
        let paths = UserDefaults.standard.stringArray(forKey: storeKey) ?? []
        let kept = Set(UserDefaults.standard.stringArray(forKey: keptKey) ?? [])
        let byHand = Set(UserDefaults.standard.stringArray(forKey: byHandKey) ?? [])
        for path in paths where FileManager.default.fileExists(atPath: path) {
            let url = URL(fileURLWithPath: path)
            guard !items.contains(where: { $0.url == url }), let thumb = makeThumbnail(url) else { continue }
            var item = Pegged(url: url, thumb: thumb)
            item.byHand = byHand.contains(path)
            item.kept = kept.contains(path) || item.byHand
            items.append(item)
        }
        // Notes hung before the history existed get their rows here.
        for item in items { keep(item.url) }
        keepKeptFirst()
    }

    // MARK: Helpers

    /// Starting a sound takes the audio system a frame or two, so it starts
    /// off the main thread, where it would hold up the photo it goes with.
    private static let sounds = DispatchQueue(label: "app.encopy.sounds", qos: .userInitiated)

    private func play(_ name: String, volume: Float) {
        guard soundOn, let sound = NSSound(named: name)?.copy() as? NSSound else { return }
        sound.volume = volume
        Self.sounds.async { sound.play() }
    }

    private func pngData(_ url: URL) -> Data? {
        if url.pathExtension.lowercased() == "png" { return try? Data(contentsOf: url) }
        guard let tiff = NSImage(contentsOf: url)?.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }
}

/// Screen recordings, as macOS saves them.
func isRecording(_ url: URL) -> Bool {
    ["mov", "mp4"].contains(url.pathExtension.lowercased())
}

/// A file copied in Finder, hung where it is (T-029): anything that is not a
/// note, a recording or a picture, folders included.
func isFile(_ url: URL) -> Bool {
    guard !isText(url), !isRecording(url) else { return false }
    if url.hasDirectoryPath { return true }
    var isDir: ObjCBool = false
    if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue { return true }
    return !(UTType(filenameExtension: url.pathExtension)?.conforms(to: .image) ?? false)
}

/// Copied text: plain text is saved as .txt, styled text as .rtf.
func isText(_ url: URL) -> Bool {
    ["txt", "rtf", "md"].contains(url.pathExtension.lowercased()) && NoteFolder.contains(url)
}

/// Text copied in dark mode carries dark backgrounds and light ink, which look
/// wrong on a white page. Dark backgrounds are dropped, light grey or white ink
/// goes back to the default, and light coloured ink is darkened to stay
/// readable. Bold, italic, fonts, links and light highlights are kept.
func lightPageStyled(_ source: NSAttributedString) -> NSAttributedString {
    lightPageEdits(source).map { lightPage(source, $0) } ?? source
}

private typealias LightPageEdit = (NSRange, NSAttributedString.Key, NSColor?)

/// The colours to change for a white page, or nil when there are none.
private func lightPageEdits(_ m: NSAttributedString) -> [LightPageEdit]? {
    let full = NSRange(location: 0, length: m.length)
    var edits: [LightPageEdit] = []
    m.enumerateAttribute(.backgroundColor, in: full) { v, r, _ in
        guard let c = v as? NSColor else { return }
        if let rgb = c.usingColorSpace(.deviceRGB), rgb.alphaComponent > 0.05, rgb.brightnessComponent >= 0.5 { return }
        edits.append((r, .backgroundColor, nil))
    }
    m.enumerateAttribute(.foregroundColor, in: full) { v, r, _ in
        guard let c = v as? NSColor else { return }
        guard let rgb = c.usingColorSpace(.deviceRGB) else { edits.append((r, .foregroundColor, nil)); return }
        if rgb.brightnessComponent <= 0.6 { return }
        if rgb.saturationComponent < 0.25 {
            edits.append((r, .foregroundColor, nil))
        } else {
            edits.append((r, .foregroundColor, NSColor(deviceHue: rgb.hueComponent, saturation: min(1, rgb.saturationComponent + 0.2),
                                                       brightness: 0.55, alpha: 1)))
        }
    }
    return edits.isEmpty ? nil : edits
}

private func lightPage(_ source: NSAttributedString, _ edits: [LightPageEdit]) -> NSAttributedString {
    let m = NSMutableAttributedString(attributedString: source)
    for (r, key, color) in edits {
        if let color { m.addAttribute(key, value: color, range: r) } else { m.removeAttribute(key, range: r) }
    }
    return m
}

/// RTF bytes, cleaned as above. Bytes with nothing to clean are kept as they
/// came: reading RTF into the text system drops what it does not know, such
/// as footnotes from InDesign or Word.
func lightPageRTF(_ data: Data) -> Data {
    guard let a = NSAttributedString(rtf: data, documentAttributes: nil),
          let edits = lightPageEdits(a) else { return data }
    let clean = lightPage(a, edits)
    return (try? clean.data(from: NSRange(location: 0, length: clean.length),
                            documentAttributes: [.documentType: NSAttributedString.DocumentType.rtf])) ?? data
}

/// Marks a pasteboard written by the line itself, so copying a note off the
/// line does not hang it again.
let selfCopyType = NSPasteboard.PasteboardType("app.encopy.SelfCopy")

/// The default size covers a card at twice its size in points, as on a
/// Retina screen, and no more: every photo on the line keeps one in memory.
func makeThumbnail(_ url: URL, maxPixels: Int = Int(320 * Layout.size.scale), full: Bool = false) -> NSImage? {
    if isRecording(url) { return firstFrame(url, maxPixels: maxPixels) }
    if isText(url) { return MainActor.assumeIsolated { noteCard(url, maxPixels: maxPixels, full: full) } }
    if isFile(url) { return MainActor.assumeIsolated { fileCard(url, maxPixels: maxPixels) } }
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
    let options: [CFString: Any] = [
        kCGImageSourceCreateThumbnailFromImageAlways: true,
        kCGImageSourceCreateThumbnailWithTransform: true,
        kCGImageSourceThumbnailMaxPixelSize: maxPixels,
    ]
    guard let cg = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
    return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
}

/// A recording shows its first frame.
private func firstFrame(_ url: URL, maxPixels: Int) -> NSImage? {
    let generator = AVAssetImageGenerator(asset: AVURLAsset(url: url))
    generator.appliesPreferredTrackTransform = true
    generator.maximumSize = CGSize(width: maxPixels, height: maxPixels)
    guard let cg = try? generator.copyCGImage(at: .zero, actualTime: nil) else { return nil }
    return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
}

/// A copied file on the line: a manila folder with the file's icon, its name
/// and the folder it is in. Drawn like a note, in points times the scale.
@MainActor func fileCard(_ url: URL, maxPixels: Int) -> NSImage? {
    let width = Layout.cardWidth - 14
    let renderer = ImageRenderer(content: FileCard(url: url, width: width))
    renderer.proposedSize = ProposedViewSize(width: width, height: width * 0.78)
    renderer.scale = max(1, CGFloat(maxPixels) / width)
    guard let cg = renderer.cgImage else { return nil }
    return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
}

private struct FileCard: View {
    let url: URL
    let width: CGFloat

    var body: some View {
        let u = width / 136
        VStack(alignment: .leading, spacing: 4 * u) {
            HStack(alignment: .top, spacing: 6 * u) {
                Image(nsImage: NSWorkspace.shared.icon(forFile: url.path))
                    .resizable().frame(width: 30 * u, height: 30 * u)
                Text(url.lastPathComponent)
                    .font(.system(size: 10.5 * u, weight: .semibold))
                    .foregroundStyle(Color(red: 0.25, green: 0.2, blue: 0.1))
                    .lineLimit(3)
            }
            Spacer(minLength: 0)
            Text(url.deletingLastPathComponent().lastPathComponent)
                .font(.system(size: 8 * u, design: .monospaced))
                .foregroundStyle(Color(red: 0.42, green: 0.33, blue: 0.19))
                .lineLimit(1)
        }
        .padding(.horizontal, 9 * u)
        .padding(.top, 14 * u)
        .padding(.bottom, 7 * u)
        .frame(width: width, height: width * 0.78, alignment: .topLeading)
        .background(FolderShape(u: u).fill(Color(red: 0.93, green: 0.835, blue: 0.608)))
    }
}

/// A folder: a tab on the top left, the rest one sheet.
private struct FolderShape: Shape {
    let u: CGFloat
    func path(in rect: CGRect) -> Path {
        var p = Path()
        p.move(to: CGPoint(x: 0, y: 8 * u))
        p.addLine(to: CGPoint(x: 7 * u, y: 8 * u))
        p.addLine(to: CGPoint(x: 11 * u, y: 0))
        p.addLine(to: CGPoint(x: 46 * u, y: 0))
        p.addLine(to: CGPoint(x: 50 * u, y: 8 * u))
        p.addLine(to: CGPoint(x: rect.maxX, y: 8 * u))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY))
        p.addLine(to: CGPoint(x: 0, y: rect.maxY))
        p.closeSubpath()
        return p
    }
}
