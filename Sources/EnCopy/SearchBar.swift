import AppKit
import Carbon
import Combine
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
    /// ⇧⏎: paste the pick as plain text.
    var onPlainReturn: () -> Void = {}
    /// ⌘P: pin or unpin the pick.
    var onPin: () -> Void = {}
    /// ⌘Y: Quick Look on the pick.
    var onQuickLook: () -> Void = {}
    /// Tab and ⇧Tab: the next or previous kind of item.
    var onTab: (Int) -> Void = { _ in }
    /// ↓ and ↑: open the basket list, and move along it (T-044).
    var onVertical: (Int) -> Void = { _ in }

    var isShown: Bool { panel.isVisible }
    /// The line's frame it was last shown under, to grow from when the
    /// basket list opens.
    private var lineFrame = NSRect.zero
    private var watching: AnyCancellable?

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
        // The basket list opens under the field: the panel grows down.
        watching = line.$basketOpen.dropFirst().removeDuplicates().sink { [weak self] open in
            DispatchQueue.main.async { self?.place(open: open) }
        }
    }

    /// Under the line, in the middle of its screen.
    func show(below lineFrame: NSRect) {
        self.lineFrame = lineFrame
        place(open: line.basketOpen)
        panel.makeKeyAndOrderFront(nil)
    }

    /// Just the field, or the field with the basket list under it, its top
    /// edge staying just under the line.
    private func place(open: Bool) {
        guard lineFrame != .zero else { return }
        let screenHeight = (panel.screen ?? NSScreen.main)?.visibleFrame.height ?? 900
        let size = open ? NSSize(width: 600, height: min(480, max(240, screenHeight - lineFrame.height - 60)))
                        : NSSize(width: 340, height: 40)
        panel.setFrame(NSRect(x: lineFrame.midX - size.width / 2, y: lineFrame.minY - size.height - 6,
                              width: size.width, height: size.height), display: true)
    }

    func hide() {
        panel.orderOut(nil)
    }

    /// Takes the keyboard back, after Quick Look had it.
    func refocus() {
        guard panel.isVisible else { return }
        panel.makeKeyAndOrderFront(nil)
    }

    /// The first Escape clears what was typed; the next one ends the search.
    private func escape() {
        if line.query.isEmpty { onEnd() } else { line.query = "" }
    }

    /// ← and → pick along the line instead of moving the caret; ⇧⏎, ⌘P and
    /// ⌘Y act on the pick. Returns
    /// whether the key was taken. Not while an input method is composing:
    /// the arrows then belong to it.
    func takes(_ event: NSEvent, composing: Bool) -> Bool {
        guard event.type == .keyDown, !composing else { return false }
        let mods = event.modifierFlags.intersection([.command, .option, .control, .shift])
        let key = event.charactersIgnoringModifiers?.lowercased()
        if mods == .shift, event.keyCode == UInt16(kVK_Return) || event.keyCode == UInt16(kVK_ANSI_KeypadEnter) { onPlainReturn(); return true }
        if mods == .command, key == "p" { onPin(); return true }
        if mods == .command, key == "y" { onQuickLook(); return true }
        if event.keyCode == UInt16(kVK_Tab), mods.isEmpty || mods == .shift { onTab(mods == .shift ? -1 : 1); return true }
        guard mods.isEmpty else { return false }
        switch event.keyCode {
        case UInt16(kVK_LeftArrow): onMove(-1)
        case UInt16(kVK_RightArrow): onMove(1)
        case UInt16(kVK_DownArrow): onVertical(1)
        case UInt16(kVK_UpArrow): onVertical(-1)
        default: return false
        }
        return true
    }

    /// True while something borrows the keyboard and gives it back, as
    /// Quick Look does: the search waits instead of ending (review 2 #3).
    var keepsOpen: () -> Bool = { false }

    func windowDidResignKey(_ notification: Notification) {
        guard panel.isVisible, !keepsOpen() else { return }
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
        VStack(spacing: 0) {
            field.frame(height: 36)
            if line.basketOpen {
                Divider()
                BasketListView(line: line)
            }
        }
        .glassFrame(cornerRadius: 14, capsule: !line.basketOpen)
        .padding(2)
        .onAppear { focused = true }
    }

    private var field: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(.secondary)
            if let kind = line.kindFilter {
                Text(kind == .text ? L("Text") : kind == .link ? L("Links") : kind == .image ? L("Images") : L("Files"))
                    .font(.system(size: 11, weight: .semibold))
                    .padding(.horizontal, 7).padding(.vertical, 2)
                    .background(Capsule().fill(Color.accentColor.opacity(0.18)))
                    .fixedSize()
            }
            TextField(line.basketOpen ? L("Type to narrow the basket") : L("Search all history"), text: $line.query)
                .textFieldStyle(.plain)
                .font(.system(size: 14))
                .focused($focused)
                .onSubmit(onSubmit)
                .onExitCommand(perform: onEscape)
            if line.isSearching && !line.basketOpen {
                Text(String(format: L("%d of %d"), line.matchCount, line.totalCount))
                    .font(.system(size: 11, weight: .medium).monospacedDigit())
                    .foregroundStyle(.secondary)
                    .fixedSize()
            }
        }
        .padding(.horizontal, 14)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
