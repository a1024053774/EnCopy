import AppKit

/// The menu bar glyph from the design: a sagging rope with two pegs on it.
/// The pegs are filled while copied things are being hung, outlined while
/// the clipboard is not watched. A template image, so it follows the menu
/// bar's light or dark look.
enum MenuBarIcon {
    static let size = NSSize(width: 18, height: 12)

    static func image(watching: Bool) -> NSImage {
        let image = NSImage(size: size, flipped: true) { _ in
            NSColor.black.set()
            let rope = NSBezierPath()
            rope.move(to: NSPoint(x: 1, y: 2))
            rope.curve(to: NSPoint(x: 17, y: 2), controlPoint1: NSPoint(x: 6.33, y: 5.33),
                       controlPoint2: NSPoint(x: 11.67, y: 5.33))
            rope.lineWidth = 1.3
            rope.lineCapStyle = .round
            rope.stroke()
            for x in [4.5, 11.5] as [CGFloat] {
                let peg = NSBezierPath(roundedRect: NSRect(x: x, y: 3, width: 2, height: 5), xRadius: 0.6, yRadius: 0.6)
                if watching {
                    peg.fill()
                } else {
                    peg.lineWidth = 0.8
                    peg.stroke()
                }
            }
            return true
        }
        image.isTemplate = true
        return image
    }
}
