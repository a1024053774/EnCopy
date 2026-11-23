import SwiftUI

/// One photo with its clothespin. All the charm lives here: it drops onto
/// the line, swings, sways with the breeze and falls when you pull it off.
struct PeggedView: View {
    let item: Pegged
    @ObservedObject var line: Line
    /// In a gust sweeping the line from the left, how long this paper waits.
    var sweepDelay: Double = 0

    @State private var swing: Double = 0
    @State private var hop: CGFloat = 0
    @State private var pegOpen = false
    @State private var arrived = false
    @State private var hovering = false

    private var copied: Bool { line.copiedIDs.contains(item.id) }
    private var selected: Bool { line.selectedIDs.contains(item.id) || line.picked?.id == item.id }
    private var dragging: Bool { line.draggingID == item.id }

    private var cornerSymbol: String {
        switch line.corner(of: item.id) {
        case .trash: "xmark"
        case .hide: "eye.slash"
        case .unpin: "pin.slash"
        }
    }
    private var pressed: Bool { line.pressedID == item.id }

    var body: some View {
        VStack(spacing: -12) {
            ThemePeg(theme: line.theme, pinned: item.kept)
                // A new paper: the peg opens, then clamps (T-038).
                .scaleEffect(x: pegOpen ? Motion.pegOpen : 1, y: 1, anchor: .top)
                .zIndex(1)
            // Held in your hand: a faded trace of it waits on its clip.
            card
                .saturation(item.held ? 0 : 1)
                .opacity(item.held ? 0.3 : 1)
                .animation(.easeOut(duration: 0.25), value: item.held)
        }
        .rotationEffect(.degrees(swing + item.tilt), anchor: .top)
        .offset(y: (arrived || Motion.reduced ? 0 : -46) - hop)
        // The fall itself is drawn over the whole screen by CaptureFlight, so
        // the card here just steps aside at once.
        .opacity(item.falling || item.flying ? 0 : (arrived ? 1 : 0))
        .transaction { t in if item.falling { t.animation = nil } }
        .animation(.easeOut(duration: 0.16), value: item.flying)
        .onAppear(perform: arrive)
        .onChange(of: item.flying) { was, now in if was && !now { land() } }
        .onChange(of: line.gust) { _, _ in breeze() }
        .onChange(of: line.landing) { _, _ in sway() }
        .onChange(of: line.slide) { _, slide in nudge(slide.degrees) }
        .onChange(of: copied) { _, isCopied in if isCopied { jump() } }
    }

    /// The photo fits inside the card area keeping its proportions, so the
    /// white border hugs it whether the screenshot is wide or tall.
    static func photoSize(for size: CGSize) -> CGSize {
        let maxW = Layout.cardWidth - 14, maxH = Layout.photoMaxHeight
        guard size.width > 0, size.height > 0 else { return CGSize(width: maxW, height: maxH) }
        let scale = min(maxW / size.width, maxH / size.height)
        return CGSize(width: size.width * scale, height: size.height * scale)
    }

    /// The card around the photo: the photo plus the glass inset.
    static func cardSize(for size: CGSize) -> CGSize {
        let p = photoSize(for: size)
        return CGSize(width: p.width + Frame.inset * 2, height: p.height + Frame.inset * 2)
    }

    /// Distance from the top of the hanging view (the clip) to the card.
    static let cardOffsetBelowTop: CGFloat = 26 - 12

    private var photoSize: CGSize { Self.photoSize(for: item.thumb.size) }

    /// A photo shows its picture. Copied text is drawn live as a sheet from
    /// Notes, so it follows the light or dark appearance.
    private var cardSize: CGSize { Self.cardSize(for: item.thumb.size) }

    /// Each kind on its own paper (T-034): notes draw their receipt, printout
    /// or tag; a file is its folder; a picture sits in a polaroid. All fill
    /// the same card size, so the line's layout and flights are unchanged.
    @ViewBuilder private var paper: some View {
        if isText(item.url), let note = Note.cached(item.url) {
            NoteSheet(note: note, width: cardSize.width, height: cardSize.height, theme: line.theme)
        } else if isFile(item.url), line.theme != .paper {
            ThemeFileCard(theme: line.theme, url: item.url, size: cardSize)
        } else if isFile(item.url) {
            Image(nsImage: item.thumb).resizable().interpolation(.high)
                .frame(width: cardSize.width, height: cardSize.height)
        } else {
            VStack(spacing: 0) {
                Image(nsImage: item.thumb)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fill)
                    .frame(width: cardSize.width - 10, height: max(10, cardSize.height - 24))
                    .clipped()
                    .overlay {
                        // A recording says so with a play button over its first frame.
                        if item.isRecording {
                            Image(systemName: "play.fill")
                                .font(.system(size: 11, weight: .bold))
                                .foregroundStyle(.primary)
                                .frame(width: 28, height: 28)
                                .glassFrame(circle: true)
                        }
                    }
                    .padding(.top, 5)
                Spacer(minLength: 0)
                Text(polaroidCaption)
                    .font(line.theme == .paper ? .system(size: 7.5, design: .monospaced) : line.theme.font(7.5))
                    .foregroundStyle(line.theme == .paper ? Color(red: 0.463, green: 0.412, blue: 0.353) : line.theme.soft(.image))
                    .lineLimit(1)
                    .padding(.bottom, 5)
            }
            .frame(width: cardSize.width, height: cardSize.height)
            .background {
                if line.theme == .paper { Color(red: 0.992, green: 0.992, blue: 0.98) }
                else { ThemeCardBackground(theme: line.theme, card: .image) }
            }
            .clipShape(RoundedRectangle(cornerRadius: line.theme == .paper ? 2 : line.theme.corner, style: .continuous))
        }
    }

    /// Under a polaroid: when it was taken or copied.
    /// The date is read from disk once per picture, and one formatter serves
    /// every card: a redraw during a scroll or a breeze touches neither (review #15).
    private var polaroidCaption: String {
        let key = item.url.path as NSString
        let date: Date
        if let known = Self.created.object(forKey: key) {
            date = known as Date
        } else {
            date = (try? item.url.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? Date()
            Self.created.setObject(date as NSDate, forKey: key)
        }
        return Self.captionFormatter.string(from: date)
    }

    private static let created = NSCache<NSString, NSDate>()
    private static let captionFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: Bundle.main.preferredLocalizations.first ?? "en")
        f.dateStyle = .short
        f.timeStyle = .short
        f.doesRelativeDateFormatting = true
        return f
    }()

    private var card: some View {
        paper
            // Where it was copied from, in the corner (T-042).
            .overlay(alignment: .bottomTrailing) {
                if Line.showsSourceApp, let app = line.sourceApp(of: item.url), let icon = AppIcons.icon(for: app) {
                    Image(nsImage: icon)
                        .resizable()
                        .interpolation(.high)
                        .frame(width: 16, height: 16)
                        .shadow(color: .black.opacity(0.25), radius: 1, y: 0.5)
                        .padding(4)
                        .allowsHitTesting(false)
                }
            }
            .overlay {
                // Picked or selected: an amber edge, and the paper sinks and grows a little.
                if selected {
                    RoundedRectangle(cornerRadius: max(3, line.theme.corner), style: .continuous)
                        .strokeBorder(line.theme.picked, lineWidth: line.theme.pickedWidth)
                        .allowsHitTesting(false)
                }
            }
            // With Reduce motion only the edge changes.
            .scaleEffect(selected && line.picked?.id == item.id && !Motion.reduced ? 1.06 : 1, anchor: .top)
            .offset(y: selected && line.picked?.id == item.id && !Motion.reduced ? 4 : 0)
            .animation(Motion.pick, value: selected)
            // One shadow for the whole card, not one per word on it.
            .compositingGroup()
            .shadow(color: line.theme.shadow(hovering: hovering).0, radius: line.theme.shadow(hovering: hovering).1,
                    x: line.theme.shadow(hovering: hovering).2, y: line.theme.shadow(hovering: hovering).3)
            // Holding presses the photo in slowly, so a long press feels like
            // it is building up to something.
            .scaleEffect(pressed ? 0.95 : (hovering ? 1.035 : 1), anchor: .top)
            .animation(pressed ? .easeInOut(duration: 0.45) : .spring(response: 0.3, dampingFraction: 0.6), value: pressed)
            .opacity(dragging ? 0.45 : 1)
            .overlay(alignment: .topLeading) {
                // Drawn here, clicked through GrabView, which sits on top. A
                // cross sends a file in EnCopy's folder to the Trash; for a
                // file anywhere else, or one hung by hand, it only hides it
                // from the line. A pinned photo is unpinned instead.
                Image(systemName: cornerSymbol)
                    .font(.system(size: 8, weight: .bold))
                    .foregroundStyle(.primary)
                    .frame(width: 20, height: 20)
                    .glassFrame(circle: true)
                    .padding(3)
                    .opacity(hovering && !dragging && !item.held ? 1 : 0)
                    .scaleEffect(hovering ? 1 : 0.6)
                    .allowsHitTesting(false)
            }
            .overlay(GrabArea(item: item, line: line))
            .overlay(alignment: .bottom) {
                if copied {
                    Label(L("Copied"), systemImage: "checkmark")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.primary)
                        // Wider than a narrow card rather than broken over several lines.
                        .fixedSize()
                        .padding(.horizontal, 10)
                        .padding(.vertical, 5)
                        .glassFrame(capsule: true)
                        .offset(y: 16)
                        .transition(.opacity.combined(with: .offset(y: -4)))
                }
            }
            .animation(.easeOut(duration: 0.18), value: hovering)
            .animation(.easeOut(duration: 0.2), value: copied)
            .onHover { hovering = $0 }
            .background(
                GeometryReader { g in
                    Color.clear.preference(key: HitRectsKey.self,
                                           value: item.falling ? [:] : [item.id: g.frame(in: .global)])
                }
            )
    }

    /// How heavy this paper hangs, for how it swings.
    private var paperKind: Motion.Paper {
        if isText(item.url), let note = Note.cached(item.url) {
            switch NoteSheet.paper(of: note) {
            case .receipt: return .receipt
            case .tag: return .tag
            case .tractor: return .tractor
            }
        }
        return isFile(item.url) ? .folder : .polaroid
    }

    private func arrive() {
        // A capture that flew in is already in place; the flight did the arriving.
        if item.flying || Motion.reduced {
            withAnimation(Motion.reduced ? Motion.fade : nil) { arrived = true }
            return
        }
        swing = 16
        pegOpen = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { withAnimation(Motion.pegClamp) { pegOpen = false } }
        withAnimation(.spring(response: 0.42, dampingFraction: 0.72)) { arrived = true }
        withAnimation(Motion.swing(paperKind)) { swing = 0 }
    }

    /// Landing after the flight: no jump, just a small sway from rest.
    private func land() {
        nudge(2.2)
    }

    private func breeze() {
        let delay = Double.random(in: 0...0.35)
        let reach = Motion.gust(summoned: line.picked != nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
            guard line.revealed else { return }
            nudge(Double.random(in: reach))
        }
    }

    /// The line just came down: with ⌥C each paper sways a little; sliding
    /// down, a gust sweeps along from the left.
    private func sway() {
        if line.cameBySummon {
            nudge(Double.random(in: Motion.summonSway))
        } else {
            DispatchQueue.main.asyncAfter(deadline: .now() + sweepDelay) {
                nudge(Double.random(in: Motion.sweepSway))
            }
        }
    }

    /// A single click copied it: the paper hops up and drops back.
    private func jump() {
        guard !Motion.reduced else { return }
        withAnimation(.easeOut(duration: 0.08)) { hop = Motion.hopHeight }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) {
            withAnimation(Motion.hop) { hop = 0 }
        }
    }

    private func nudge(_ degrees: Double) {
        guard line.revealed, !Motion.reduced else { return }
        withAnimation(.easeOut(duration: 0.3)) { swing = degrees }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            withAnimation(Motion.swing(paperKind)) { swing = 0 }
        }
    }
}

enum Frame {
    static let radius: CGFloat = 16
    static let inset: CGFloat = 4
}

extension View {
    /// A crisp glass: the system's blurred material with a thin specular
    /// edge, lit from above. No refraction, so the background stays sharp
    /// around the frame instead of bending like gel.
    func glassFrame(cornerRadius: CGFloat = 0, circle: Bool = false, capsule: Bool = false) -> some View {
        let shape: AnyShape = circle ? AnyShape(Circle())
            : capsule ? AnyShape(Capsule())
            : AnyShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
        return background(.ultraThinMaterial, in: shape)
            .overlay(
                shape.stroke(
                    LinearGradient(colors: [Color.white.opacity(0.55), Color.white.opacity(0.12)],
                                   startPoint: .top, endPoint: .bottom),
                    lineWidth: 0.75)
            )
            .overlay(shape.stroke(Color.black.opacity(0.10), lineWidth: 0.5).padding(-0.5))
    }
}

/// A wooden peg with a metal spring round its middle (T-034). What you
/// pinned has a peg painted dark grey, so what stays reads at a glance.
struct Clothespin: View {
    var pinned = false

    private let metal = LinearGradient(
        stops: [
            .init(color: Color(red: 0.725, green: 0.506, blue: 0.275), location: 0),
            .init(color: Color(red: 0.914, green: 0.749, blue: 0.522), location: 0.46),
            .init(color: Color(red: 0.784, green: 0.576, blue: 0.353), location: 0.54),
            .init(color: Color(red: 0.612, green: 0.416, blue: 0.212), location: 1),
        ],
        startPoint: .leading, endPoint: .trailing)

    private let spaceGrey = LinearGradient(
        stops: [
            .init(color: Color(white: 0.30), location: 0),
            .init(color: Color(white: 0.47), location: 0.35),
            .init(color: Color(white: 0.38), location: 0.65),
            .init(color: Color(white: 0.24), location: 1),
        ],
        startPoint: .leading, endPoint: .trailing)

    var body: some View {
        RoundedRectangle(cornerRadius: 2.5, style: .continuous)
            .fill(pinned ? spaceGrey : metal)
            .frame(width: 10, height: 28)
            .overlay(
                RoundedRectangle(cornerRadius: 3.5, style: .continuous)
                    .stroke(LinearGradient(colors: [Color.white.opacity(pinned ? 0.45 : 0.9), Color.black.opacity(0.18)],
                                           startPoint: .top, endPoint: .bottom),
                            lineWidth: 0.6)
            )
            .overlay(alignment: .top) {
                // The spring that holds the two halves together.
                Capsule()
                    .fill(LinearGradient(colors: [Color(white: 0.62), Color(white: 0.94), Color(white: 0.5)],
                                         startPoint: .top, endPoint: .bottom))
                    .frame(width: 13, height: 3)
                    .padding(.top, 10)
            }
            .shadow(color: .black.opacity(0.30), radius: 2, y: 1.5)
            .allowsHitTesting(false)
    }
}
