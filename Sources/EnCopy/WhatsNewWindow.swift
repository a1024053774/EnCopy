import AppKit
import SwiftUI

/// The small window that tells what a version brings, once, after an update
/// (T-074): a picture when the version has a theme to show, a few lines, and
/// a button to put that theme on.
@MainActor
final class WhatsNewWindow {
    static let shared = WhatsNewWindow()
    private var window: NSWindow?

    func show(_ entry: WhatsNew.Entry, version: String, current: Theme, putOn: @escaping (Theme) -> Void) {
        window?.close()
        let w = NSWindow(contentRect: .zero, styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = String(format: L("New in EnCopy %@"), version)
        w.isReleasedWhenClosed = false
        w.contentView = NSHostingView(rootView: WhatsNewView(
            entry: entry, current: current,
            putOn: { [weak w] theme in putOn(theme); w?.close() },
            close: { [weak w] in w?.close() }))
        w.setContentSize(w.contentView?.fittingSize ?? NSSize(width: 420, height: 360))
        w.center()
        window = w
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }
}

private struct WhatsNewView: View {
    let entry: WhatsNew.Entry
    let current: Theme
    let putOn: (Theme) -> Void
    let close: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let theme = entry.theme {
                ThemeScene(theme: theme, width: 372, height: 104, scale: 1.7)
                    .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                    .overlay {
                        RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(.black.opacity(0.12), lineWidth: 1)
                    }
                    .accessibilityLabel(theme.title)
            }
            VStack(alignment: .leading, spacing: 8) {
                ForEach(entry.points, id: \.self) { point in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text("•").foregroundStyle(.secondary)
                        Text(point).fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            .font(.system(size: 13))
            HStack {
                Spacer()
                Button(L("Got It"), action: close)
                    .keyboardShortcut(entry.theme == nil || entry.theme == current ? .defaultAction : .cancelAction)
                if let theme = entry.theme, theme != current {
                    Button(String(format: L("Use %@"), theme.title)) { putOn(theme) }
                        .keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(24)
        .frame(width: 420)
    }
}
