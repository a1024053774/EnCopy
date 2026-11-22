// Draws the EnCopy app icon (placeholder): a paper receipt pegged to a twine line.
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

func color(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: r / 255, green: g / 255, blue: b / 255, alpha: a)
}

func shadow(_ alpha: CGFloat, blur: CGFloat, y: CGFloat) {
    let s = NSShadow()
    s.shadowColor = color(20, 24, 48, alpha)
    s.shadowBlurRadius = blur
    s.shadowOffset = NSSize(width: 0, height: y)
    s.set()
}

// Body, following the macOS icon grid: 824pt square, 100pt margin.
let body = NSRect(x: 100, y: 100, width: 824, height: 824)
let shape = NSBezierPath(roundedRect: body, xRadius: 185, yRadius: 185)

// A plain wall: soft drop shadow, then a vertical gradient, top to bottom.
ctx.saveGState()
shadow(0.30, blur: 26, y: -12)
color(0xc7, 0xd1, 0xdb).setFill()
shape.fill()
ctx.restoreGState()

ctx.saveGState()
shape.addClip()
NSGradient(colors: [color(0xe3, 0xe8, 0xed), color(0xc7, 0xd1, 0xdb)],
           atLocations: [0, 1], colorSpace: .sRGB)!
    .draw(in: body, angle: -90)
ctx.restoreGState()

// The twine, clipped to the body so its ends leave at the edges.
let left = NSPoint(x: 60, y: 720), right = NSPoint(x: 964, y: 720)
let control = NSPoint(x: 512, y: 580)  // about 70 px of sag at the centre
func twinePath() -> NSBezierPath {
    let p = NSBezierPath()
    p.move(to: left)
    p.curve(to: right,
            controlPoint1: NSPoint(x: left.x + (control.x - left.x) * 2 / 3, y: left.y + (control.y - left.y) * 2 / 3),
            controlPoint2: NSPoint(x: right.x + (control.x - right.x) * 2 / 3, y: right.y + (control.y - right.y) * 2 / 3))
    return p
}
ctx.saveGState()
shape.addClip()
let twine = twinePath()
twine.lineWidth = 12
color(0x8a, 0x6d, 0x45).setStroke()
twine.stroke()
let highlight = twinePath()
highlight.lineWidth = 4
highlight.transform(using: AffineTransform(translationByX: 0, byY: 2))
color(0xc9, 0xab, 0x7c).setStroke()
highlight.stroke()
ctx.restoreGState()

// One paper receipt hanging from the twine at the centre, tilted a little.
// Its top edge sits just above the twine's lowest point, so the peg below
// overlaps both.
ctx.saveGState()
ctx.translateBy(x: 512, y: 672)
ctx.rotate(by: -4 * .pi / 180)

// Zigzag bottom edge: sixteen teeth, 25 px wide and 14 px deep.
let paper = NSBezierPath()
paper.move(to: NSPoint(x: -200, y: 0))
paper.line(to: NSPoint(x: 200, y: 0))
paper.line(to: NSPoint(x: 200, y: -470))
for i in stride(from: 15, through: 0, by: -1) {
    let x0 = -200 + CGFloat(i) * 25
    paper.line(to: NSPoint(x: x0 + 12.5, y: -484))
    paper.line(to: NSPoint(x: x0, y: -470))
}
paper.close()

ctx.saveGState()
shadow(0.28, blur: 30, y: -14)
color(0xfb, 0xfa, 0xf4).setFill()
paper.fill()
ctx.restoreGState()

// ⌥C, centred horizontally in the upper half.
let style = NSMutableParagraphStyle()
style.alignment = .center
NSAttributedString(string: "⌥C", attributes: [
    .font: NSFont.systemFont(ofSize: 150, weight: .heavy),
    .foregroundColor: color(0x2a, 0x25, 0x1f),
    .paragraphStyle: style,
]).draw(in: NSRect(x: -200, y: -255, width: 400, height: 215))

// Three rounded rule bars under the shortcut, like a receipt's text lines.
color(0xcd, 0xbf, 0x9f).setFill()
for (i, width) in [280.0, 220.0, 248.0].enumerated() {
    NSBezierPath(roundedRect: NSRect(x: -160, y: -295 - CGFloat(i) * 34, width: width, height: 18),
                 xRadius: 9, yRadius: 9).fill()
}
ctx.restoreGState()

// One wooden clothes-peg on top, straddling the twine and the receipt's
// top edge.
let pegRect = NSRect(x: 484, y: 597, width: 56, height: 150)
let peg = NSBezierPath(roundedRect: pegRect, xRadius: 22, yRadius: 22)
ctx.saveGState()
NSGradient(colors: [color(0xe7, 0xbd, 0x86), color(0xc4, 0x8f, 0x55)],
           atLocations: [0, 1], colorSpace: .sRGB)!
    .draw(in: peg, angle: 0)
ctx.restoreGState()

// The groove where the twine passes, and a soft outline.
let groove = NSBezierPath()
groove.move(to: NSPoint(x: 488, y: 650))
groove.line(to: NSPoint(x: 536, y: 650))
groove.lineWidth = 6
groove.lineCapStyle = .round
color(0x59, 0x62, 0x6c, 0.6).setStroke()
groove.stroke()

peg.lineWidth = 2
color(0x8a, 0x6d, 0x45, 0.5).setStroke()
peg.stroke()

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
