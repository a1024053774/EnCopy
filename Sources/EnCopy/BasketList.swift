import SwiftUI

/// The handle at the end of the line (T-044): how many things the basket
/// holds, and ↓ to open it as a list. Without it nothing on screen says the
/// basket is there (T-043).
struct BasketHandle: View {
    @ObservedObject var line: Line

    var body: some View {
        Button(action: { line.onOpenBasket() }) {
            HStack(spacing: 6) {
                Image(systemName: "basket")
                    .font(.system(size: 12, weight: .semibold))
                Text(String(format: L("Basket %d"), line.basketCount))
                    .font(.system(size: 12, weight: .semibold).monospacedDigit())
                Text(line.basketOpen ? "↑" : "↓")
                    .font(.system(size: 11, weight: .bold, design: .monospaced))
                    .padding(.horizontal, 4).padding(.vertical, 1)
                    .background(RoundedRectangle(cornerRadius: 4).fill(Color.primary.opacity(0.12)))
            }
            .padding(.horizontal, 12).padding(.vertical, 6)
            .glassFrame(capsule: true)
        }
        .buttonStyle(.plain)
        .help(L("Open the basket"))
        .background(
            GeometryReader { g in
                Color.clear.preference(key: HitRectsKey.self, value: [Line.basketHandleID: g.frame(in: .global)])
            }
        )
    }
}

/// The whole basket as a list under the search field (T-044): newest first,
/// in groups by day and then by month, all the way back.
struct BasketListView: View {
    @ObservedObject var line: Line

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                    ForEach(line.basketGroups) { group in
                        Section {
                            ForEach(group.rows) { row in
                                BasketRowView(row: row, line: line, picked: row.url == line.basketPick)
                                    .id(row.url)
                                    .contentShape(Rectangle())
                                    .onTapGesture { line.pickBasket(row.url) }
                            }
                        } header: {
                            HStack {
                                Text(group.title)
                                Spacer()
                                Text(String(format: L("%d items"), group.rows.count)).monospacedDigit()
                            }
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(.secondary)
                            .padding(.horizontal, 14).padding(.vertical, 5)
                            .background(.regularMaterial)
                        }
                    }
                    if line.basketGroups.isEmpty {
                        Text(line.isSearching ? L("Nothing in the basket matches") : L("The basket is empty"))
                            .font(.system(size: 13))
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 30)
                    }
                }
            }
            .onChange(of: line.basketPick) { _, url in
                guard let url else { return }
                proxy.scrollTo(url, anchor: nil)
            }
        }
    }
}

/// One row: what it is, its first line, and when it was last used.
private struct BasketRowView: View {
    let row: Line.BasketRow
    @ObservedObject var line: Line
    let picked: Bool

    var body: some View {
        HStack(spacing: 10) {
            Text(kindLabel)
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(.secondary)
                .frame(width: 34, alignment: .leading)
            Text(summary)
                .font(.system(size: 13))
                .lineLimit(1)
                .truncationMode(.tail)
            Spacer(minLength: 8)
            Text(Self.time(row.used))
                .font(.system(size: 11).monospacedDigit())
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14).padding(.vertical, 7)
        .background(picked ? Color.accentColor.opacity(0.18) : Color.clear)
    }

    private var kindLabel: String {
        switch line.kind(of: row.url) {
        case .text: L("Text")
        case .link: L("Link")
        case .image: L("Image")
        case .file: L("File")
        }
    }

    /// A note's first line; a picture's name and the start of the words read
    /// from it; a file's name.
    private var summary: String {
        if isText(row.url), let note = Note.cached(row.url) {
            let first = note.title.isEmpty ? note.body : note.title
            return first.split(whereSeparator: \.isNewline).first.map(String.init) ?? first
        }
        if !isFile(row.url), let words = line.history.words(of: row.url), !words.isEmpty {
            return row.url.deletingPathExtension().lastPathComponent + " · " + words.prefix(80)
        }
        return row.url.lastPathComponent
    }

    static func time(_ date: Date) -> String {
        let f = Calendar.current.isDate(date, equalTo: Date(), toGranularity: .weekOfYear) ? timeFormatter : dateFormatter
        return f.string(from: date)
    }

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: Bundle.main.preferredLocalizations.first ?? "en")
        f.setLocalizedDateFormatFromTemplate("EEE HH:mm")
        return f
    }()

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: Bundle.main.preferredLocalizations.first ?? "en")
        f.setLocalizedDateFormatFromTemplate("MMMd")
        return f
    }()
}
