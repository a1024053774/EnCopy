// Draws the EnCopy app icon: a polaroid and a ⌥C receipt pegged with
// sprung wooden clothespins to a two-tone twine, on a night-ink wall. The
// receipt has the amber edge of the picked item, as after ⌥C (T-037).
// Usage: swift scripts/make-icon.swift out.png
import AppKit

let size: CGFloat = 1024
let out = CommandLine.arguments.dropFirst().first ?? "icon.png"

let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size),
                           bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                           colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext

func color(_ hex: UInt32, _ a: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat(hex >> 16 & 0xff) / 255, green: CGFloat(hex >> 8 & 0xff) / 255,
            blue: CGFloat(hex & 0xff) / 255, alpha: a)
}

func shadow(_ alpha: CGFloat, blur: CGFloat, x: CGFloat = 0, y: CGFloat) {
    let s = NSShadow()
    s.shadowColor = NSColor(white: 0, alpha: alpha)
    s.shadowBlurRadius = blur
    s.shadowOffset = NSSize(width: x, height: y)
    s.set()
}

/// Runs `draw` with the origin moved to `at` and turned by `degrees`.
func turned(_ degrees: CGFloat, at: CGPoint, _ draw: () -> Void) {
    ctx.saveGState()
    ctx.translateBy(x: at.x, y: at.y)
    ctx.rotate(by: degrees * .pi / 180)
    draw()
    ctx.restoreGState()
}

// Body, following the macOS icon grid: 824pt square, 100pt margin.
let body = NSRect(x: 100, y: 100, width: 824, height: 824)
let shape = NSBezierPath(roundedRect: body, xRadius: 185, yRadius: 185)

ctx.saveGState()
shadow(0.35, blur: 28, y: -12)
color(0x1b2338).setFill()
shape.fill()
ctx.restoreGState()

// The wall: night ink, a little lighter where the lamp is, top left.
ctx.saveGState()
shape.addClip()
NSGradient(colors: [color(0x34446a), color(0x222c47), color(0x161d31)])!
    .draw(in: body, angle: -70)
let glow = NSGradient(colors: [color(0xffd9a0, 0.20), color(0xffd9a0, 0)])!
glow.draw(fromCenter: NSPoint(x: 330, y: 820), radius: 0, toCenter: NSPoint(x: 330, y: 820), radius: 560, options: [])
ctx.restoreGState()

// The twine sags from edge to edge.
let left = CGPoint(x: 100, y: 742), right = CGPoint(x: 924, y: 742)
let sag: CGFloat = 92
func ropeY(_ x: CGFloat) -> CGFloat {
    let f = (x - left.x) / (right.x - left.x)
    return left.y - 4 * sag * f * (1 - f)
}
func ropeSlope(_ x: CGFloat) -> CGFloat {
    let f = (x - left.x) / (right.x - left.x)
    return atan(-4 * sag * (1 - 2 * f) / (right.x - left.x)) * 180 / .pi
}

ctx.saveGState()
shape.addClip()
let rope = NSBezierPath()
rope.move(to: left)
var x = left.x
while x <= right.x { rope.line(to: NSPoint(x: x, y: ropeY(x))); x += 4 }
rope.lineCapStyle = .round
ctx.saveGState()
shadow(0.45, blur: 10, y: -8)
rope.lineWidth = 15
color(0x8a6534).setStroke()
rope.stroke()
ctx.restoreGState()
// Two strands twisted together: light dashes over the dark one.
rope.lineWidth = 9
rope.setLineDash([14, 12], count: 2, phase: 0)
color(0xd8b077).setStroke()
rope.stroke()
ctx.restoreGState()

/// A wooden clothespin standing on the rope, its jaw at the origin.
func clothespin() {
    let w: CGFloat = 58, h: CGFloat = 150
    let jaw = NSRect(x: -w / 2, y: -h * 0.62, width: w, height: h)
    ctx.saveGState()
    shadow(0.4, blur: 12, y: -6)
    let pin = NSBezierPath(roundedRect: jaw, xRadius: 16, yRadius: 16)
    color(0xc8935a).setFill()
    pin.fill()
    ctx.restoreGState()
    NSGradient(colors: [color(0xe2b47a), color(0xc8935a), color(0xa8743f)])!
        .draw(in: NSBezierPath(roundedRect: jaw, xRadius: 16, yRadius: 16), angle: 0)
    // The split between the two halves.
    color(0x7a5228, 0.8).setFill()
    NSBezierPath(rect: NSRect(x: -2.5, y: jaw.minY + 10, width: 5, height: jaw.height * 0.52)).fill()
    // The spring: a dark steel coil across the middle.
    let coil = NSRect(x: -w / 2 - 6, y: jaw.minY + jaw.height * 0.56, width: w + 12, height: 26)
    color(0x3a3f4a).setFill()
    NSBezierPath(roundedRect: coil, xRadius: 13, yRadius: 13).fill()
    color(0x9aa3b2).setStroke()
    for i in 0..<4 {
        let cx = coil.minX + 12 + CGFloat(i) * (coil.width - 24) / 3
        let turn = NSBezierPath()
        turn.move(to: NSPoint(x: cx - 6, y: coil.minY + 4))
        turn.line(to: NSPoint(x: cx + 6, y: coil.maxY - 4))
        turn.lineWidth = 4
        turn.lineCapStyle = .round
        turn.stroke()
    }
}

// The polaroid, behind, on the left.
let polaroidX: CGFloat = 368
turned(-9 + ropeSlope(polaroidX) * 0.4, at: CGPoint(x: polaroidX, y: ropeY(polaroidX))) {
    let card = NSRect(x: -135, y: -390, width: 270, height: 330)
    ctx.saveGState()
    shadow(0.5, blur: 30, y: -18)
    color(0xfcfcf8).setFill()
    NSBezierPath(roundedRect: card, xRadius: 8, yRadius: 8).fill()
    ctx.restoreGState()
    let photo = NSRect(x: card.minX + 22, y: card.minY + 82, width: card.width - 44, height: card.height - 104)
    NSGradient(colors: [color(0x2e6fd0), color(0x1f4f9c)])!.draw(in: photo, angle: -90)
    color(0xffd25a).setFill()
    NSBezierPath(roundedRect: NSRect(x: photo.minX + 34, y: photo.minY + 50, width: 132, height: 90),
                 xRadius: 6, yRadius: 6).fill()
    color(0xc9c2b4).setFill()
    NSBezierPath(roundedRect: NSRect(x: card.minX + 60, y: card.minY + 34, width: 150, height: 14),
                 xRadius: 7, yRadius: 7).fill()
    clothespin()
}

// The receipt, in front, on the right, picked: an amber edge.
let receiptX: CGFloat = 658
turned(5 + ropeSlope(receiptX) * 0.4, at: CGPoint(x: receiptX, y: ropeY(receiptX))) {
    let w: CGFloat = 310, top: CGFloat = -34, bottom: CGFloat = -520, tooth: CGFloat = 18
    let paper = NSBezierPath()
    paper.move(to: NSPoint(x: -w / 2, y: top))
    paper.line(to: NSPoint(x: w / 2, y: top))
    paper.line(to: NSPoint(x: w / 2, y: bottom + tooth))
    var tx = w / 2, down = true
    while tx > -w / 2 {
        tx -= tooth
        paper.line(to: NSPoint(x: max(tx, -w / 2), y: down ? bottom : bottom + tooth))
        down.toggle()
    }
    paper.line(to: NSPoint(x: -w / 2, y: bottom + tooth))
    paper.close()
    ctx.saveGState()
    shadow(0.55, blur: 34, y: -20)
    color(0xfbfaf4).setFill()
    paper.fill()
    ctx.restoreGState()
    paper.lineWidth = 12
    paper.lineJoinStyle = .round
    color(0xd38a22).setStroke()
    paper.stroke()

    // ⌥C, big, in ink.
    let ink = color(0x2a251f)
    let glyphs = NSAttributedString(string: "⌥C", attributes: [
        .font: NSFont.systemFont(ofSize: 150, weight: .heavy),
        .foregroundColor: ink,
        .kern: -4,
    ])
    let g = glyphs.size()
    glyphs.draw(at: NSPoint(x: -g.width / 2, y: top - 70 - g.height))
    // A dashed rule and three lines of words.
    let rule = NSBezierPath()
    rule.move(to: NSPoint(x: -w / 2 + 40, y: top - 290))
    rule.line(to: NSPoint(x: w / 2 - 40, y: top - 290))
    rule.lineWidth = 5
    rule.setLineDash([12, 10], count: 2, phase: 0)
    color(0x8a7f70).setStroke()
    rule.stroke()
    color(0xb9ad98).setFill()
    for (i, len) in [CGFloat(230), 190, 140].enumerated() {
        NSBezierPath(roundedRect: NSRect(x: -w / 2 + 40, y: top - 336 - CGFloat(i) * 44, width: len, height: 18),
                     xRadius: 9, yRadius: 9).fill()
    }
    clothespin()
}

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
