import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Where copied text and links are kept: next to the screenshots folder, not
/// in it, so a note is known by where it lives and never mixed with captures.
enum NoteFolder {
    /// Fixed, so it stays put even if screenshots ever go somewhere else.
    static let url = DataFolder.url.appendingPathComponent("Notes", isDirectory: true)

    static func contains(_ url: URL) -> Bool {
        url.deletingLastPathComponent().standardizedFileURL.path == Self.url.standardizedFileURL.path
    }

    /// Whether a file from elsewhere, such as Finder, can hang as a note.
    static func isTextFile(_ url: URL) -> Bool {
        guard let type = UTType(filenameExtension: url.pathExtension) else { return false }
        return type.conforms(to: .plainText) || type.conforms(to: .rtf)
    }

    /// A text file hung from Finder is copied in, so it becomes a note and
    /// the original stays untouched. Plain text of any kind (Markdown,
    /// source code) is kept as .txt. Huge files are left alone.
    static func adopt(_ source: URL) -> URL? {
        if contains(source) { return source }
        let size = (try? FileManager.default.attributesOfItem(atPath: source.path))?[.size] as? Int ?? 0
        guard isTextFile(source), size <= 1_000_000 else { return nil }
        // Styled text stays RTF and Markdown stays Markdown; any other plain
        // text becomes .txt.
        let kind = source.pathExtension.lowercased()
        let ext = kind == "rtf" ? "rtf" : ["md", "markdown"].contains(kind) ? "md" : "txt"
        let base = source.deletingPathExtension().lastPathComponent
        let fm = FileManager.default
        var target = url.appendingPathComponent("\(base).\(ext)")
        var n = 2
        while fm.fileExists(atPath: target.path) {
            target = url.appendingPathComponent("\(base) \(n).\(ext)")
            n += 1
        }
        do {
            try fm.createDirectory(at: url, withIntermediateDirectories: true)
            try fm.copyItem(at: source, to: target)
            try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: target.path)
            return target
        } catch {
            return nil
        }
    }
}

/// What a copied text shows on the line: its first line as a title, the
/// rest as the body, or the whole of it as code, and when it was copied.
struct Note {
    var title: String
    var body: String
    var code: Bool
    var date: Date
    /// When its file last changed, to notice an edit made in another app.
    var modified: Date? = nil
    /// A copy that is just a web address hangs as a link: its site and path.
    var link: URL? = nil
    /// Written in Markdown: shown formatted, copied back as written.
    var markdown = false
    /// The app it was copied from, by name, when it is known.
    var app: String? = nil

    /// When it clears by itself ("Clears in an hour"), if it does.
    var clears: String? = nil

    /// Where a note's source app comes from; the line points it at its history.
    @MainActor static var sourceOf: ((URL) -> String?)?
    /// Where a note's own clearing time comes from, likewise.
    @MainActor static var clearsOf: ((URL) -> String?)?

    /// Read once per thumbnail and kept, so drawing the line never touches
    /// the disk.
    @MainActor private static var cache: [URL: Note] = [:]

    @MainActor static func cached(_ url: URL) -> Note? {
        if let note = cache[url] { return note }
        return reload(url)
    }

    /// What is cached, without reading the file.
    @MainActor static func peek(_ url: URL) -> Note? { cache[url] }

    /// Read from the file system each time: URL resource values are cached.
    static func modificationDate(_ url: URL) -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: url.path))?[.modificationDate] as? Date
    }

    @MainActor @discardableResult static func reload(_ url: URL) -> Note? {
        guard var note = read(url) else { return nil }
        note.modified = modificationDate(url)
        note.app = sourceOf?(url)
        note.clears = clearsOf?(url)
        cache[url] = note
        return note
    }

    private static func read(_ url: URL) -> Note? {
        var text: String
        var monospaced = false
        if url.pathExtension.lowercased() == "rtf" {
            guard let data = try? Data(contentsOf: url),
                  let rich = NSAttributedString(rtf: data, documentAttributes: nil) else { return nil }
            text = rich.string
            if rich.length > 0, let font = rich.attribute(.font, at: 0, effectiveRange: nil) as? NSFont {
                monospaced = font.isFixedPitch
            }
        } else {
            // Decoded whole, then cut: cutting the bytes first could split a
            // character in two and fail the whole note.
            guard let data = try? Data(contentsOf: url) else { return nil }
            text = String(decoding: data, as: UTF8.self)
        }
        text = String(text.prefix(4000))
        let date = (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? Date()
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if let link = webAddress(trimmed), let host = link.host {
            let site = host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
            return Note(title: site, body: trimmed, code: false, date: date, link: link)
        }
        // Markdown before code: a list can look like code at a glance.
        let kind = url.pathExtension.lowercased()
        if kind == "md" || (kind != "rtf" && !monospaced && Markdown.looks(trimmed)) {
            let lines = trimmed.components(separatedBy: .newlines)
            let first = lines.first ?? ""
            let rest = lines.dropFirst().joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
            // "# Shopping" is the title; a note that starts with a list, a
            // quote or code has none, and neither does a long first line.
            if let heading = Markdown.heading(first) {
                return Note(title: heading, body: rest, code: false, date: date, markdown: true)
            }
            if Markdown.startsBlock(first) || first.count > titleLimit {
                return Note(title: "", body: trimmed, code: false, date: date, markdown: true)
            }
            return Note(title: first, body: rest, code: false, date: date, markdown: true)
        }
        if monospaced || looksLikeCode(trimmed) {
            return Note(title: "", body: trimmed, code: true, date: date)
        }
        let lines = trimmed.components(separatedBy: .newlines)
        let title = lines.first?.trimmingCharacters(in: .whitespaces) ?? ""
        // A title is short. A long first line is the start of a paragraph,
        // as most copied text is: then it all reads as body.
        if title.count > titleLimit {
            return Note(title: "", body: trimmed, code: false, date: date)
        }
        let body = lines.dropFirst().joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
        return Note(title: title, body: body, code: false, date: date)
    }

    /// About two lines of a card in bold.
    private static let titleLimit = 60

    /// The whole copy is one web address, and nothing else.
    static func webAddress(_ text: String) -> URL? {
        guard !text.contains(where: \.isWhitespace),
              let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue),
              let match = detector.firstMatch(in: text, range: NSRange(location: 0, length: (text as NSString).length)),
              match.range.length == (text as NSString).length,
              let url = match.url, ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return nil }
        return url
    }

    /// What follows the site, for the line under it: "/reel/DfhgD7X". The
    /// query is left out: it is mostly tracking. Copying and opening keep it.
    var linkPath: String {
        guard let path = link?.path, path != "/" else { return "" }
        return path
    }

    /// Several lines that are indented, end like code or are full of the
    /// symbols prose rarely uses.
    private static func looksLikeCode(_ text: String) -> Bool {
        let lines = text.components(separatedBy: .newlines).filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        guard lines.count >= 2 else { return false }
        let codeLike = lines.filter { line in
            line.hasPrefix("  ") || line.hasPrefix("\t")
                || ["{", "}", ";", "\\", ")", "=>", "->"].contains { line.hasSuffix($0) }
        }
        let symbols = text.filter { "{}[]();=<>$#|\\".contains($0) }.count
        return codeLike.count * 2 >= lines.count || symbols * 12 >= text.count
    }

    /// "Today, 16:02", in the language the menus speak.
    var dateText: String {
        let f = DateFormatter()
        f.locale = Locale(identifier: Bundle.main.preferredLocalizations.first ?? "en")
        f.dateStyle = .short
        f.timeStyle = .short
        f.doesRelativeDateFormatting = true
        return f.string(from: date)
    }

    /// The date, and the app it came from when that is known.
    var caption: String { [dateText, app, clears].compactMap { $0 }.joined(separator: " · ") }
}

/// A note as paper (T-034): a till receipt with a torn bottom for text, a
/// tractor-feed printout for code, a kraft luggage tag for a link. The paper
/// stays paper in dark mode; only the line around it follows the system.
struct NoteSheet: View {
    let note: Note
    let width: CGFloat
    /// The height the line gives it; none to take what the text needs.
    var height: CGFloat? = nil
    /// Paper draws the receipt, printout and tag; any other theme its own card (T-041).
    var theme: Theme = .paper

    /// Everything is drawn for a card 136 points wide and scaled from there.
    private var u: CGFloat { width / 136 }
    private let dark = false
    private var card: Theme.Card { note.link != nil ? .link : note.code ? .code : .text }
    private var ink: Color { theme == .paper ? Color(red: 0.165, green: 0.145, blue: 0.122) : theme.ink(card) }
    private var sub: Color { theme == .paper ? Color(red: 0.463, green: 0.412, blue: 0.353) : theme.soft(card) }

    /// Paper's own type, or the theme's.
    private func font(_ size: CGFloat, bold: Bool = false, mono: Bool = true, code: Bool = false) -> Font {
        theme == .paper ? .system(size: size * u, weight: bold ? .bold : .regular, design: mono ? .monospaced : .default)
                        : theme.font(size * u, bold: bold, code: code)
    }

    enum Paper { case receipt, tractor, tag }
    private var paper: Paper { Self.paper(of: note) }
    static func paper(of note: Note) -> Paper { note.link != nil ? .tag : note.code ? .tractor : .receipt }

    var body: some View {
        VStack(alignment: .leading, spacing: 3 * u) {
            if note.link != nil {
                Text(note.title)
                    .font(font(11.5, bold: true, mono: false))
                    .foregroundStyle(theme == .paper ? Color(red: 0.25, green: 0.19, blue: 0.11) : ink)
                    .lineLimit(1)
                    .padding(.trailing, 14 * u)
                    .layoutPriority(1)
                if !note.linkPath.isEmpty {
                    Text(note.linkPath)
                        .font(font(8.5, code: true))
                        .foregroundStyle(theme == .paper ? Color(red: 0.36, green: 0.29, blue: 0.2) : sub)
                        .lineLimit(2)
                        .truncationMode(.middle)
                }
            } else if note.code {
                Text(note.body)
                    .font(font(7.5, code: true))
                    .foregroundStyle(ink)
                    .lineSpacing(5.5 * u)
                    // Code keeps its lines whole and is cut at the edge.
                    .fixedSize(horizontal: true, vertical: false)
                    .frame(width: width - 34 * u, alignment: .leading)
                    .clipped()
                    .layoutPriority(0)
            } else {
                if !note.title.isEmpty {
                    Text(note.markdown ? Markdown.inline(note.title) : AttributedString(note.title))
                        .font(font(note.body.isEmpty ? 11.5 : 10.5, bold: true))
                        .foregroundStyle(ink)
                        .lineLimit(3)
                        .layoutPriority(1)
                }
                if !note.body.isEmpty {
                    Dashes()
                        .stroke(sub.opacity(0.6), style: StrokeStyle(lineWidth: 0.8 * u, dash: [2.5 * u, 2 * u]))
                        .frame(height: 1 * u)
                    if note.markdown {
                        Text(Markdown.swiftUI(note.body, size: 8.5 * u, ink: ink, soft: sub,
                                              link: Color(red: 0.6, green: 0.36, blue: 0.08)))
                            .lineSpacing(1 * u)
                            .lineLimit(3)
                            .layoutPriority(0)
                    } else {
                        Text(linked(note.body))
                            .font(font(8.5))
                            .foregroundStyle(sub)
                            .lineSpacing(1 * u)
                            .lineLimit(3)
                            .layoutPriority(0)
                    }
                }
            }
            Spacer(minLength: 0)
            Text(note.caption)
                .font(font(7))
                .foregroundStyle(sub)
                .lineLimit(1)
                .layoutPriority(2)
        }
        .padding(.top, (paper == .tag ? 14 : 9) * u)
        .padding(.horizontal, (paper == .tractor ? 15 : 9) * u)
        .padding(.bottom, (paper == .receipt ? 11 : 7) * u)
        .frame(width: width, height: height, alignment: .top)
        .background(themedBackground)
        .clipShape(themedShape)
    }

    @ViewBuilder private var themedBackground: some View {
        if theme == .paper { background } else { ThemeCardBackground(theme: theme, card: card) }
    }

    private var themedShape: AnyShape {
        theme == .paper ? AnyShape(PaperShape(paper: paper, u: u))
                        : AnyShape(RoundedRectangle(cornerRadius: theme.corner, style: .continuous))
    }

    @ViewBuilder private var background: some View {
        switch paper {
        case .receipt:
            Color(red: 0.984, green: 0.98, blue: 0.957)
        case .tag:
            ZStack(alignment: .topTrailing) {
                Color(red: 0.831, green: 0.69, blue: 0.525)
                Circle().fill(Color(red: 0.945, green: 0.882, blue: 0.765)).frame(width: 9 * u, height: 9 * u)
                    .overlay(Circle().fill(Color(red: 0.24, green: 0.16, blue: 0.08).opacity(0.5)).frame(width: 4.5 * u, height: 4.5 * u))
                    .padding(.top, 6 * u).padding(.trailing, 8 * u)
            }
        case .tractor:
            ZStack {
                Canvas { context, size in
                    var y: CGFloat = 0
                    var light = true
                    while y < size.height {
                        context.fill(Path(CGRect(x: 0, y: y, width: size.width, height: 13 * u)),
                                     with: .color(light ? Color(red: 0.96, green: 0.973, blue: 0.94) : Color(red: 0.882, green: 0.929, blue: 0.839)))
                        y += 13 * u
                        light.toggle()
                    }
                    for x in [4.5 * u, size.width - 4.5 * u] {
                        var hy: CGFloat = 6.5 * u
                        while hy < size.height {
                            context.fill(Path(ellipseIn: CGRect(x: x - 1.8 * u, y: hy - 1.8 * u, width: 3.6 * u, height: 3.6 * u)),
                                         with: .color(Color(red: 0.35, green: 0.31, blue: 0.24).opacity(0.38)))
                            hy += 13 * u
                        }
                    }
                }
            }
        }
    }

    /// Links in the yellow Notes uses for them.
    private func linked(_ text: String) -> AttributedString {
        var result = AttributedString(text)
        guard let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue) else { return result }
        let ns = text as NSString
        for match in detector.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
            guard let range = Range(match.range, in: text),
                  let lower = AttributedString.Index(range.lowerBound, within: result),
                  let upper = AttributedString.Index(range.upperBound, within: result) else { continue }
            result[lower..<upper].foregroundColor = dark ? Color(red: 0.9, green: 0.706, blue: 0.133) : Color(red: 0.788, green: 0.604, blue: 0)
            result[lower..<upper].underlineStyle = .single
        }
        return result
    }

    private struct Dashes: Shape {
        func path(in rect: CGRect) -> Path {
            var p = Path()
            p.move(to: CGPoint(x: rect.minX, y: rect.midY))
            p.addLine(to: CGPoint(x: rect.maxX, y: rect.midY))
            return p
        }
    }
}

/// The outline of each paper: a receipt's torn bottom, a tag's cut corners,
/// a printout's plain sheet.
struct PaperShape: Shape {
    let paper: NoteSheet.Paper
    let u: CGFloat

    func path(in rect: CGRect) -> Path {
        var p = Path()
        switch paper {
        case .tractor:
            p.addRect(rect)
        case .tag:
            let c = 11 * u
            p.move(to: CGPoint(x: c, y: 0))
            p.addLine(to: CGPoint(x: rect.maxX - c, y: 0))
            p.addLine(to: CGPoint(x: rect.maxX, y: c))
            p.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY))
            p.addLine(to: CGPoint(x: 0, y: rect.maxY))
            p.addLine(to: CGPoint(x: 0, y: c))
            p.closeSubpath()
        case .receipt:
            let tooth = 4.5 * u
            p.move(to: .zero)
            p.addLine(to: CGPoint(x: rect.maxX, y: 0))
            p.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY - tooth))
            var x = rect.maxX
            var down = true
            while x > 0 {
                x -= tooth
                p.addLine(to: CGPoint(x: max(0, x), y: down ? rect.maxY : rect.maxY - tooth))
                down.toggle()
            }
            p.addLine(to: CGPoint(x: 0, y: rect.maxY - tooth))
            p.closeSubpath()
        }
        return p
    }
}

/// The note as a picture, measured in pixels like a photo's thumbnail. For
/// the line and the flight it has the card's shape; held in front of you it
/// is a bigger sheet with the whole text.
@MainActor func noteCard(_ url: URL, maxPixels: Int, full: Bool = false) -> NSImage? {
    guard let note = Note.reload(url) else { return nil }
    let width: CGFloat = full ? 260 : Layout.cardWidth - 14
    let cap: CGFloat = full ? 700 : Layout.photoMaxHeight
    let dark = NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
    let scheme: ColorScheme = dark ? .dark : .light
    let natural = ImageRenderer(content: NoteSheet(note: note, width: width).environment(\.colorScheme, scheme))
    natural.proposedSize = ProposedViewSize(width: width, height: nil)
    let height = min(cap, max(width * 0.42, natural.nsImage?.size.height ?? cap))
    let renderer = ImageRenderer(content: NoteSheet(note: note, width: width, height: height).environment(\.colorScheme, scheme))
    renderer.scale = full ? 2 : max(1, CGFloat(maxPixels) / width)
    guard let cg = renderer.cgImage else { return nil }
    return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
}

// MARK: Held in front of you

/// How big a held note's words are. Stretching the window reflows the
/// text at this size; zooming changes it.
final class NoteScale: ObservableObject {
    /// The sheet is drawn for 260 points wide when held.
    static let natural: CGFloat = 260 / 136
    @Published var u: CGFloat = NoteScale.natural
}

/// A note held in front of you: the same sheet, with real text you can
/// select, copy and scroll, and links you can click. Stretch it to any
/// shape and the text reflows; zoom it and everything grows.
struct HeldNote: View {
    let note: Note
    @ObservedObject var scale: NoteScale
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        GeometryReader { geo in
            let u = scale.u
            let dark = scheme == .dark
            VStack(alignment: .leading, spacing: 0) {
                LinearGradient(colors: [Color(red: 1, green: 0.886, blue: 0.49), Color(red: 0.973, green: 0.788, blue: 0.235)],
                               startPoint: .top, endPoint: .bottom)
                    .frame(height: 9 * u)
                Path { p in
                    p.move(to: CGPoint(x: 0, y: 0.65 * u))
                    p.addLine(to: CGPoint(x: geo.size.width - 12 * u, y: 0.65 * u))
                }
                .stroke(dark ? Color(white: 0.42) : Color(red: 0.78, green: 0.76, blue: 0.71),
                        style: StrokeStyle(lineWidth: 1.3 * u, lineCap: .round, dash: [0, 3.4 * u]))
                .frame(height: 1.3 * u)
                .padding(.horizontal, 6 * u)
                .padding(.top, 2.5 * u)
                Group {
                    if note.code {
                        NoteText(note: note, u: u, dark: dark)
                            .padding(5 * u)
                            .background(RoundedRectangle(cornerRadius: 4 * u, style: .continuous)
                                .fill(dark ? Color(white: 0.17) : Color(red: 0.95, green: 0.95, blue: 0.97)))
                    } else {
                        NoteText(note: note, u: u, dark: dark)
                    }
                }
                .padding(.top, 5 * u)
                .padding(.horizontal, 9 * u)
                Text(note.caption)
                    .font(.system(size: 7.5 * u))
                    .foregroundStyle(Color(red: 0.557, green: 0.557, blue: 0.576))
                    .lineLimit(1)
                    .padding(.horizontal, 9 * u)
                    .padding(.vertical, 5 * u)
            }
            .frame(width: geo.size.width, height: geo.size.height, alignment: .top)
            .background(dark ? Color(white: 0.118) : Color.white)
        }
    }
}

/// The note's words in a read-only text view: selectable, scrollable when
/// long, code kept on whole lines and scrolled sideways.
private struct NoteText: NSViewRepresentable {
    let note: Note
    let u: CGFloat
    let dark: Bool

    final class Coordinator {
        var drawn: (CGFloat, Bool, String)?
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.scrollerStyle = .overlay
        guard let text = scroll.documentView as? NSTextView else { return scroll }
        text.isEditable = false
        text.isSelectable = true
        text.drawsBackground = false
        text.textContainerInset = .zero
        text.textContainer?.lineFragmentPadding = 0
        if note.code {
            scroll.hasHorizontalScroller = true
            text.isHorizontallyResizable = true
            text.textContainer?.widthTracksTextView = false
            text.textContainer?.containerSize = NSSize(width: CGFloat.greatestFiniteMagnitude,
                                                       height: CGFloat.greatestFiniteMagnitude)
        }
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let text = scroll.documentView as? NSTextView else { return }
        let words = note.title + "\n" + note.body
        if let drawn = context.coordinator.drawn, abs(drawn.0 - u) < 0.01, drawn.1 == dark, drawn.2 == words { return }
        context.coordinator.drawn = (u, dark, words)
        let link = dark ? NSColor(red: 0.9, green: 0.706, blue: 0.133, alpha: 1) : NSColor(red: 0.788, green: 0.604, blue: 0, alpha: 1)
        text.linkTextAttributes = [.foregroundColor: link, .underlineStyle: NSUnderlineStyle.single.rawValue,
                                   .cursor: NSCursor.pointingHand]
        text.textStorage?.setAttributedString(attributed(link: link))
    }

    private func attributed(link: NSColor) -> NSAttributedString {
        let ink = dark ? NSColor(white: 0.96, alpha: 1) : NSColor(white: 0.11, alpha: 1)
        if note.code {
            let style = NSMutableParagraphStyle()
            style.lineSpacing = 1.2 * u
            return NSAttributedString(string: note.body, attributes: [
                .font: NSFont.monospacedSystemFont(ofSize: 7.5 * u, weight: .regular),
                .foregroundColor: ink, .paragraphStyle: style])
        }
        let title = note.markdown ? String(Markdown.inline(note.title).characters) : note.title
        let result = NSMutableAttributedString(string: title, attributes: [
            .font: NSFont.systemFont(ofSize: (note.body.isEmpty ? 12.5 : 11) * u, weight: .bold),
            .foregroundColor: ink])
        guard !note.body.isEmpty else { return result }
        if note.markdown {
            if !title.isEmpty { result.append(NSAttributedString(string: "\n")) }
            result.append(Markdown.appKit(note.body, size: (title.isEmpty ? 9.5 : 8.5) * u, ink: ink,
                                          soft: dark ? NSColor(white: 1, alpha: 0.75) : NSColor(white: 0.24, alpha: 1),
                                          link: link, lineSpacing: 1 * u))
            return result
        }
        let style = NSMutableParagraphStyle()
        style.lineSpacing = 1 * u
        style.paragraphSpacingBefore = note.title.isEmpty ? 0 : 3 * u
        let lead = note.title.isEmpty ? "" : "\n"
        let start = result.length + lead.utf16.count
        result.append(NSAttributedString(string: lead + note.body, attributes: [
            .font: NSFont.systemFont(ofSize: (note.title.isEmpty ? 9.5 : 8.5) * u),
            .foregroundColor: dark ? NSColor(white: 1, alpha: 0.75) : NSColor(white: 0.24, alpha: 1),
            .paragraphStyle: style]))
        if let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue) {
            let body = note.body as NSString
            for match in detector.matches(in: note.body, range: NSRange(location: 0, length: body.length)) {
                guard let url = match.url else { continue }
                result.addAttribute(.link, value: url, range: NSRange(location: start + match.range.location, length: match.range.length))
            }
        }
        return result
    }
}

// MARK: Edited elsewhere

/// Notices when a note's file is saved by another app, such as TextEdit
/// after a double click. Editors that save by replacing the file change the
/// folder, which is what this watches; the line also checks when it comes
/// down, for editors that write in place.
@MainActor final class NoteWatcher {
    private var source: DispatchSourceFileSystemObject?
    private var pending: DispatchWorkItem?
    private let onChange: () -> Void

    init(onChange: @escaping () -> Void) { self.onChange = onChange }

    func start() {
        guard source == nil else { return }
        try? FileManager.default.createDirectory(at: NoteFolder.url, withIntermediateDirectories: true)
        let fd = open(NoteFolder.url.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let src = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write, .rename, .delete], queue: .main)
        src.setEventHandler { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.pending?.cancel()
                let work = DispatchWorkItem { [weak self] in self?.onChange() }
                self.pending = work
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: work)
            }
        }
        src.setCancelHandler { close(fd) }
        src.resume()
        source = src
    }
}
