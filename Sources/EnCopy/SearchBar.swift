import AppKit
import Carbon
import SwiftUI

/// Searching the line: a small glass field under it. What does not match
/// leaves the line for as long as the search lasts; nothing is taken down.
@MainActor
final class SearchBar: NSObject, NSWindowDelegate {
    private let panel: SearchPanel
    private let line: Line
    /// Return: the newest match is copied and the search ends.
    var onReturn: () -> Void = {}
    /// Escape with nothing typed, or a click outside the line: the search
    /// ends and the line goes up. Clicking a photo on the line is not
    /// outside: the line never takes the keyboard from the field.
    var onEnd: () -> Void = {}
    /// ← and → while summoned: -1 left, +1 right along the pick.
    var onMove: (Int) -> Void = { _ in }

    var isShown: Bool { panel.isVisible }

    init(line: Line) {
        self.line = line
        panel = SearchPanel()
        super.init()
        panel.delegate = self
        panel.owner = self
        panel.contentView = NSHostingView(rootView: SearchField(
            line: line,
            onSubmit: { [weak self] in self?.onReturn() },
            onEscape: { [weak self] in self?.escape() }))
        panel.onEscape = { [weak self] in self?.escape() }
    }

    /// Under the line, in the middle of its screen.
    func show(below lineFrame: NSRect) {
        let size = NSSize(width: 340, height: 40)
        panel.setFrame(NSRect(x: lineFrame.midX - size.width / 2, y: lineFrame.minY - size.height - 6,
                              width: size.width, height: size.height), display: true)
        panel.makeKeyAndOrderFront(nil)
    }

    func hide() {
        panel.orderOut(nil)
    }

    /// The first Escape clears what was typed; the next one ends the search.
    private func escape() {
        if line.query.isEmpty { onEnd() } else { line.query = "" }
    }

    /// ← and → pick along the line instead of moving the caret. Returns
    /// whether the key was taken. Not while an input method is composing:
    /// the arrows then belong to it.
    func takes(_ event: NSEvent, composing: Bool) -> Bool {
        guard event.type == .keyDown, !composing else { return false }
        guard event.modifierFlags.intersection([.command, .option, .control, .shift]).isEmpty else { return false }
        switch event.keyCode {
        case UInt16(kVK_LeftArrow): onMove(-1)
        case UInt16(kVK_RightArrow): onMove(1)
        default: return false
        }
        return true
    }

    func windowDidResignKey(_ notification: Notification) {
        guard panel.isVisible else { return }
        onEnd()
    }
}

/// Takes the keyboard without bringing EnCopy to the front, the way
/// Spotlight does, and floats over every app and Space.
private final class SearchPanel: NSPanel {
    var onEscape: () -> Void = {}
    weak var owner: SearchBar?

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

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
    override func cancelOperation(_ sender: Any?) { onEscape() }

    override func sendEvent(_ event: NSEvent) {
        if event.type == .keyDown {
            let taken = MainActor.assumeIsolated {
                owner?.takes(event, composing: (firstResponder as? NSTextView)?.hasMarkedText() ?? false) ?? false
            }
            if taken { return }
        }
        super.sendEvent(event)
    }
}

private struct SearchField: View {
    @ObservedObject var line: Line
    let onSubmit: () -> Void
    let onEscape: () -> Void
    @FocusState private var focused: Bool

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(.secondary)
            TextField(L("Search all history"), text: $line.query)
                .textFieldStyle(.plain)
                .font(.system(size: 14))
                .focused($focused)
                .onSubmit(onSubmit)
                .onExitCommand(perform: onEscape)
            if line.isSearching {
                Text(String(format: L("%d of %d"), line.matchCount, line.totalCount))
                    .font(.system(size: 11, weight: .medium).monospacedDigit())
                    .foregroundStyle(.secondary)
                    .fixedSize()
            }
        }
        .padding(.horizontal, 14)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .glassFrame(capsule: true)
        .padding(2)
        .onAppear { focused = true }
    }
}
