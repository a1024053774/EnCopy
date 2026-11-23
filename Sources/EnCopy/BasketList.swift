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
                                Text(group.rows.count == 1 ? L("1 item") : String(format: L("%d items"), group.rows.count)).monospacedDigit()
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

/// One row (T-045): a strip of the paper it would hang on, in the line's
/// theme — a receipt, tractor-feed code, a link's tag, a polaroid, a folder —
/// with its first words, the app it came from, and when it was last used.
private struct BasketRowView: View {
    let row: Line.BasketRow
    @ObservedObject var line: Line
    let picked: Bool

    var body: some View {
        let card = line.paperCard(of: row.url)
        let theme = line.theme
        let summary = line.basketSummary(row.url)
        HStack(spacing: 10) {
            // What it is, first and the same width on every row (T-047).
            KindTag(tag: line.tag(of: row.url))
                .frame(width: 50, alignment: .leading)
            leading(card)
            VStack(alignment: .leading, spacing: 2) {
                Text(summary.title)
                    .font(theme.font(12.5, bold: card != .code, code: card == .code || card == .link))
                    .foregroundStyle(ink(card))
                    .lineLimit(1)
                if let detail = summary.detail {
                    Text(detail)
                        .font(theme.font(11, code: card != .text))
                        .foregroundStyle(soft(card))
                        .lineLimit(1)
                }
            }
            .truncationMode(.tail)
            Spacer(minLength: 8)
            // The app and the time in columns of their own, so rows line up.
            Group {
                if Line.showsSourceApp, let app = row.app, let icon = AppIcons.icon(for: app) {
                    Image(nsImage: icon).resizable().interpolation(.high)
                } else {
                    Color.clear
                }
            }
            .frame(width: 16, height: 16)
            Text(Self.time(row.used))
                .font(theme.font(10.5, code: true).monospacedDigit())
                .foregroundStyle(soft(card))
                .frame(width: 74, alignment: .trailing)
        }
        .padding(.leading, card == .code ? 18 : 12).padding(.trailing, card == .code ? 18 : 12)
        .padding(.vertical, 7)
        .frame(minHeight: 46)
        .background(PaperStrip(theme: theme, card: card))
        .overlay {
            if picked {
                RoundedRectangle(cornerRadius: max(3, theme.corner), style: .continuous)
                    .strokeBorder(theme.picked, lineWidth: theme.pickedWidth + 1)
            }
        }
        // The pick is pulled a little out of the pile, with a deeper shadow;
        // the rest sit back. A receipt's own amber edge alone would look the
        // same as the pick's edge (seen on the walk).
        .shadow(color: .black.opacity(picked ? 0.35 : 0), radius: picked ? 6 : 0, y: picked ? 3 : 0)
        .offset(x: picked ? 5 : 0)
        .opacity(picked ? 1 : 0.86)
        .animation(Motion.reduced ? nil : .spring(response: 0.22, dampingFraction: 0.8), value: picked)
        .padding(.horizontal, 10).padding(.vertical, 3)
    }

    /// A picture shows a small polaroid of itself; a file its icon.
    @ViewBuilder private func leading(_ card: Theme.Card) -> some View {
        switch card {
        case .image:
            if let thumb = Self.thumbnail(row.url) {
                Image(nsImage: thumb).resizable().aspectRatio(contentMode: .fill)
                    .frame(width: 44, height: 30).clipped()
                    .padding(3).padding(.bottom, 5)
                    .background(Color.white)
                    .shadow(color: .black.opacity(0.18), radius: 1.5, y: 1)
            }
        case .file:
            Image(nsImage: NSWorkspace.shared.icon(forFile: row.url.path)).resizable().frame(width: 28, height: 28)
        default:
            EmptyView()
        }
    }

    private func ink(_ card: Theme.Card) -> Color {
        line.theme == .paper ? Color(red: 0.11, green: 0.12, blue: 0.13) : line.theme.ink(card)
    }

    private func soft(_ card: Theme.Card) -> Color {
        line.theme == .paper ? Color(red: 0.42, green: 0.40, blue: 0.36) : line.theme.soft(card)
    }

    private static let thumbs = NSCache<NSURL, NSImage>()

    static func thumbnail(_ url: URL) -> NSImage? {
        if let known = thumbs.object(forKey: url as NSURL) { return known }
        guard let made = makeThumbnail(url, maxPixels: 120) else { return nil }
        thumbs.setObject(made, forKey: url as NSURL)
        return made
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

/// The strip of paper behind a row. In the paper theme each kind has its own
/// paper, as on the line; the other themes draw their own card background.
private struct PaperStrip: View {
    let theme: Theme
    let card: Theme.Card

    var body: some View {
        if theme == .paper {
            let shape = RoundedRectangle(cornerRadius: 3, style: .continuous)
            ZStack {
                switch card {
                case .text:
                    shape.fill(Color.white)
                    // A faint receipt edge: the amber one is the pick's.
                    shape.strokeBorder(Color(red: 0.88, green: 0.54, blue: 0.12).opacity(0.35), lineWidth: 0.8)
                case .code:
                    shape.fill(Color(red: 0.93, green: 0.96, blue: 0.89))
                    // Tractor-feed holes down both edges.
                    HStack {
                        holes
                        Spacer()
                        holes
                    }
                    .padding(.horizontal, 5)
                case .link:
                    TagShape().fill(Color(red: 0.84, green: 0.69, blue: 0.52))
                case .image:
                    shape.fill(Color.white)
                case .file:
                    shape.fill(Color(red: 0.91, green: 0.77, blue: 0.56))
                }
            }
            .shadow(color: .black.opacity(0.14), radius: 2, y: 1)
        } else {
            ThemeCardBackground(theme: theme, card: card)
                .shadow(color: theme.shadow(hovering: false).0.opacity(0.6), radius: 2, y: 1)
        }
    }

    private var holes: some View {
        VStack(spacing: 5) {
            ForEach(0..<4, id: \.self) { _ in Circle().fill(Color.black.opacity(0.18)).frame(width: 4, height: 4) }
        }
    }
}

/// A kraft tag: the left corners clipped, like the link cards on the line.
private struct TagShape: Shape {
    func path(in r: CGRect) -> Path {
        let c: CGFloat = 9
        var p = Path()
        p.move(to: CGPoint(x: r.minX + c, y: r.minY))
        p.addLine(to: CGPoint(x: r.maxX - 3, y: r.minY))
        p.addLine(to: CGPoint(x: r.maxX, y: r.minY + 3))
        p.addLine(to: CGPoint(x: r.maxX, y: r.maxY - 3))
        p.addLine(to: CGPoint(x: r.maxX - 3, y: r.maxY))
        p.addLine(to: CGPoint(x: r.minX + c, y: r.maxY))
        p.addLine(to: CGPoint(x: r.minX, y: r.maxY - c))
        p.addLine(to: CGPoint(x: r.minX, y: r.minY + c))
        p.closeSubpath()
        return p
    }
}

/// What something is, as a small coloured tag (T-047): on each card on the
/// line and on each row of the basket list. One colour per kind, the same in
/// every theme, so the kinds read apart at a glance.
struct KindTag: View {
    let tag: Line.Tag

    var body: some View {
        HStack(spacing: 3) {
            Image(systemName: Self.symbol(tag)).font(.system(size: 8, weight: .bold))
            Text(Self.label(tag)).font(.system(size: 9.5, weight: .semibold))
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 5).padding(.vertical, 2)
        .background(Capsule().fill(Self.color(tag)))
        .fixedSize()
    }

    static func label(_ tag: Line.Tag) -> String {
        switch tag {
        case .text: L("Text")
        case .code: L("Code")
        case .link: L("Link")
        case .image: L("Image")
        case .video: L("Video")
        case .file: L("File")
        }
    }

    static func symbol(_ tag: Line.Tag) -> String {
        switch tag {
        case .text: "text.alignleft"
        case .code: "chevron.left.forwardslash.chevron.right"
        case .link: "link"
        case .image: "photo"
        case .video: "video.fill"
        case .file: "doc.fill"
        }
    }

    static func color(_ tag: Line.Tag) -> Color {
        switch tag {
        case .text: Color(red: 0.86, green: 0.52, blue: 0.10)
        case .code: Color(red: 0.18, green: 0.58, blue: 0.34)
        case .link: Color(red: 0.16, green: 0.44, blue: 0.86)
        case .image: Color(red: 0.56, green: 0.34, blue: 0.86)
        case .video: Color(red: 0.86, green: 0.28, blue: 0.30)
        case .file: Color(red: 0.52, green: 0.38, blue: 0.24)
        }
    }
}

/// The chips above the basket list (T-047): all, text, links, images, each
/// with how many the list holds. A click narrows the list, as Tab does.
/// Files copied in Finder never fall into the basket, so they have none.
struct BasketKindChips: View {
    @ObservedObject var line: Line

    var body: some View {
        HStack(spacing: 6) {
            chip(nil, L("All"), line.basketMatchCount, color: .secondary)
            chip(.text, L("Text"), line.basketKindCounts[.text] ?? 0, color: KindTag.color(.text))
            chip(.link, L("Links"), line.basketKindCounts[.link] ?? 0, color: KindTag.color(.link))
            chip(.image, L("Images"), line.basketKindCounts[.image] ?? 0, color: KindTag.color(.image))
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
    }

    private func chip(_ kind: Line.Kind?, _ title: String, _ count: Int, color: Color) -> some View {
        let on = line.kindFilter == kind
        return Button(action: { line.kindFilter = kind }) {
            HStack(spacing: 5) {
                if kind != nil { Circle().fill(color).frame(width: 6, height: 6) }
                Text(title).font(.system(size: 11.5, weight: .semibold))
                Text("\(count)").font(.system(size: 11, weight: .medium).monospacedDigit())
                    .foregroundStyle(on ? Color.white.opacity(0.85) : Color.secondary)
            }
            .foregroundStyle(on ? Color.white : Color.primary)
            .padding(.horizontal, 9).padding(.vertical, 4)
            .background(Capsule().fill(on ? Color.accentColor : Color.primary.opacity(0.08)))
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .opacity(count == 0 && !on ? 0.45 : 1)
    }
}
