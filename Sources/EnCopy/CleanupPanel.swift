import AppKit
import SwiftUI

/// 清理历史 (T-005): pick a batch by kind, age, app and words, see it, clear
/// it into 「最近清掉」; put a recent batch back; set how much the originals
/// may take, and see what the next daily run will do.
@MainActor
final class CleanupPanel {
    static let shared = CleanupPanel()
    private var window: NSWindow?
    private var model: CleanupModel?

    func show(line: Line) {
        if window == nil {
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 640),
                             styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
            w.title = L("Clean Up History")
            w.isReleasedWhenClosed = false
            let model = CleanupModel(line: line)
            self.model = model
            w.contentView = NSHostingView(rootView: CleanupView(model: model))
            w.center()
            window = w
        }
        // Opened again: list what is true now, not what was (review #13).
        model?.refresh()
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

@MainActor
final class CleanupModel: ObservableObject {
    let line: Line
    @Published var filter = CleanupFilter(olderThanDays: 30) {
        didSet {
            // A changed filter is a new question: no confirming the old one
            // (review 2 #4). Typing waits for a pause before the list is
            // worked out again.
            confirming = false
            pendingRefresh?.cancel()
            guard filter.text != oldValue.text else { refresh(); return }
            let work = DispatchWorkItem { [weak self] in self?.refresh() }
            pendingRefresh = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: work)
        }
    }
    private var pendingRefresh: DispatchWorkItem?
    @Published private(set) var candidates: [URL] = []
    @Published private(set) var batches: [Clearing.Batch] = []
    @Published private(set) var plan = Retention.Result()
    @Published var cap = Line.originalsCap { didSet { Line.originalsCap = cap; refresh() } }
    @Published var confirming = false

    init(line: Line) {
        self.line = line
        refresh()
    }

    var apps: [(id: String, name: String)] {
        Set(line.history.kept().compactMap(\.app)).map { ($0, History.appName($0) ?? $0) }.sorted { $0.name < $1.name }
    }

    func refresh() {
        candidates = line.cleanupCandidates(filter)
        batches = line.clearing.batches()
        plan = Retention.plan(history: line.history, onLine: line.onLinePaths, isKeptImage: line.isKeptImage, cap: cap)
        confirming = false
    }

    func clear() {
        // As the filter is now, not as the list last showed it.
        pendingRefresh?.cancel()
        line.cleanUp(line.cleanupCandidates(filter))
        refresh()
    }

    func putBack(_ id: String) {
        line.putBack(batch: id)
        refresh()
    }

    func title(_ url: URL) -> String {
        if isText(url), let note = Note.cached(url) { return note.title.isEmpty ? note.body : note.title }
        return url.lastPathComponent
    }
}

private struct CleanupView: View {
    @ObservedObject var model: CleanupModel

    private let ages: [(Double?, String)] = [(nil, L("Any time")), (1, L("Over a day ago")), (7, L("Over a week ago")),
                                            (30, L("Over a month ago")), (90, L("Over three months ago")), (365, L("Over a year ago"))]
    private let caps: [(Int64, String)] = [(512 << 20, "512 MB"), (1 << 30, "1 GB"), (2 << 30, "2 GB"), (5 << 30, "5 GB")]

    var body: some View {
        Form {
            Section(L("Clean up history")) {
                Picker(L("Kind"), selection: $model.filter.kind) {
                    Text(L("Everything")).tag(Line.Kind?.none)
                    Text(L("Text")).tag(Line.Kind?.some(.text))
                    Text(L("Links")).tag(Line.Kind?.some(.link))
                    Text(L("Images")).tag(Line.Kind?.some(.image))
                    Text(L("Files")).tag(Line.Kind?.some(.file))
                }
                Picker(L("Last used"), selection: $model.filter.olderThanDays) {
                    ForEach(ages, id: \.1) { Text($0.1).tag($0.0) }
                }
                Picker(L("Copied from"), selection: $model.filter.app) {
                    Text(L("Any app")).tag(String?.none)
                    ForEach(model.apps, id: \.id) { Text($0.name).tag(String?.some($0.id)) }
                }
                TextField(L("Containing"), text: $model.filter.text)
                Text(String(format: L("%d items will go to Recently Cleared, where they stay 7 days."), model.candidates.count))
                    .foregroundStyle(.secondary)
                ForEach(model.candidates.prefix(6), id: \.self) { url in
                    Text(model.title(url)).lineLimit(1).font(.callout)
                }
                HStack {
                    Spacer()
                    if model.confirming {
                        Button(L("Cancel")) { model.confirming = false }
                        Button(String(format: L("Clear %d"), model.candidates.count), role: .destructive) { model.clear() }
                            .keyboardShortcut(.defaultAction)
                    } else {
                        Button(String(format: L("Clear %d…"), model.candidates.count)) { model.confirming = true }
                            .disabled(model.candidates.isEmpty)
                    }
                }
            }
            Section(L("Recently Cleared")) {
                if model.batches.isEmpty {
                    Text(L("Nothing cleared in the last 7 days.")).foregroundStyle(.secondary)
                }
                ForEach(model.batches, id: \.id) { batch in
                    HStack {
                        VStack(alignment: .leading) {
                            Text(batch.date.formatted(date: .abbreviated, time: .shortened))
                            Text(String(format: batch.reason == "expired" ? L("%d expired") : L("%d cleared by you"), batch.entries.count))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button(L("Put Back")) { model.putBack(batch.id) }
                    }
                }
            }
            Section(L("Keeping")) {
                Text(L("Text stays a year after its last use. Screenshot originals shrink to thumbnails after 30 days; their text can still be found. What hangs on the line never expires."))
                    .font(.callout).foregroundStyle(.secondary)
                Picker(L("Originals may use up to"), selection: $model.cap) {
                    ForEach(caps, id: \.0) { Text($0.1).tag($0.0) }
                }
                Text(String(format: L("Next run: %d expire, %d originals shrink."), model.plan.expired.count, model.plan.shrunk.count))
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 460, minHeight: 520)
        .onAppear { model.refresh() }
    }
}
