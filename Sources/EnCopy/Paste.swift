import AppKit
import Carbon
import SwiftUI

/// What the front app is sent to make it paste: ⌘V, as the key that types
/// V in the keyboard layout in use.
struct PasteKey: Equatable {
    let keyCode: Int
    let flags: CGEventFlags

    static var commandV: PasteKey {
        PasteKey(keyCode: Shortcut.keyCode(typing: "v") ?? kVK_ANSI_V, flags: .maskCommand)
    }

    /// The key down carries ⌘; the key up carries nothing. The system takes the flags of the last
    /// posted event as the modifier keys now held, so a key up that still said ⌘ would leave ⌘
    /// down for the user's next click.
    var strokes: [(keyDown: Bool, flags: CGEventFlags)] { [(true, flags), (false, [])] }
}

/// Pasting the picked item into the app in front. Sending the key needs
/// the Accessibility permission; without it the item is only copied.
@MainActor
final class Paster {
    private let line: Line
    private let isTrusted: () -> Bool
    private let send: (PasteKey) -> Void

    /// The line and its field go away first, so the keyboard is back with
    /// the front app before it is asked to paste.
    var tuckAway: () -> Void = {}
    /// Without the Accessibility permission nothing can be sent: the item
    /// is only copied, and this says so.
    var showHint: () -> Void = {}

    init(line: Line,
         isTrusted: @escaping () -> Bool = { AXIsProcessTrusted() },
         send: @escaping (PasteKey) -> Void = Paster.post) {
        self.line = line
        self.isTrusted = isTrusted
        self.send = send
    }

    /// `plain` is ⇧⏎: the words without their styling.
    func pastePicked(plain: Bool = false) {
        guard let item = line.picked else { return }
        paste(item.id, plain: plain)
    }

    /// One item into the front app: ⏎ on the pick, or a double click on a card.
    func paste(_ id: UUID, plain: Bool = false) {
        line.copy(id, plain: plain)
        tuckAway()
        if isTrusted() {
            send(.commandV)
        } else {
            showHint()
        }
    }

    /// The strokes in order — the key down with its flags, the key up with
    /// none — from the combined session posted where the system reads real
    /// keys.
    nonisolated static func post(_ key: PasteKey) {
        let source = CGEventSource(stateID: .combinedSessionState)
        for stroke in key.strokes {
            guard let event = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(key.keyCode),
                                      keyDown: stroke.keyDown) else { continue }
            event.flags = stroke.flags
            event.post(tap: .cghidEventTap)
        }
    }

    /// Asks for the Accessibility permission, then opens its pane so the
    /// user can allow EnCopy to send the paste.
    static func requestPermission() {
        let prompt = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        AXIsProcessTrustedWithOptions([prompt: true] as CFDictionary)
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }
}

/// "Copied. Press ⌘V to paste.": a small glass note under the menu bar, for
/// when the Accessibility permission is missing and ⌘V cannot be sent.
@MainActor
final class PasteHint {
    private let panel: HintPanel
    private var hideWork: DispatchWorkItem?

    init() {
        panel = HintPanel()
        panel.contentView = NSHostingView(rootView: HintContent { [weak self] in
            Paster.requestPermission()
            self?.hide()
        })
    }

    /// Top-centre just under the menu bar of that screen, or of the screen
    /// under the pointer when none is given. It goes away after six
    /// seconds; showing again restarts them.
    func show(on screen: NSScreen?) {
        let screen = screen ?? LinePanel.screenUnderPointer()
        guard let screen, let content = panel.contentView else { return }
        hideWork?.cancel()
        content.layoutSubtreeIfNeeded()
        let fit = content.fittingSize
        let size = NSSize(width: max(200, fit.width), height: max(40, fit.height))
        let frame = NSRect(x: screen.frame.midX - size.width / 2,
                           y: screen.visibleFrame.maxY - size.height - 8,
                           width: size.width, height: size.height)
        panel.setFrame(frame, display: true)
        panel.orderFrontRegardless()
        let work = DispatchWorkItem { [weak self] in self?.hide() }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 6, execute: work)
    }

    func hide() {
        hideWork?.cancel()
        hideWork = nil
        panel.orderOut(nil)
    }
}

/// A click lands without activating EnCopy, like the search field's panel.
private final class HintPanel: NSPanel {
    init() {
        super.init(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

private struct HintContent: View {
    let onAllow: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Text(L("Copied. Press ⌘V to paste."))
                .font(.system(size: 13, weight: .medium))
            Button(L("Allow pasting…"), action: onAllow)
                .font(.system(size: 13))
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
        .glassFrame(capsule: true)
        .padding(2)
    }
}
