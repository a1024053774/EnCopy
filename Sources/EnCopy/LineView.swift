import SwiftUI

enum Layout {
    /// How big the photos hang, from Size in the menu bar. The clip, the rope
    /// and the glass frame keep their size; the photos and the room they take
    /// grow or shrink. Medium is the original size.
    enum Size: String, CaseIterable {
        case small, medium, large

        var scale: CGFloat {
            switch self {
            case .small: 0.8
            case .medium: 1
            case .large: 1.35
            }
        }
    }

    static var size: Size {
        get { Size(rawValue: UserDefaults.standard.string(forKey: "lineSize") ?? "") ?? .medium }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: "lineSize") }
    }

    static var photoMaxHeight: CGFloat { (104 * size.scale).rounded() }
    static var cardWidth: CGFloat { (150 * size.scale).rounded() }
    static var spacing: CGFloat { cardWidth + 24 }
    static var panelHeight: CGFloat { photoMaxHeight + 106 }
    static let ropeTop: CGFloat = 10
    static let pinAbove: CGFloat = 9.5

    /// The rope hangs as a parabola from edge to edge of the screen.
    static func sag(width: CGFloat) -> CGFloat { min(30, width * 0.018) }

    static func anchorsPinned(_ pinned: Int, visible: Int) -> Bool {
        pinned > 0 && pinned < visible - 1
    }

    /// On a line that scrolls with pinned photos anchored, where the photos
    /// scrolling behind them should have faded out. Nil otherwise.
    static func pinnedEdge(count: Int, width: CGFloat, visible: Int, pinned: Int) -> CGFloat? {
        guard Line.keepOnLine != nil, count > visible, anchorsPinned(pinned, visible: visible) else { return nil }
        return first(width: width, visible: visible) + CGFloat(pinned - 1) * spacing
    }

    /// The leftmost place on a line that scrolls.
    static func first(width: CGFloat, visible: Int) -> CGFloat {
        width / 2 - CGFloat(visible - 1) * spacing / 2
    }

    /// Which place, counting from the left, the item at `index` hangs in.
    /// Items are stored oldest first with the pinned ones at the front; on
    /// the line the pinned ones keep their order left of the knot, and the
    /// rest hang newest first from the knot, older ones further right.
    /// The mapping is its own inverse: the item in place `p` is at index
    /// `slot(p, …)`.
    static func slot(_ index: Int, count: Int, pinned: Int) -> Int {
        index < pinned ? index : pinned + (count - 1 - index)
    }

    static func ropeY(x: CGFloat, width: CGFloat) -> CGFloat {
        guard width > 0 else { return ropeTop }
        let f = x / width
        return ropeTop + 4 * sag(width: width) * f * (1 - f)
    }

    /// Photos hang `spacing` apart, centred, in the places `slot` gives.
    /// When there are more than the `visible` that fit: with Keep on line
    /// set, they keep their spacing, the newest stays in the first place
    /// past the knot, and `scroll` brings older ones in from the right.
    /// Otherwise (a line hung on a wider screen) they move closer together
    /// so none is past the edge.
    ///
    /// On a line that scrolls the pinned photos stay put at its left end,
    /// and the rest scroll behind them, unless they would leave almost no
    /// room for the rest.
    static func x(index: Int, count: Int, width: CGFloat, visible: Int, scroll: CGFloat,
                  pinned: Int = 0) -> CGFloat {
        let place = CGFloat(slot(index, count: count, pinned: pinned))
        if Line.keepOnLine != nil && count > visible {
            let stays = anchorsPinned(pinned, visible: visible) && index < pinned
            return first(width: width, visible: visible) + place * spacing - (stays ? 0 : scroll)
        }
        let gaps = CGFloat(max(count - 1, 0))
        let step = gaps > 0 ? min(spacing, max(0, width - 200) / gaps) : spacing
        return width / 2 - gaps * step / 2 + place * step
    }
}

struct LineView: View {
    @ObservedObject var line: Line

    /// While searching, only what matches hangs, from the left as on any
    /// line: no knot, no pinned end, no scroll.
    private var shown: [Pegged] { line.shown }
    private var pinned: Int { line.layoutPinned }
    private var scroll: CGFloat { line.isSearching ? 0 : line.scroll }
    private var narrowed: Bool { line.isSearching || line.kindFilter != nil || line.shelf != .line }

    var body: some View {
        GeometryReader { geo in
            let width = geo.size.width
            ZStack(alignment: .topLeading) {
                Rope(width: width)

                if line.items.isEmpty && !line.isSearching {
                    Hint()
                        .position(x: width / 2, y: Layout.ropeY(x: width / 2, width: width) + 34)
                        .transition(.opacity)
                }

                if line.layoutPinned > 0, let x = knotX(width: width) {
                    Knot()
                        .position(x: x, y: Layout.ropeY(x: x, width: width) + 0.5)
                        .transition(.opacity)
                }

                ForEach(Array(shown.enumerated()), id: \.element.id) { index, item in
                    let x = Layout.x(index: index, count: shown.count, width: width,
                                     visible: line.visibleCount, scroll: scroll, pinned: pinned)
                    // On a line that scrolls, a photo reaching the edge of the
                    // screen, or the knot it slips behind, dims and stops taking
                    // clicks as soon as it starts to.
                    let bounds = index >= pinned ? scrollBounds(width: width) : nil
                    let cardLeft = x - Layout.cardWidth / 2, cardRight = x + Layout.cardWidth / 2
                    let out = bounds.map { cardLeft < $0.lowerBound || cardRight > $0.upperBound } == true
                    // On the left it fades gradually: whole in the first place,
                    // gone once halfway behind the limit.
                    let fadeIn = bounds.map { bounds -> CGFloat in
                        let gone = bounds.lowerBound - Layout.cardWidth / 2
                        let rest = firstLooseX(width: width) - Layout.cardWidth / 2
                        return min(1, max(0, (cardLeft - gone) / max(1, rest - gone)))
                    } ?? 1
                    let ropeY = Layout.ropeY(x: x, width: width)
                    PeggedView(item: item, line: line, sweepDelay: Motion.sweepDelay(x: x, width: width))
                        .frame(width: Layout.cardWidth, height: Layout.panelHeight - ropeY, alignment: .top)
                        // Photos scrolled towards an edge fade out before they reach it.
                        .opacity(min(1, max(0, min(x, width - x) / 90)) * fadeIn
                                 * (bounds.map { cardRight > $0.upperBound } == true ? 0.35 : 1))
                        .animation(.easeOut(duration: 0.2), value: out)
                        .allowsHitTesting(!out)
                        .zIndex(index < pinned ? 1 : 0)
                        // Left out of a search, a photo rises out of sight
                        // instead of falling: it is still on the line.
                        .transition(.opacity.combined(with: .offset(y: -40)))
                        .position(x: x, y: ropeY - Layout.pinAbove + (Layout.panelHeight - ropeY) / 2)
                }
            }
            // A search, Tab or ↓ leaving papers out closes the gaps without a
            // bounce; a new paper hung makes the rest step aside.
            .animation(narrowed ? Motion.slide : Motion.makeWay, value: shown.map(\.id))
            .animation(.easeInOut(duration: 0.3), value: line.items.isEmpty)
            .animation(Motion.makeWay, value: line.items.map(\.kept))
            // Tucked away, the whole line waits above the top edge and slides
            // out from under the menu bar, the way an auto-hiding Dock does.
            // With Reduce motion it only fades.
            .offset(y: line.revealed || Motion.reduced ? 0 : -(Layout.panelHeight + 12))
            .opacity(line.revealed || !Motion.reduced ? 1 : 0)
            .animation(Motion.panel(revealed: line.revealed, bySummon: line.cameBySummon), value: line.revealed)
        }
        .onPreferenceChange(HitRectsKey.self) { rects in
            line.hitRects = rects
        }
    }

    /// Where the first photo past the pinned ones hangs at rest on a
    /// scrolling line: the first place, or the one just past the knot.
    private func firstLooseX(width: CGFloat) -> CGFloat {
        let lead = Layout.anchorsPinned(pinned, visible: line.visibleCount) ? pinned : 0
        return Layout.first(width: width, visible: line.visibleCount) + CGFloat(lead) * Layout.spacing
    }

    /// Where a photo on a scrolling line stops being whole in view: the
    /// knot when pinned photos stay put, or else the left edge of the screen,
    /// and the right edge. Nil on a line that does not scroll.
    private func scrollBounds(width: CGFloat) -> ClosedRange<CGFloat>? {
        guard Line.keepOnLine != nil, shown.count > line.visibleCount else { return nil }
        let pinnedStay = Layout.pinnedEdge(count: shown.count, width: width,
                                           visible: line.visibleCount, pinned: pinned) != nil
        let start = pinnedStay ? knotX(width: width) ?? 0 : 0
        return start...width
    }

    /// Halfway between the last pinned photo and the place past it, where
    /// the newest hangs. The empty place a pinned photo leaves still counts,
    /// so the knot stays put until the gaps close.
    private func knotX(width: CGFloat) -> CGFloat? {
        guard pinned > 0 else { return nil }
        let last = pinned - 1
        let count = shown.count
        let x = { (i: Int) in Layout.x(index: i, count: count, width: width,
                                       visible: line.visibleCount, scroll: scroll, pinned: pinned) }
        // On a line that scrolls, the pinned photos stay put, and the knot with them.
        if Layout.pinnedEdge(count: count, width: width, visible: line.visibleCount, pinned: pinned) != nil {
            return x(last) + Layout.spacing / 2
        }
        let next = pinned < count ? x(Layout.slot(pinned, count: count, pinned: pinned)) : x(last) + Layout.spacing
        return (x(last) + next) / 2
    }
}

/// Where the line is tied: what hangs left of it is pinned and stays.
private struct Knot: View {
    var body: some View {
        ZStack {
            Ellipse()
                .fill(Color(white: 0.5))
                .frame(width: 7, height: 5.5)
                .shadow(color: .black.opacity(0.25), radius: 1, y: 1)
            // Two short loose ends hanging from it.
            Path { p in
                p.move(to: CGPoint(x: 3.5, y: 3))
                p.addQuadCurve(to: CGPoint(x: 1.5, y: 11), control: CGPoint(x: 3.8, y: 7))
                p.move(to: CGPoint(x: 4, y: 3))
                p.addQuadCurve(to: CGPoint(x: 6.5, y: 10), control: CGPoint(x: 4.5, y: 7))
            }
            .stroke(Color(white: 0.55), style: StrokeStyle(lineWidth: 1, lineCap: .round))
            .frame(width: 8, height: 12)
            .offset(y: 5)
        }
        .allowsHitTesting(false)
    }
}

private struct Hint: View {
    var body: some View {
        Text(L("Take a screenshot and it will hang here"))
            .font(.system(size: 12, weight: .medium, design: .rounded))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            .background(.regularMaterial, in: Capsule())
    }
}

/// Twine (T-034): two strands of brown twisted together, with a soft shadow
/// so it reads on any background. It fades out at both ends so it seems to
/// come from beyond the screen.
struct Rope: View {
    let width: CGFloat
    @Environment(\.colorScheme) private var scheme

    private var path: Path {
        Path { p in
            let top = Layout.ropeTop
            p.move(to: CGPoint(x: -20, y: top))
            p.addQuadCurve(
                to: CGPoint(x: width + 20, y: top),
                control: CGPoint(x: width / 2, y: top + 2 * Layout.sag(width: width)))
        }
    }

    var body: some View {
        ZStack {
            path.stroke(Color.black.opacity(0.24), lineWidth: 2.4).offset(y: 1.4).blur(radius: 1.3)
            path.stroke(scheme == .dark ? Color(red: 0.663, green: 0.541, blue: 0.369) : Color(red: 0.541, green: 0.427, blue: 0.271), lineWidth: 2.2)
            path.stroke(scheme == .dark ? Color(red: 0.831, green: 0.725, blue: 0.545) : Color(red: 0.788, green: 0.671, blue: 0.486),
                        style: StrokeStyle(lineWidth: 2.2, dash: [1.6, 3.4]))
        }
        .mask(
            LinearGradient(stops: [
                .init(color: .clear, location: 0),
                .init(color: .black, location: 0.08),
                .init(color: .black, location: 0.92),
                .init(color: .clear, location: 1),
            ], startPoint: .leading, endPoint: .trailing)
        )
        .allowsHitTesting(false)
    }
}

struct HitRectsKey: PreferenceKey {
    static var defaultValue: [UUID: CGRect] = [:]
    static func reduce(value: inout [UUID: CGRect], nextValue: () -> [UUID: CGRect]) {
        value.merge(nextValue()) { $1 }
    }
}
