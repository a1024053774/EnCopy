import AppKit
import SwiftUI

/// Every timing on the line, from the design's motion table (T-006, T-036).
/// Three rules: a key acts on the frame it is pressed, without waiting for
/// or queueing behind an animation; the more often something happens, the
/// quieter it moves; everything moves on springs. With Reduce motion on,
/// what is left is fading.
enum Motion {
    /// Set by the checks; otherwise the system's Reduce motion setting.
    static var reducedOverride: Bool?
    static var reduced: Bool {
        reducedOverride ?? NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
    }

    // MARK: Paper weight

    /// Each kind of paper hangs at its own weight: the receipt is lightest
    /// and swings longest, the folder is heaviest and settles first.
    enum Paper: CaseIterable { case receipt, tag, polaroid, tractor, folder }

    struct Weight: Equatable {
        let stiffness: Double
        let damping: Double
        /// How quickly a swing dies away: more is sooner.
        var settling: Double { damping / (2 * stiffness.squareRoot()) }
    }

    static func weight(_ paper: Paper) -> Weight {
        switch paper {
        case .receipt: Weight(stiffness: 38, damping: 2.4)
        case .tag: Weight(stiffness: 42, damping: 2.6)
        case .polaroid: Weight(stiffness: 46, damping: 2.8)
        case .tractor: Weight(stiffness: 50, damping: 3.0)
        case .folder: Weight(stiffness: 58, damping: 3.4)
        }
    }

    static func swing(_ paper: Paper) -> Animation {
        let w = weight(paper)
        return .interpolatingSpring(stiffness: w.stiffness, damping: w.damping)
    }

    // MARK: The panel

    /// ⌥C drops the line quickly; sliding down past the top edge is slower,
    /// as upstream had it. Going away has no bounce and starts at once.
    static let summon = Animation.spring(response: 0.28, dampingFraction: 0.86)
    static let slideDown = Animation.spring(response: 0.42, dampingFraction: 0.82)
    static let tuck = Animation.timingCurve(0.23, 1, 0.32, 1, duration: 0.16)
    /// Reduce motion: the panel only fades.
    static let fade = Animation.easeInOut(duration: 0.12)

    static func panel(revealed: Bool, bySummon: Bool, reduced: Bool = Motion.reduced) -> Animation {
        if reduced { return fade }
        if !revealed { return tuck }
        return bySummon ? summon : slideDown
    }

    // MARK: Swaying as the line comes down

    /// ⌥C: each paper sways a little as the line lands.
    static let summonSway: ClosedRange<Double> = 1.5...3
    /// Sliding down: a gust sweeps from left to right, harder.
    static let sweepSway: ClosedRange<Double> = 5...9
    /// The rightmost paper in the sweep starts this much later.
    static let sweepSpan: Double = 0.26

    static func sweepDelay(x: CGFloat, width: CGFloat) -> Double {
        guard width > 0 else { return 0 }
        return sweepSpan * Double(min(1, max(0, x / width)))
    }

    // MARK: Picking, searching, hanging

    /// The picked paper sinks, grows and gets its edge.
    static let pick = Animation.easeOut(duration: 0.11)
    /// The line sliding to keep the pick in view, and papers closing up
    /// after a search or Tab left some out: no bounce.
    static let slide = Animation.spring(response: 0.24, dampingFraction: 1)
    /// A new paper hung: the rest make way, as upstream had it.
    static let makeWay = Animation.spring(response: 0.55, dampingFraction: 0.78)
    /// A single click copies: the paper hops this far up and drops back.
    static let hopHeight: CGFloat = 7
    static let hop = Animation.spring(response: 0.42, dampingFraction: 0.72)

    // MARK: The rope, the pegs, leaving (T-038)

    /// The line comes down with its rope pulled almost straight; it springs
    /// into its sag. ⌥C settles quickly; sliding down bounces twice; ↓ opening the basket list once.
    static let ropeTaut: CGFloat = 0.15
    static let ropeSummon = Animation.spring(response: 0.3, dampingFraction: 0.85)
    static let ropeSlide = Animation.spring(response: 0.5, dampingFraction: 0.5)
    static let basketTaut: CGFloat = 0.6

    /// The line sliding to keep the pick in view: the papers swing the other
    /// way this far. Arrow presses closer together than `keyRepeat` are a
    /// held key, and a held key slides without swinging.
    static let slideSway: Double = 1.5
    static let keyRepeat: TimeInterval = 0.12

    /// A new paper: the peg opens, then clamps.
    static let pegOpen: CGFloat = 1.3
    static let pegClamp = Animation.spring(response: 0.25, dampingFraction: 0.6)

    /// ⏎: the paper leaves its peg and flies to the pointer, shrinking.
    static let flyOut: TimeInterval = 0.3
    static let flyOutScale: CGFloat = 0.16

    /// ⌘⌫: the paper sinks at once, then falls under gravity.
    static let fall: TimeInterval = 0.42
    static let fallSink: CGFloat = 5
    /// The share of the fall spent sinking, eased out so the key shows at once.
    static let fallSinkShare: Double = 0.1

    /// Where a falling paper is, `raw` (0…1) through the fall: how far it has
    /// sunk below where it hung, and how far gravity has taken it (0…1).
    static func fallProgress(_ raw: Double) -> (sink: CGFloat, gravity: Double) {
        let s = min(1, raw / fallSinkShare)
        let sink = fallSink * CGFloat(1 - (1 - s) * (1 - s))
        let g = raw <= fallSinkShare ? 0 : (raw - fallSinkShare) / (1 - fallSinkShare)
        return (sink, g * g)
    }

    /// Copied while the line is tucked away: the menu bar pegs sway once,
    /// these angles in turn over 0.6 s.
    static let menuBarSway: [CGFloat] = [8, -6, 4, -2, 0]
    static let menuBarSwayTotal: TimeInterval = 0.6

    // MARK: Breeze

    /// No breeze until this long after the last key: none while you type or pick.
    static let breezeQuiet: TimeInterval = 4
    static let breezeEvery: ClosedRange<Double> = 7...16

    static func breezes(now: Date, lastKey: Date, reduced: Bool = Motion.reduced) -> Bool {
        !reduced && now.timeIntervalSince(lastKey) >= breezeQuiet
    }

    /// How far each paper sways in a gust: half as far while summoned.
    static func gust(summoned: Bool) -> ClosedRange<Double> {
        summoned ? 0.8...1.7 : 1.6...3.4
    }
}
