import AppKit
import Quartz

/// A quick look at a photo without opening it: the same Quick Look window
/// Finder shows. Space with the pointer on a photo, a force click, or Quick
/// Look from its menu. The arrow keys go through the rest of the line, and
/// Space or Escape closes it.
///
/// The line never takes the keyboard from the app you are in: Space is only
/// borrowed while the pointer rests on a photo, and the window takes the
/// keyboard once it is open.
@MainActor
final class QuickLook: NSObject, QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    static let shared = QuickLook()

    private var urls: [URL] = []

    /// Called when the window closes, to give the keyboard back.
    var onClosed: () -> Void = {}

    var isOpen: Bool { QLPreviewPanel.sharedPreviewPanelExists() && QLPreviewPanel.shared()?.isVisible == true }

    func close() { QLPreviewPanel.shared()?.orderOut(nil) }

    func show(_ urls: [URL], at index: Int) {
        guard !urls.isEmpty, let panel = QLPreviewPanel.shared() else { return }
        self.urls = urls
        NSApp.activate(ignoringOtherApps: true)
        // Quick Look asks the responder chain who controls it; with no
        // window of ours that is the app delegate, which hands it to us.
        panel.updateController()
        panel.reloadData()
        panel.currentPreviewItemIndex = index
        panel.makeKeyAndOrderFront(nil)
    }

    func take(_ panel: QLPreviewPanel) {
        panel.dataSource = self
        panel.delegate = self
    }

    func release(_ panel: QLPreviewPanel) {
        panel.dataSource = nil
        panel.delegate = nil
        urls = []
        onClosed()
    }

    // MARK: QLPreviewPanelDataSource

    nonisolated func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int {
        MainActor.assumeIsolated { urls.count }
    }

    nonisolated func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> QLPreviewItem! {
        MainActor.assumeIsolated { urls.indices.contains(index) ? urls[index] as NSURL : nil }
    }

    // MARK: QLPreviewPanelDelegate

    /// Left and right go to the photo before or after it on the line.
    nonisolated func previewPanel(_ panel: QLPreviewPanel!, handle event: NSEvent!) -> Bool {
        guard event.type == .keyDown, let panel else { return false }
        // ⌘Y again, Return or Escape closes it; the keyboard goes back to
        // the line's search, so ⏎ never lands in Quick Look (review #12).
        let closes = (event.modifierFlags.contains(.command) && event.charactersIgnoringModifiers == "y")
            || event.keyCode == 36 || event.keyCode == 53
        if closes {
            MainActor.assumeIsolated { panel.orderOut(nil) }
            return true
        }
        let step: Int
        switch Int(event.keyCode) {
        case 123: step = -1 // left arrow
        case 124: step = 1 // right arrow
        default: return false
        }
        return MainActor.assumeIsolated {
            let next = panel.currentPreviewItemIndex + step
            guard urls.indices.contains(next) else { return true }
            panel.currentPreviewItemIndex = next
            return true
        }
    }
}
