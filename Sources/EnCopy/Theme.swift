import AppKit
import SwiftUI

/// How the line looks (T-041): the paper of T-034, and five more. A theme
/// changes the cards' paper, edge and shadow, the pegs, the rope and the
/// picked card's edge; what is on a card, where it hangs and what clicking
/// does stay the same.
enum Theme: String, CaseIterable, Identifiable {
    case paper, pixel, skeuo, minimal, neon, washi

    var id: String { rawValue }

    /// The chosen theme; paper unless another was picked.
    nonisolated static var current: Theme {
        get { UserDefaults.standard.string(forKey: "theme").flatMap(Theme.init(rawValue:)) ?? .paper }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: "theme") }
    }

    var title: String {
        switch self {
        case .paper: L("Paper")
        case .pixel: L("Pixel")
        case .skeuo: L("Skeuomorphic")
        case .minimal: L("Minimal")
        case .neon: L("Neon")
        case .washi: L("Journal")
        }
    }

    // MARK: Cards

    /// What a card shows, for its paper.
    enum Card { case text, code, link, image, file }

    func fill(_ card: Card) -> Color {
        switch self {
        case .paper: Color(red: 0.984, green: 0.98, blue: 0.957)
        case .pixel:
            switch card {
            case .code: Color(red: 0.11, green: 0.15, blue: 0.22)
            case .link: Color(red: 0.55, green: 0.82, blue: 0.98)
            case .file: Color(red: 0.99, green: 0.80, blue: 0.36)
            default: Color(red: 0.98, green: 0.97, blue: 0.91)
            }
        case .skeuo:
            switch card {
            case .link: Color(red: 0.93, green: 0.87, blue: 0.74)
            case .code: Color(red: 0.93, green: 0.95, blue: 0.90)
            default: Color(red: 0.99, green: 0.98, blue: 0.94)
            }
        case .minimal: Color.white
        case .neon: Color(red: 0.06, green: 0.06, blue: 0.12).opacity(0.92)
        case .washi:
            switch card {
            case .link: Color(red: 0.96, green: 0.90, blue: 0.86)
            case .code: Color(red: 0.92, green: 0.94, blue: 0.96)
            default: Color(red: 0.98, green: 0.96, blue: 0.90)
            }
        }
    }

    func ink(_ card: Card) -> Color {
        switch self {
        case .pixel where card == .code: Color(red: 0.55, green: 0.98, blue: 0.62)
        case .neon: card == .link ? Color(red: 1.0, green: 0.45, blue: 0.85) : Color(red: 0.86, green: 0.97, blue: 1.0)
        case .minimal: Color(white: 0.12)
        default: Color(red: 0.165, green: 0.145, blue: 0.122)
        }
    }

    func soft(_ card: Card) -> Color {
        switch self {
        case .pixel where card == .code: Color(red: 0.45, green: 0.75, blue: 0.5)
        case .neon: Color(red: 0.55, green: 0.75, blue: 0.85)
        case .minimal: Color(white: 0.5)
        default: Color(red: 0.463, green: 0.412, blue: 0.353)
        }
    }

    var corner: CGFloat {
        switch self {
        case .paper, .skeuo: 2
        case .pixel: 0
        case .minimal: 10
        case .neon: 8
        case .washi: 3
        }
    }

    /// The edge drawn around a card: colour and width, or none.
    func edge(_ card: Card) -> (Color, CGFloat)? {
        switch self {
        case .paper, .skeuo, .washi: nil
        case .pixel: (Color.black, 2)
        case .minimal: (Color(white: 0.86), 0.7)
        case .neon: (card == .link ? Color(red: 1.0, green: 0.3, blue: 0.8) : Color(red: 0.2, green: 0.95, blue: 1.0), 1.3)
        }
    }

    /// The card's shadow: colour, blur, offset.
    func shadow(hovering: Bool) -> (Color, CGFloat, CGFloat, CGFloat) {
        switch self {
        case .paper: (.black.opacity(hovering ? 0.26 : 0.18), hovering ? 14 : 10, 0, hovering ? 8 : 5)
        case .pixel: (.black.opacity(0.85), 0, 4, hovering ? 5 : 4)
        case .skeuo: (.black.opacity(hovering ? 0.42 : 0.34), hovering ? 16 : 12, 0, hovering ? 10 : 7)
        case .minimal: (.black.opacity(hovering ? 0.14 : 0.08), hovering ? 12 : 8, 0, hovering ? 5 : 3)
        case .neon: (Color(red: 0.2, green: 0.95, blue: 1.0).opacity(hovering ? 0.45 : 0.28), hovering ? 8 : 5, 0, 0)
        case .washi: (.black.opacity(hovering ? 0.2 : 0.13), hovering ? 10 : 6, 0, hovering ? 5 : 3)
        }
    }

    /// The picked card's edge.
    var picked: Color {
        switch self {
        case .paper, .skeuo: Color(red: 0.827, green: 0.541, blue: 0.133)
        case .pixel: Color(red: 1.0, green: 0.83, blue: 0.0)
        case .minimal: Color.accentColor
        case .neon: Color(red: 1.0, green: 0.3, blue: 0.8)
        case .washi: Color(red: 0.93, green: 0.45, blue: 0.4)
        }
    }

    var pickedWidth: CGFloat { self == .pixel ? 4 : 2.5 }

    /// Words on a card: each theme its own type.
    func font(_ size: CGFloat, bold: Bool = false, code: Bool = false) -> Font {
        switch self {
        case .paper: .system(size: size, weight: bold ? .bold : .regular, design: .monospaced)
        case .pixel: .system(size: size, weight: .heavy, design: .monospaced)
        case .skeuo: code ? .system(size: size, design: .monospaced) : .system(size: size + 0.5, weight: bold ? .semibold : .regular, design: .serif)
        case .minimal: code ? .system(size: size, design: .monospaced) : .system(size: size + 0.5, weight: bold ? .semibold : .regular)
        case .neon: code ? .system(size: size, design: .monospaced) : .system(size: size + 0.5, weight: bold ? .semibold : .medium, design: .rounded)
        case .washi: code ? .system(size: size, design: .monospaced) : .system(size: size + 0.5, weight: bold ? .semibold : .regular, design: .serif)
        }
    }
}

/// A card's paper in a theme other than paper: fill, pattern, edge.
struct ThemeCardBackground: View {
    let theme: Theme
    let card: Theme.Card

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: theme.corner, style: .continuous)
        ZStack {
            shape.fill(theme.fill(card))
            switch theme {
            case .skeuo:
                // Light from above: a sheen at the top, a faint curl at the foot.
                shape.fill(LinearGradient(colors: [.white.opacity(0.55), .clear, .black.opacity(0.07)],
                                          startPoint: .top, endPoint: .bottom))
            case .washi:
                // Dot-grid journal paper.
                Canvas { context, size in
                    var y: CGFloat = 8
                    while y < size.height {
                        var x: CGFloat = 8
                        while x < size.width {
                            context.fill(Path(ellipseIn: CGRect(x: x - 0.7, y: y - 0.7, width: 1.4, height: 1.4)),
                                         with: .color(Color(red: 0.55, green: 0.5, blue: 0.45).opacity(0.35)))
                            x += 9
                        }
                        y += 9
                    }
                }
            case .pixel:
                // A light band along the top edge, as on an old game's window.
                VStack(spacing: 0) {
                    Rectangle().fill(.white.opacity(card == .code ? 0.08 : 0.5)).frame(height: 3)
                    Spacer(minLength: 0)
                }
            default:
                EmptyView()
            }
            if let (color, width) = theme.edge(card) {
                shape.strokeBorder(color, lineWidth: width)
            }
        }
    }
}

/// The peg in each theme. Paper keeps its wooden clothespin.
struct ThemePeg: View {
    let theme: Theme
    var pinned = false

    var body: some View {
        switch theme {
        case .paper:
            Clothespin(pinned: pinned)
        case .pixel:
            // Blocks: a jaw of two columns and a band across.
            let fill = pinned ? Color(white: 0.3) : Color(red: 0.86, green: 0.36, blue: 0.24)
            VStack(spacing: 0) {
                HStack(spacing: 2) {
                    Rectangle().fill(fill).frame(width: 4, height: 14)
                    Rectangle().fill(fill).frame(width: 4, height: 14)
                }
                Rectangle().fill(Color.black).frame(width: 12, height: 3)
                HStack(spacing: 2) {
                    Rectangle().fill(fill).frame(width: 4, height: 8)
                    Rectangle().fill(fill).frame(width: 4, height: 8)
                }
            }
            .overlay(Rectangle().strokeBorder(Color.black, lineWidth: 1))
            .frame(width: 12, height: 28)
        case .skeuo:
            // A metal binder clip: a dark jaw and two silver arms.
            VStack(spacing: -2) {
                HStack(spacing: 6) {
                    Capsule().stroke(Color(white: 0.75), lineWidth: 1.6).frame(width: 4, height: 12)
                    Capsule().stroke(Color(white: 0.75), lineWidth: 1.6).frame(width: 4, height: 12)
                }
                RoundedRectangle(cornerRadius: 2, style: .continuous)
                    .fill(LinearGradient(colors: pinned ? [Color(white: 0.35), Color(white: 0.15)]
                                                        : [Color(white: 0.28), Color(white: 0.05)],
                                         startPoint: .top, endPoint: .bottom))
                    .overlay(Rectangle().fill(.white.opacity(0.25)).frame(height: 1), alignment: .top)
                    .frame(width: 22, height: 12)
                    .shadow(color: .black.opacity(0.35), radius: 1.5, y: 1)
            }
            .frame(width: 24, height: 28)
        case .minimal:
            Circle()
                .fill(pinned ? Color(white: 0.25) : Color(white: 0.72))
                .overlay(Circle().fill(.white.opacity(0.6)).frame(width: 2.5, height: 2.5).offset(x: -1, y: -1))
                .frame(width: 8, height: 8)
                .shadow(color: .black.opacity(0.2), radius: 1, y: 0.5)
                .frame(width: 12, height: 22, alignment: .bottom)
        case .neon:
            Circle()
                .fill(pinned ? Color(red: 1.0, green: 0.3, blue: 0.8) : Color(red: 0.2, green: 0.95, blue: 1.0))
                .frame(width: 7, height: 7)
                .shadow(color: pinned ? Color(red: 1.0, green: 0.3, blue: 0.8) : Color(red: 0.2, green: 0.95, blue: 1.0), radius: 5)
                .frame(width: 12, height: 22, alignment: .bottom)
        case .washi:
            // A strip of striped paper tape, laid on a little crooked.
            WashiTape(pinned: pinned)
                .frame(width: 40, height: 13)
                .rotationEffect(.degrees(-6))
                .frame(width: 44, height: 22, alignment: .bottom)
        }
    }
}

private struct WashiTape: View {
    let pinned: Bool
    var body: some View {
        let base = pinned ? Color(white: 0.45) : Color(red: 0.94, green: 0.6, blue: 0.55)
        Canvas { context, size in
            context.fill(Path(CGRect(origin: .zero, size: size)), with: .color(base.opacity(0.78)))
            var x: CGFloat = -size.height
            while x < size.width {
                var stripe = Path()
                stripe.move(to: CGPoint(x: x, y: size.height))
                stripe.addLine(to: CGPoint(x: x + size.height, y: 0))
                context.stroke(stripe, with: .color(.white.opacity(0.45)), lineWidth: 2.2)
                x += 7
            }
        }
        .mask(TornEnds())
    }

    /// Torn ends, as tape is torn off the roll.
    private struct TornEnds: Shape {
        func path(in rect: CGRect) -> Path {
            var p = Path()
            let teeth = 4, h = rect.height / CGFloat(teeth)
            p.move(to: CGPoint(x: 2, y: 0))
            p.addLine(to: CGPoint(x: rect.maxX - 2, y: 0))
            for i in 0..<teeth {
                p.addLine(to: CGPoint(x: rect.maxX - (i % 2 == 0 ? 0 : 3), y: h * CGFloat(i) + h / 2))
            }
            p.addLine(to: CGPoint(x: rect.maxX - 2, y: rect.maxY))
            p.addLine(to: CGPoint(x: 2, y: rect.maxY))
            for i in (0..<teeth).reversed() {
                p.addLine(to: CGPoint(x: i % 2 == 0 ? 0 : 3, y: h * CGFloat(i) + h / 2))
            }
            p.closeSubpath()
            return p
        }
    }
}

/// The rope in each theme. Paper keeps its two-tone twine.
struct ThemeRopeStroke: View {
    let theme: Theme
    let curve: RopeCurve
    let scheme: ColorScheme

    var body: some View {
        switch theme {
        case .paper:
            EmptyView()
        case .pixel:
            curve.stroke(Color.black, style: StrokeStyle(lineWidth: 3, lineCap: .butt, dash: [6, 3]))
        case .skeuo:
            ZStack {
                curve.stroke(Color.black.opacity(0.3), lineWidth: 4.5).offset(y: 2).blur(radius: 2)
                curve.stroke(Color(red: 0.55, green: 0.4, blue: 0.22), lineWidth: 4)
                curve.stroke(Color(red: 0.85, green: 0.7, blue: 0.45), style: StrokeStyle(lineWidth: 4, dash: [3, 4]))
                curve.stroke(.white.opacity(0.35), lineWidth: 1).offset(y: -1)
            }
        case .minimal:
            curve.stroke(scheme == .dark ? Color(white: 0.6) : Color(white: 0.55), lineWidth: 1)
        case .neon:
            ZStack {
                curve.stroke(Color(red: 0.2, green: 0.95, blue: 1.0).opacity(0.6), lineWidth: 5).blur(radius: 4)
                curve.stroke(Color(red: 0.75, green: 1.0, blue: 1.0), lineWidth: 1.4)
            }
        case .washi:
            ZStack {
                curve.stroke(Color(red: 0.85, green: 0.25, blue: 0.25), lineWidth: 1.6)
                curve.stroke(.white, style: StrokeStyle(lineWidth: 1.6, dash: [4, 4]))
            }
        }
    }
}

/// A file's card in a theme other than paper: its Finder icon and name.
struct ThemeFileCard: View {
    let theme: Theme
    let url: URL
    let size: CGSize

    var body: some View {
        VStack(spacing: 6) {
            Image(nsImage: FileIcons.icon(for: url))
                .resizable()
                .interpolation(.high)
                .frame(width: 34, height: 34)
            Text(FileManager.default.displayName(atPath: url.path))
                .font(theme.font(theme == .pixel ? 8 : 9, bold: true))
                .foregroundStyle(theme.ink(.file))
                .lineSpacing(2)
                // Pixel's heavy type runs one line, smaller if it must.
                .lineLimit(theme == .pixel ? 1 : 2)
                .minimumScaleFactor(0.7)
                .multilineTextAlignment(.center)
            Text(url.deletingLastPathComponent().lastPathComponent)
                .font(theme.font(7))
                .foregroundStyle(theme.soft(.file))
                .lineLimit(1)
        }
        .padding(8)
        .frame(width: size.width, height: size.height)
        .background(ThemeCardBackground(theme: theme, card: .file))
    }
}

/// Finder's icon for each file, looked up once each.
@MainActor
enum FileIcons {
    private static let cache = NSCache<NSString, NSImage>()
    static func icon(for url: URL) -> NSImage {
        let key = url.path as NSString
        if let icon = cache.object(forKey: key) { return icon }
        let icon = NSWorkspace.shared.icon(forFile: url.path)
        cache.setObject(icon, forKey: key)
        return icon
    }
}
