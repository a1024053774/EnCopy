import AppKit
import Carbon
import SwiftUI
import UniformTypeIdentifiers

// Compiled with the production views and model, without AppDelegate or
// Inbox's screenshot-settings code. All files and preferences are isolated;
// copying uses a private pasteboard. Nothing watches the user's screenshots.
enum Inbox { static var folder: URL! }

struct CheckFailed: Error { let message: String }

@MainActor final class Checks {
    let root: URL
    let pasteboard = NSPasteboard.withUniqueName()
    var passed = 0
    var window: NSWindow!

    init(root: URL) { self.root = root }

    func check(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
        guard try condition() else { throw CheckFailed(message: message) }
        passed += 1
        print("PASS \(passed): \(message)")
    }

    func pump(_ seconds: TimeInterval = 0.1) {
        RunLoop.main.run(until: Date().addingTimeInterval(seconds))
    }

    func png(_ name: String, in folder: URL? = nil, red: CGFloat = 0.3) throws -> URL {
        let url = (folder ?? root).appendingPathComponent(name)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let context = CGContext(data: nil, width: 240, height: 140, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(CGColor(red: red, green: 0.5, blue: 0.8, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 240, height: 140))
        let output = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(output, context.makeImage()!, nil)
        guard CGImageDestinationFinalize(output) else { throw CheckFailed(message: "Could not write fixture") }
        return url
    }

    func line() -> Line {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let desktop = root.appendingPathComponent("desktop", isDirectory: true)
        try! FileManager.default.createDirectory(at: desktop, withIntermediateDirectories: true)
        let line = Line(pasteboard: pasteboard, desktop: desktop)
        line.maxItems = 100
        line.visibleCount = 8
        line.revealed = true
        return line
    }

    func show(_ line: Line) {
        window?.orderOut(nil)
        window = NSWindow(contentRect: NSRect(x: 80, y: 200, width: 1100, height: 250),
                          styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.title = "EnCopy checks — synthetic images"
        window.contentView = NSHostingView(rootView: LineView(line: line))
        window.orderFront(nil)
        pump(0.7)
    }

    func grab(_ url: URL) throws -> GrabView {
        func all(_ view: NSView) -> [GrabView] {
            (view as? GrabView).map { [$0] } ?? view.subviews.flatMap(all)
        }
        guard let view = all(window.contentView!).first(where: { $0.url == url }) else {
            throw CheckFailed(message: "No native card for \(url.lastPathComponent)")
        }
        return view
    }

    func click(_ view: GrabView, shift: Bool = false, count: Int = 1) {
        let point = view.convert(NSPoint(x: view.bounds.midX, y: view.bounds.midY), to: nil)
        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: shift ? [.shift] : [],
                                          timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: view.window?.windowNumber ?? 0,
                                          context: nil, eventNumber: 1, clickCount: count, pressure: 1)!
            if type == .leftMouseDown { view.mouseDown(with: event) } else { view.mouseUp(with: event) }
        }
    }

    func fire(_ title: String, in menu: NSMenu) throws {
        guard let item = menu.items.first(where: { $0.title == title }), item.isEnabled, let action = item.action else {
            throw CheckFailed(message: "Missing enabled menu action: \(title)")
        }
        NSApp.sendAction(action, to: item.target, from: item)
        pump()
    }

    var copiedURLs: [URL] {
        (pasteboard.pasteboardItems ?? []).compactMap { $0.string(forType: .fileURL).flatMap(URL.init(string:)) }
    }

    func run() throws {
        Inbox.folder = root.appendingPathComponent("inbox", isDirectory: true)
        let a = try png("A.png"), b = try png("B.png", red: 0.7), c = try png("C.png", red: 0.9)
        let line = line()
        let aID = line.hang(a, quietly: true)!, bID = line.hang(b, quietly: true)!, cID = line.hang(c, quietly: true)!
        show(line)
        pasteboard.clearContents()
        pasteboard.setString("untouched", forType: .string)
        let before = pasteboard.changeCount
        click(try grab(c), shift: true)
        click(try grab(a), shift: true)
        pump(0.65)
        try check(line.selectedItems.map(\.id) == [aID, cID], "Shift toggles individual photos in line order and survives release")
        try check(pasteboard.changeCount == before, "Shift selection does not copy or open a long-press editor")
        click(try grab(c), shift: true)
        try check(line.selectedIDs == [aID], "Shift-clicking a selected photo removes it")
        click(try grab(c), shift: true)

        let menu = try grab(a).menuProvider()
        let titles = menu.items.map(\.title)
        try check(titles.contains("2 selected"), "Selection menu states how many photos it affects")
        try check(["Copy", "Open", "Show in Finder", "Share…", "Pin", "Save to Desktop (inbox or Desktop only)", "Take down"].allSatisfy(titles.contains),
                  "Selection menu exposes the requested batch actions")
        try check(["Quick Look", "Hold", "Markup", "Move to Trash", "Take down everything to the left"].allSatisfy { !titles.contains($0) },
                  "Single-photo editors and unrelated removal actions are absent from the selection menu")
        try check(menu.items.first { $0.title == "Save to Desktop (inbox or Desktop only)" }?.isEnabled == false, "External originals cannot be moved by Save to Desktop")
        line.clearSelection()
        line.toggleSelection(bID)
        try fire("Copy", in: menu)
        try check(copiedURLs == [a, c], "An open menu acts on its captured selection, not later changes")
        try check(pasteboard.pasteboardItems?.allSatisfy { $0.data(forType: .png) != nil } == true,
                  "Every copied photo has both PNG and file representations")
        try check(line.copiedIDs == [aID, cID], "Copy feedback covers every copied photo")
        pump(1.3)
        try check(line.copiedIDs.isEmpty, "Batch copy feedback expires")

        line.clearSelection()
        click(try grab(a))
        click(try grab(c), shift: true)
        let pending = pasteboard.changeCount
        pump(0.65)
        try check(pasteboard.changeCount == pending, "Starting selection cancels a pending copy on another card")
        click(try grab(b))
        pump(0.65)
        try check(line.selectedIDs.isEmpty && copiedURLs == [b], "An ordinary click clears selection and still copies one photo")
        // Copying B sent it to the head (T-021); copying C sends C after
        // it, so the pins below start from A, B, C again.
        click(try grab(c))
        pump(0.65)

        let gestures = GrabView(frame: NSRect(x: 0, y: 0, width: 100, height: 80))
        var singles = 0, doubles = 0, holds = 0, toggles = 0
        gestures.onClick = { singles += 1 }
        gestures.onDoubleClick = { doubles += 1 }
        gestures.onLongPress = { holds += 1 }
        gestures.onSelect = { toggles += 1 }
        click(gestures, shift: true)
        click(gestures, shift: true, count: 2)
        pump(0.65)
        try check(toggles == 2 && singles == 0 && doubles == 0 && holds == 0, "Rapid Shift clicks never copy, open or mark up")
        click(gestures)
        click(gestures, count: 2)
        pump(0.65)
        try check(doubles == 1 && singles == 0, "Ordinary double-click still opens once without copying")

        line.toggleSelection(cID)
        line.toggleSelection(aID)
        try fire("Pin", in: try grab(a).menuProvider())
        try check(line.items.filter { !$0.falling }.map(\.id) == [aID, cID, bID], "Batch pin keeps the selected photos' relative order")
        try check(!line.canDiscard([aID, cID]), "Pinned screenshots are protected from batch discard")
        let pinnedMenu = try grab(a).menuProvider()
        try check(pinnedMenu.items.first { $0.title == "Take down" }?.isEnabled == false, "Pinned selection disables Take down")
        try fire("Unpin", in: pinnedMenu)
        try check(line.items.map(\.id) == [aID, cID, bID] && line.keptCount == 0, "Batch unpin preserves relative order")
        let d = try png("D.png")
        let savedMenu = try grab(a).menuProvider()
        let dID = line.hang(d, quietly: true)!
        try fire("Take down", in: savedMenu)
        try check(line.items.filter { !$0.falling }.map(\.id) == [bID, dID], "Batch removal does not include new arrivals")
        try check(FileManager.default.fileExists(atPath: a.path) && FileManager.default.fileExists(atPath: c.path), "Taking down external photos preserves both originals")
        try check(line.selectedIDs.isEmpty, "Removed photos leave the selection immediately")
        line.putBack()
        try check(line.items.filter { !$0.falling }.map(\.url) == [a, c, b, d], "One undo restores the whole batch in its original order")
        try check(line.canPutBack, "Undoing removal leaves the earlier pin actions available")

        let restored = Line(pasteboard: pasteboard)
        try check(restored.selectedIDs.isEmpty && restored.items.map(\.url) == [a, c, b, d], "Order persists but transient selection does not")
        line.putBack()
        try check(line.items.filter(\.kept).map(\.url) == [a, c], "One undo restores the whole batch's pinned state")
        line.putBack()
        try check(line.keptCount == 0 && line.items.filter { !$0.falling }.map(\.url) == [a, b, c, d],
                  "Undoing batch pin restores positions after a later discard and undo")
        try check(!line.canPutBack, "Each batch uses one undo step")
        line.clearSelection()
        line.toggleSelection(bID)
        _ = try grab(d).menuProvider()
        try check(line.selectedIDs.isEmpty, "Right-clicking an unselected photo returns to its single-photo menu")
        line.toggleSelection(bID)
        line.setHeld(bID, true)
        line.toggleSelection(bID)
        try check(line.selectedIDs.isEmpty, "Held photos cannot remain selected or be selected through their faded trace")
        line.copy(bID)
        try check(copiedURLs == [b], "Copy from a held photo still works")

        let trashLine = self.line()
        defer { if trashLine.canPutBack { trashLine.putBack() } }
        let i1 = try png("Inbox 1.png", in: Inbox.folder), i2 = try png("Inbox 2.png", in: Inbox.folder)
        let ids = [trashLine.hang(i1, quietly: true)!, trashLine.hang(i2, quietly: true)!, trashLine.hang(a, quietly: true)!]
        ids.forEach(trashLine.toggleSelection)
        trashLine.copy(ids)
        let originalData = [try Data(contentsOf: i1), try Data(contentsOf: i2)]
        var undoActions = 0
        trashLine.onUndoableAction = { undoActions += 1 }
        trashLine.discard(ids)
        try check(undoActions == 1, "A batch acquires the undo interaction once, not again for each file")
        try check(!FileManager.default.fileExists(atPath: i1.path) && !FileManager.default.fileExists(atPath: i2.path)
                  && FileManager.default.fileExists(atPath: a.path), "Mixed removal trashes inbox copies and preserves external originals")
        try check(copiedURLs.count == 3 && copiedURLs.allSatisfy { FileManager.default.fileExists(atPath: $0.path) },
                  "Moving copied photos to Trash keeps every clipboard file URL valid")
        trashLine.putBack()
        try check(try Data(contentsOf: i1) == originalData[0] && Data(contentsOf: i2) == originalData[1], "Undo restores both trashed files byte for byte")
        try check(copiedURLs == [i1, i2, a] && trashLine.items.filter { !$0.falling }.map(\.url) == [i1, i2, a], "Undo restores batch clipboard URLs and order")

        let saveLine = self.line()
        let s1 = try png("Save 1.png", in: Inbox.folder), s2 = try png("Save 2.png", in: Inbox.folder)
        let s1ID = saveLine.hang(s1, quietly: true)!, s2ID = saveLine.hang(s2, quietly: true)!
        // Copied before A hangs: copying sends them to the head (T-021),
        // where they already are, so the line stays S1, S2, A.
        saveLine.copy([s1ID, s2ID])
        _ = saveLine.hang(a, quietly: true)
        saveLine.pin(s1ID)
        let savedBytes = [try Data(contentsOf: s1), try Data(contentsOf: s2)]
        let desktop = root.appendingPathComponent("desktop", isDirectory: true)
        let saved1 = desktop.appendingPathComponent(s1.lastPathComponent)
        let saved2 = desktop.appendingPathComponent(s2.lastPathComponent)
        // An unrelated namesake on the Desktop must never be overwritten.
        let unrelated = Data("already on Desktop".utf8)
        try unrelated.write(to: saved1)
        let renamed1 = desktop.appendingPathComponent("Save 1 2.png")
        saveLine.saveToDesktop([s1ID, s2ID])
        try check(saveLine.liveCount == 1 && copiedURLs == [renamed1, saved2], "Batch save records the actual destinations and updates clipboard URLs")
        try check(try Data(contentsOf: saved1) == unrelated, "Saving never overwrites an existing Desktop file")
        // Simulate the Desktop watcher noticing the moved screenshot.
        _ = saveLine.hang(renamed1, quietly: true)
        saveLine.putBack()
        try check(try Data(contentsOf: s1) == savedBytes[0] && Data(contentsOf: s2) == savedBytes[1],
                  "One undo moves both saved files back byte for byte")
        try check(saveLine.items.filter { !$0.falling }.map(\.url) == [s1, s2, a] && saveLine.keptCount == 1,
                  "Undoing save restores pin state and order without a duplicate Desktop card")
        try check(copiedURLs == [s1, s2] && !FileManager.default.fileExists(atPath: renamed1.path)
                  && !FileManager.default.fileExists(atPath: saved2.path), "Undoing save removes only the moved copies and repairs clipboard URLs")
        saveLine.putBack()
        try check(!saveLine.canPutBack && saveLine.keptCount == 0, "Undo history continues to the pin action before saving")

        let singleID = saveLine.items.first { $0.url == s2 && !$0.falling }!.id
        saveLine.pin(singleID)
        saveLine.saveToDesktop(singleID)
        let replacement = try png("Save 2.png", in: Inbox.folder, red: 0.1)
        let replacementBytes = try Data(contentsOf: replacement)
        saveLine.putBack()
        let restored2 = Inbox.folder.appendingPathComponent("Save 2 2.png")
        try check(try Data(contentsOf: replacement) == replacementBytes && Data(contentsOf: restored2) == savedBytes[1],
                  "Undoing a single save preserves a newer file at the original path")
        try check(saveLine.canPutBack && saveLine.liveCount == 3, "Single save also creates exactly one undo step")
        saveLine.putBack()
        try check(!saveLine.canPutBack && saveLine.keptCount == 0,
                  "Earlier pin undo follows a restored file's new name without touching its namesake")

        let noChange = self.line()
        let noChangeID = noChange.hang(a, quietly: true)!
        noChange.unpin([noChangeID])
        noChange.saveToDesktop([noChangeID])
        try check(!noChange.canPutBack, "Disabled and no-op batch actions do not create undo records")

        let mixed = self.line()
        let m1 = try png("Mixed inbox 1.png", in: Inbox.folder)
        let m2 = try png("Mixed inbox 2.png", in: Inbox.folder)
        let alreadySaved = try png("Already on Desktop.png", in: desktop)
        let mixedURLs = [m1, alreadySaved, m2, b]
        let mixedBytes = try mixedURLs.map { try Data(contentsOf: $0) }
        let mixedIDs = mixedURLs.map { mixed.hang($0, quietly: true)! }
        mixedIDs.forEach(mixed.toggleSelection)
        show(mixed)
        let mixedMenu = try grab(m1).menuProvider()
        try check(mixedMenu.items.first { $0.title == "Save to Desktop" }?.isEnabled == true,
                  "Desktop and external photos do not disable saving selected inbox photos")
        mixed.copy(mixedIDs)
        try fire("Save to Desktop", in: mixedMenu)
        let moved1 = desktop.appendingPathComponent(m1.lastPathComponent)
        let moved2 = desktop.appendingPathComponent(m2.lastPathComponent)
        let desktopCopy = desktop.appendingPathComponent("Already on Desktop 2.png")
        try check(mixed.items.filter { !$0.falling }.map(\.url) == [b]
                  && copiedURLs == [moved1, alreadySaved, moved2, b],
                  "Mixed save moves inbox files, copies Desktop files, and keeps clipboard URLs on the Desktop originals")
        try check(try Data(contentsOf: alreadySaved) == mixedBytes[1] && Data(contentsOf: b) == mixedBytes[3]
                  && Data(contentsOf: desktopCopy) == mixedBytes[1],
                  "Mixed save creates a numbered Desktop copy without changing either external original")
        mixed.putBack()
        try check(try mixedURLs.map { try Data(contentsOf: $0) } == mixedBytes
                  && mixed.items.filter { !$0.falling }.map(\.url) == mixedURLs && !mixed.canPutBack
                  && !FileManager.default.fileExists(atPath: desktopCopy.path),
                  "One undo restores the moved files and order, removes the new copy, and preserves the Desktop original")
        mixed.clearSelection()
        show(mixed)
        let savedID = mixed.items.first { $0.url == alreadySaved && !$0.falling }!.id
        mixed.toggleSelection(savedID)
        mixed.toggleSelection(mixedIDs[3])
        let savedOnlyMenu = try grab(alreadySaved).menuProvider()
        try fire("Save to Desktop", in: savedOnlyMenu)
        try check(mixed.canPutBack && copiedURLs == mixedURLs
                  && FileManager.default.fileExists(atPath: desktopCopy.path),
                  "A selection containing Desktop files can save again even without any inbox files")
        mixed.putBack()
        mixed.clearSelection()
        // A fresh host avoids outgoing cards left by the synchronous runner.
        show(mixed)
        try check(try grab(alreadySaved).menuProvider().items.first { $0.title == "Save to Desktop" }?.isEnabled == true,
                  "A single Desktop photo offers an enabled Save to Desktop action")
        try check(try grab(b).menuProvider().items.first { $0.title == "Save to Desktop (inbox or Desktop only)" }?.isEnabled == false,
                  "A photo outside the inbox and Desktop keeps its original location rule")
        try check(try grab(m1).menuProvider().items.first { $0.title == "Save to Desktop" }?.isEnabled == true,
                  "A single inbox photo still offers Save to Desktop")
        mixed.toggleSelection(mixed.items.first { $0.url == alreadySaved && !$0.falling }!.id)
        try check(try grab(alreadySaved).menuProvider().items.first { $0.title == "Save to Desktop" }?.isEnabled == true,
                  "Single-photo and selection menus both allow another Desktop copy")
        mixed.clearSelection()
        let heldInboxID = mixed.items.first { $0.url == m1 && !$0.falling }!.id
        mixed.setHeld(heldInboxID, true)
        show(mixed)
        try fire("Save to Desktop", in: try grab(m1).menuProvider())
        try check(FileManager.default.fileExists(atPath: moved1.path) && !FileManager.default.fileExists(atPath: m1.path),
                  "The existing single-photo save still works for an inbox photo held in hand")
        mixed.putBack()

        let handLine = self.line()
        _ = handLine.hangKept(a)
        let hand = handLine.items[0].id
        handLine.toggleSelection(hand)
        handLine.unpin([hand])
        try check(handLine.items[0].kept, "Photos hung by hand remain pinned when unpinning a batch")
        handLine.discard([hand])
        try check(FileManager.default.fileExists(atPath: a.path) && handLine.liveCount == 0, "Taking down a manually hung photo preserves its file")
        handLine.putBack()

        let eviction = self.line()
        eviction.maxItems = 2
        let oldest = eviction.hang(a, quietly: true)!
        _ = eviction.hang(b, quietly: true)
        eviction.toggleSelection(oldest)
        _ = eviction.hang(c, quietly: true)
        try check(eviction.selectedIDs.isEmpty && FileManager.default.fileExists(atPath: a.path), "Capacity eviction clears stale selection without deleting an external file")
        try checkDesktopCopies()
        try checkSummon()
        try checkUsedMovesToHead()
        try checkDoubleClickPastes()
        try checkPickKeys()
        try checkTabFilter()
        try checkDataFolder()
        try checkHistory()
    }

    /// A note fixture adopted into the checks' own Notes folder, so `isText`
    /// holds. It lives under the redirected home and goes away with it.
    func adoptedNote(_ name: String, _ text: String) throws -> URL {
        let dir = root.appendingPathComponent("note-src", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let src = dir.appendingPathComponent("\(name)-\(UUID().uuidString.prefix(8)).txt")
        try text.write(to: src, atomically: true, encoding: .utf8)
        guard let url = NoteFolder.adopt(src) else { throw CheckFailed(message: "Could not adopt the note fixture") }
        return url
    }

    /// ⌥C summons the line: the newest hangs picked, ← → move the pick
    /// without wrapping, ⏎ pastes it into the front app — sent only with
    /// the Accessibility permission — and a long line scrolls to keep the
    /// pick in view.
    func checkSummon() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        try check(Shortcut.current == Shortcut(keyCode: kVK_ANSI_C, modifiers: [.option], key: "c"),
                  "The one global shortcut is ⌥C")

        // A, B, C hung in order: summoning picks the newest, and the arrows
        // walk back and forth without wrapping at either end.
        let pickLine = line()
        let pa = try png("Pick A.png"), pb = try png("Pick B.png", red: 0.7), pc = try png("Pick C.png", red: 0.9)
        _ = pickLine.hang(pa, quietly: true)
        _ = pickLine.hang(pb, quietly: true)
        _ = pickLine.hang(pc, quietly: true)
        pickLine.pickNewest()
        try check(pickLine.picked?.url == pc, "Summoning picks the newest item")
        pickLine.movePick(-1)
        try check(pickLine.picked?.url == pb, "Left moves the pick one older")
        pickLine.movePick(-1)
        try check(pickLine.picked?.url == pa, "Left again reaches the oldest")
        pickLine.movePick(-1)
        try check(pickLine.picked?.url == pa, "The pick stops at the oldest, no wrap")
        pickLine.movePick(1)
        try check(pickLine.picked?.url == pb, "Right moves the pick one newer")
        pickLine.movePick(1)
        pickLine.movePick(1)
        try check(pickLine.picked?.url == pc, "The pick stops at the newest, no wrap")
        pickLine.clearPick()
        try check(pickLine.picked == nil, "Clearing leaves nothing picked")
        pickLine.movePick(1)
        try check(pickLine.picked == nil, "Moving with nothing picked picks nothing")

        // What is taken down while picked yields the newest remaining.
        pickLine.pickNewest()
        pickLine.movePick(-1)
        let pbID = pickLine.items.first { $0.url == pb }!.id
        pickLine.discard(pbID)
        try check(pickLine.picked?.url == pc, "A picked item taken down leaves the newest picked")

        // Search narrows what can be picked: the newest match stands in.
        let olderNote = try adoptedNote("older", "olderunique alphaword")
        let newerNote = try adoptedNote("newer", "newerunique betaword")
        let noteLine = line()
        _ = noteLine.hang(olderNote, quietly: true)
        _ = noteLine.hang(newerNote, quietly: true)
        noteLine.pickNewest()
        try check(noteLine.picked?.url == newerNote, "The pick starts at the newest note")
        noteLine.query = "olderunique"
        try check(noteLine.picked?.url == olderNote, "The pick follows what the search shows")
        noteLine.query = ""
        noteLine.pickNewest()

        // Trusted: the line tucks away, then exactly one ⌘V is sent.
        var order: [String] = []
        var sent: [PasteKey] = []
        var hints = 0
        let pasteLine = line()
        let qa = try png("Paste A.png"), qb = try png("Paste B.png", red: 0.7), qc = try png("Paste C.png", red: 0.9)
        _ = pasteLine.hang(qa, quietly: true)
        _ = pasteLine.hang(qb, quietly: true)
        _ = pasteLine.hang(qc, quietly: true)
        pasteLine.pickNewest()
        pasteLine.movePick(-1)
        let trusted = Paster(line: pasteLine, isTrusted: { true }, send: { key in
            sent.append(key)
            order.append("send")
        })
        trusted.tuckAway = { order.append("tuck") }
        trusted.showHint = { hints += 1; order.append("hint") }
        trusted.pastePicked()
        try check(copiedURLs == [qb], "Pasting copies the picked item")
        try check(sent == [.commandV] && sent.first?.flags == .maskCommand,
                  "A trusted paste sends exactly one ⌘V")
        try check(order == ["tuck", "send"], "The line tucks away before the key is sent")
        try check(hints == 0, "A trusted paste shows no hint")
        try check(PasteKey.commandV.strokes.count == 2
                  && PasteKey.commandV.strokes[0] == (keyDown: true, flags: .maskCommand)
                  && PasteKey.commandV.strokes[1] == (keyDown: false, flags: []),
                  "The key up carries no modifier, so ⌘ is not left held")

        // Untrusted: the note is copied, the hint shows once, no key is sent.
        let unLine = line()
        let unNote = try adoptedNote("untrusted", "untrusted paste keeps these words")
        _ = unLine.hang(unNote, quietly: true)
        unLine.pickNewest()
        var sentUntrusted: [PasteKey] = []
        var tucks = 0, untrustedHints = 0
        let untrusted = Paster(line: unLine, isTrusted: { false }, send: { sentUntrusted.append($0) })
        untrusted.tuckAway = { tucks += 1 }
        untrusted.showHint = { untrustedHints += 1 }
        untrusted.pastePicked()
        try check(pasteboard.string(forType: .string) == "untrusted paste keeps these words",
                  "An untrusted paste still copies the picked note")
        try check(sentUntrusted.isEmpty && tucks == 1 && untrustedHints == 1,
                  "Untrusted paste sends nothing and tucks away with one hint")

        // Empty: nothing happens at all.
        let emptyLine = line()
        var emptyTucks = 0, emptySends = 0, emptyHints = 0
        let empty = Paster(line: emptyLine, isTrusted: { true }, send: { _ in emptySends += 1 })
        empty.tuckAway = { emptyTucks += 1 }
        empty.showHint = { emptyHints += 1 }
        let unchanged = pasteboard.changeCount
        empty.pastePicked()
        try check(emptyTucks == 0 && emptySends == 0 && emptyHints == 0 && pasteboard.changeCount == unchanged,
                  "An empty line pastes nothing")

        // A long line scrolls to keep the pick in view.
        let scrollLine = line()
        Line.keepOnLine = 25
        scrollLine.maxItems = 25
        scrollLine.visibleCount = 8
        defer { Line.keepOnLine = nil }
        for i in 0..<12 {
            _ = scrollLine.hang(try png("Scroll \(i).png", red: CGFloat(i) / 12), quietly: true)
        }
        scrollLine.pickNewest()
        try check(scrollLine.scroll == 0, "The newest pick shows the newest end")
        for _ in 0..<8 { scrollLine.movePick(-1) }
        try check(scrollLine.scroll == Layout.spacing, "Picking older scrolls the line one place")
        for _ in 0..<8 { scrollLine.movePick(1) }
        try check(scrollLine.scroll == 0, "Picking back to the newest scrolls home")

        // ← and → belong to the pick; everything else stays with the field.
        let bar = SearchBar(line: pickLine)
        var moves: [Int] = []
        bar.onMove = { moves.append($0) }
        func keyDown(_ code: UInt16, _ flags: NSEvent.ModifierFlags = []) -> NSEvent {
            NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0,
                             windowNumber: 0, context: nil, characters: "", charactersIgnoringModifiers: "",
                             isARepeat: false, keyCode: code)!
        }
        try check(bar.takes(keyDown(UInt16(kVK_LeftArrow)), composing: false) && moves == [-1],
                  "A plain left arrow moves the pick left")
        try check(bar.takes(keyDown(UInt16(kVK_RightArrow)), composing: false) && moves == [-1, 1],
                  "A plain right arrow moves the pick right")
        moves = []
        try check(!bar.takes(keyDown(UInt16(kVK_LeftArrow), [.shift]), composing: false) && moves.isEmpty,
                  "Shift-left stays with the field")
        try check(!bar.takes(keyDown(UInt16(kVK_LeftArrow)), composing: true) && moves.isEmpty,
                  "An arrow while composing stays with the input method")
        try check(!bar.takes(keyDown(0), composing: false) && moves.isEmpty,
                  "A letter key stays with the field")
    }

    /// T-021: what is used goes to the head of the line — pasted after ⌥C,
    /// clicked to copy, copied from the menu — so the next ⌥C picks what
    /// the clipboard holds. Pinned and held stay where they are, and the
    /// new order is what a restart brings back.
    func checkUsedMovesToHead() throws {
        let use = line()
        let notes = try (0..<4).map { try adoptedNote("use\($0)", "used words \($0) headcheck\($0)") }
        for note in notes { _ = use.hang(note, quietly: true) }
        func order() -> [URL] { use.items.filter { !$0.falling }.map(\.url) }
        func id(_ url: URL) -> UUID { use.items.first { $0.url == url && !$0.falling }!.id }

        // ⌥C, ← ←, ⏎: the third newest is pasted and goes to the head.
        use.pickNewest()
        use.movePick(-1)
        use.movePick(-1)
        var sent = 0
        let paster = Paster(line: use, isTrusted: { true }, send: { _ in sent += 1 })
        paster.tuckAway = { use.clearPick() }
        paster.pastePicked()
        try check(sent == 1 && pasteboard.string(forType: .string) == "used words 1 headcheck1",
                  "⏎ pastes the picked note, the third newest")
        try check(order() == [notes[0], notes[2], notes[3], notes[1]],
                  "The pasted note goes to the head; the others keep their order")
        use.pickNewest()
        try check(use.picked?.url == notes[1] && pasteboard.string(forType: .string) == "used words 1 headcheck1",
                  "The next ⌥C picks the note just pasted, which is what the clipboard holds")
        use.clearPick()

        // A click copies one note and sends it to the head.
        show(use)
        click(try grab(notes[2]))
        pump(0.7)
        try check(pasteboard.string(forType: .string) == "used words 2 headcheck2"
                  && order() == [notes[0], notes[3], notes[1], notes[2]],
                  "A click copies the note and sends it to the head; the rest keep their order")

        // Copied together from the menu: they go to the head in line order.
        use.toggleSelection(id(notes[3]))
        use.toggleSelection(id(notes[0]))
        try fire("Copy", in: try grab(notes[0]).menuProvider())
        try check(order() == [notes[1], notes[2], notes[0], notes[3]],
                  "Copied together, they go to the head in the order they hung")
        use.clearSelection()
        let restarted = Line(pasteboard: pasteboard)
        try check(restarted.items.map(\.url) == order(),
                  "A restart brings back the order that use gave the line")

        // Pinned and held stay where they are when copied.
        use.pin(id(notes[2]))
        try check(order() == [notes[2], notes[1], notes[0], notes[3]], "The pinned note sits left of the knot")
        use.copy(id(notes[2]))
        try check(order() == [notes[2], notes[1], notes[0], notes[3]]
                  && pasteboard.string(forType: .string) == "used words 2 headcheck2",
                  "A pinned note is copied and stays left of the knot")
        use.setHeld(id(notes[1]), true)
        use.copy(id(notes[1]))
        try check(order() == [notes[2], notes[1], notes[0], notes[3]]
                  && pasteboard.string(forType: .string) == "used words 1 headcheck1",
                  "A held note is copied and keeps its empty clip")
        use.setHeld(id(notes[1]), false)
    }

    /// T-007: a double click on a card pastes it into the front app through
    /// the same paster as ⏎, and opens nothing.
    func checkDoubleClickPastes() throws {
        let dbl = line()
        let notes = try (0..<3).map { try adoptedNote("dbl\($0)", "double click words \($0)") }
        for note in notes { _ = dbl.hang(note, quietly: true) }
        var sent: [PasteKey] = []
        var tucks = 0
        let paster = Paster(line: dbl, isTrusted: { true }, send: { sent.append($0) })
        paster.tuckAway = { tucks += 1 }
        dbl.onPaste = { paster.paste($0) }
        show(dbl)
        click(try grab(notes[0]))
        click(try grab(notes[0]), count: 2)
        pump(0.7)
        try check(sent == [.commandV] && tucks == 1, "A double click sends exactly one ⌘V after tucking the line away")
        try check(pasteboard.string(forType: .string) == "double click words 0",
                  "A double click puts that card's words on the clipboard first")
        try check(dbl.items.filter { !$0.falling }.last?.url == notes[0],
                  "The double-clicked card goes to the head, like any paste")
    }

    /// T-024: while summoned, ⇧⏎ pastes the pick as plain text, ⌘P pins or
    /// unpins it, ⌘Y asks for Quick Look; other modified keys stay with the field.
    func checkPickKeys() throws {
        let keys = line()
        let dir = root.appendingPathComponent("note-src", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let rtfSrc = dir.appendingPathComponent("styled-\(UUID().uuidString.prefix(8)).rtf")
        let styled = NSAttributedString(string: "bold plain words", attributes: [.font: NSFont.boldSystemFont(ofSize: 14)])
        try styled.data(from: NSRange(location: 0, length: styled.length), documentAttributes: [.documentType: NSAttributedString.DocumentType.rtf]).write(to: rtfSrc)
        guard let rtf = NoteFolder.adopt(rtfSrc) else { throw CheckFailed(message: "Could not adopt the RTF fixture") }
        let other = try adoptedNote("other", "other words")
        _ = keys.hang(rtf, quietly: true)
        _ = keys.hang(other, quietly: true)
        var sent = 0
        let paster = Paster(line: keys, isTrusted: { true }, send: { _ in sent += 1 })
        keys.pickNewest(); keys.movePick(-1)
        paster.pastePicked()
        try check(pasteboard.data(forType: .rtf) != nil, "⏎ on a styled note keeps its styling")
        keys.pickNewest()
        try check(keys.picked?.url == rtf, "The pasted styled note is now the newest, picked first")
        paster.pastePicked(plain: true)
        try check(sent == 2 && pasteboard.data(forType: .rtf) == nil && pasteboard.string(forType: .string) == "bold plain words",
                  "⇧⏎ pastes the same words with no styling")

        let bar = SearchBar(line: keys)
        var plain = 0, pins = 0, looks = 0, moves = 0
        bar.onPlainReturn = { plain += 1 }; bar.onPin = { pins += 1 }; bar.onQuickLook = { looks += 1 }; bar.onMove = { _ in moves += 1 }
        func key(_ code: Int, _ chars: String, _ flags: NSEvent.ModifierFlags) -> NSEvent {
            NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0, windowNumber: 0, context: nil,
                             characters: chars, charactersIgnoringModifiers: chars, isARepeat: false, keyCode: UInt16(code))!
        }
        try check(bar.takes(key(kVK_Return, "\r", [.shift]), composing: false) && plain == 1, "⇧⏎ is taken as a plain paste")
        try check(bar.takes(key(kVK_ANSI_P, "p", [.command]), composing: false) && pins == 1, "⌘P is taken as pin")
        try check(bar.takes(key(kVK_ANSI_Y, "y", [.command]), composing: false) && looks == 1, "⌘Y is taken as Quick Look")
        try check(!bar.takes(key(kVK_Return, "\r", []), composing: false) && plain == 1, "A plain ⏎ still goes to the field's submit")
        try check(!bar.takes(key(kVK_ANSI_P, "p", [.command]), composing: true) && pins == 1, "⌘P while composing stays with the input method")
        try check(!bar.takes(key(kVK_ANSI_A, "a", [.command]), composing: false), "⌘A stays with the field")

        let id = keys.items.first { $0.url == other }!.id
        keys.togglePin(id)
        try check(keys.items.first { $0.id == id }?.kept == true && keys.items.first?.id == id, "⌘P pins the pick left of the knot")
        keys.togglePin(id)
        try check(keys.items.first { $0.id == id }?.kept == false, "⌘P again unpins it")
    }

    /// T-025: Tab while summoned narrows the line to one kind and cycles
    /// all → text → links → images → all; ⇧Tab goes back.
    func checkTabFilter() throws {
        let tab = line()
        let text = try adoptedNote("tabtext", "plain words for tab")
        let src = root.appendingPathComponent("note-src/link-\(UUID().uuidString.prefix(8)).txt")
        try "https://example.com/tab".write(to: src, atomically: true, encoding: .utf8)
        guard let link = NoteFolder.adopt(src) else { throw CheckFailed(message: "Could not adopt the link fixture") }
        let photo = try png("Tab photo.png")
        for url in [text, link, photo] { _ = tab.hang(url, quietly: true) }
        let kinds = [tab.kind(of: text), tab.kind(of: link), tab.kind(of: photo)]
        try check(kinds == [.text, .link, .image], "Text, a link and a photo are told apart")
        let bar = SearchBar(line: tab)
        bar.onTab = { tab.cycleKind($0); tab.pickNewest() }
        func tabKey(_ flags: NSEvent.ModifierFlags) -> NSEvent {
            NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0, windowNumber: 0, context: nil,
                             characters: "\t", charactersIgnoringModifiers: "\t", isARepeat: false, keyCode: UInt16(kVK_Tab))!
        }
        func shownURLs() -> [URL] { tab.shown.filter { !$0.falling }.map(\.url) }
        tab.pickNewest()
        try check(shownURLs() == [text, link, photo], "With no filter everything shows")
        try check(bar.takes(tabKey([]), composing: false) && shownURLs() == [text] && tab.picked?.url == text, "Tab shows only text and picks it")
        _ = bar.takes(tabKey([]), composing: false)
        try check(shownURLs() == [link] && tab.picked?.url == link, "Tab again shows only links")
        _ = bar.takes(tabKey([]), composing: false)
        try check(shownURLs() == [photo], "Tab again shows only images")
        _ = bar.takes(tabKey([]), composing: false)
        try check(tab.kindFilter == nil && shownURLs().count == 3, "Tab again shows everything")
        _ = bar.takes(tabKey([.shift]), composing: false)
        try check(tab.kindFilter == .image, "⇧Tab goes back to images")
        tab.kindFilter = .text
        tab.query = "tab"
        try check(shownURLs() == [text], "A search inside the text filter shows only matching text")
        try check(!bar.takes(tabKey([]), composing: true), "Tab while composing stays with the input method")
    }

    func checkDesktopCopies() throws {
        let fm = FileManager.default
        let desktop = root.appendingPathComponent("desktop", isDirectory: true)
        let original = try png("Repeat.png", in: desktop)
        let namesake = try png("Repeat 2.png", in: desktop, red: 0.9)
        let bytes = try Data(contentsOf: original)
        let namesakeBytes = try Data(contentsOf: namesake)
        let copy = desktop.appendingPathComponent("Repeat 3.png")
        let nextCopy = desktop.appendingPathComponent("Repeat 4.png")
        let line = self.line()
        let id = line.hang(original, quietly: true)!
        line.pin(id)
        line.copy(id)
        show(line)
        try fire("Save to Desktop", in: try grab(original).menuProvider())
        try check(try Data(contentsOf: copy) == bytes && Data(contentsOf: original) == bytes
                  && Data(contentsOf: namesake) == namesakeBytes,
                  "Single save creates the next available numbered copy without overwriting Desktop originals")
        try check(line.liveCount == 0 && line.isDesktopSave(copy) && copiedURLs == [original],
                  "A copied Desktop photo leaves the line, suppresses re-hang, and keeps the original clipboard URL")
        // A user may hang the copy by hand before undoing its creation.
        let copyID = line.hang(copy, quietly: true)!
        line.copy(copyID)
        line.putBack()
        try check(!fm.fileExists(atPath: copy.path) && line.liveCount == 1 && line.keptCount == 1
                  && line.items.first(where: { !$0.falling })?.url == original,
                  "Undo removes only the new copy and its card, restoring the original photo and pin state")
        try check(try copiedURLs.count == 1 && copiedURLs[0] != original && Data(contentsOf: copiedURLs[0]) == bytes,
                  "An undone copy remains recoverable in Trash and its copied file URL stays valid")
        line.putBack()
        try check(!line.canPutBack && line.keptCount == 0, "Undo continues to an earlier pin after undoing a Desktop copy")

        let firstID = line.items.first { !$0.falling }!.id
        line.saveToDesktop(firstID)
        let secondID = line.hang(original, quietly: true)!
        line.saveToDesktop(secondID)
        try check(try Data(contentsOf: copy) == bytes && Data(contentsOf: nextCopy) == bytes,
                  "Saving the same Desktop original again allocates another free numbered filename")
        line.putBack()
        try check(fm.fileExists(atPath: copy.path) && !fm.fileExists(atPath: nextCopy.path),
                  "One undo removes only the most recent copy")
        line.putBack()
        try check(!fm.fileExists(atPath: copy.path) && line.liveCount == 1 && !line.canPutBack,
                  "A second undo removes the earlier copy without duplicating an original already back on the line")

        line.saveToDesktop(line.items.first { !$0.falling }!.id)
        let parked = root.appendingPathComponent("Moved Desktop copy.png")
        try fm.moveItem(at: copy, to: parked)
        let replacement = try png(copy.lastPathComponent, in: desktop, red: 0.1)
        let replacementBytes = try Data(contentsOf: replacement)
        _ = line.hang(replacement, quietly: true)
        line.putBack()
        try check(try Data(contentsOf: replacement) == replacementBytes && Data(contentsOf: parked) == bytes
                  && Data(contentsOf: original) == bytes && line.liveCount == 2,
                  "Undo preserves a different file now using the copy's name and the copy moved elsewhere")

        line.saveToDesktop(line.items.first { $0.url == original && !$0.falling }!.id)
        let changedOriginal = try png(original.lastPathComponent, in: desktop, red: 0.6)
        let changedBytes = try Data(contentsOf: changedOriginal)
        line.putBack()
        try check(try Data(contentsOf: original) == changedBytes && !fm.fileExists(atPath: nextCopy.path)
                  && Data(contentsOf: replacement) == replacementBytes,
                  "Undo never overwrites later edits to the Desktop original or touches a namesake")
    }

    /// Exercise real directory events, delayed file checks, and overlapping
    /// watchers. The earlier menu-only checks could not catch a saved image
    /// being picked up again by the Desktop safety watcher.
    func checkDesktopWatchers() async throws {
        // Folder listings can use /private/var where the test root uses
        // /var. Compare standardized URLs, as the production gate does.
        func canonical(_ url: URL) -> URL { url.standardizedFileURL }
        let fm = FileManager.default
        let desktop = root.appendingPathComponent("watched-desktop", isDirectory: true)
        try fm.createDirectory(at: desktop, withIntermediateDirectories: true)
        UserDefaults.standard.removeObject(forKey: "pegged")
        let line = Line(pasteboard: pasteboard, desktop: desktop)
        line.maxItems = 100
        let first = try png("Watched 1.png", in: Inbox.folder)
        let second = try png("Watched 2.png", in: Inbox.folder)
        let bytes = try [first, second].map { try Data(contentsOf: $0) }
        let ids = [line.hang(first, quietly: true)!, line.hang(second, quietly: true)!]
        var reports: [URL] = []
        var changes = 0
        let watchers = (0..<2).map { _ in
            ScreenshotWatcher(folder: desktop, anyImage: true, onNew: { url in
                reports.append(url)
                // The same gate used by AppDelegate.hangCapture, before
                // either hanging the photo or starting its flight.
                guard !line.isDesktopSave(url) else { return }
                _ = line.hang(url, quietly: true)
            }, onChange: { changes += 1; line.prune() })
        }
        defer { watchers.forEach { $0.stop() } }
        watchers.forEach { $0.start() }
        try await Task.sleep(nanoseconds: 800_000_000)
        line.saveToDesktop(ids)
        let targets = [first, second].map { desktop.appendingPathComponent($0.lastPathComponent) }
        let deadline = Date().addingTimeInterval(5)
        while reports.count < 4 && Date() < deadline { try await Task.sleep(nanoseconds: 100_000_000) }
        try check(targets.allSatisfy { target in reports.filter { canonical($0) == canonical(target) }.count >= 2 },
                  "Both live folder watchers observe both exported screenshots")
        try check(line.liveCount == 0 && targets.allSatisfy(line.isDesktopSave),
                  "Saved screenshots stay off the line after asynchronous and overlapping watcher callbacks")
        try check(try targets.map { try Data(contentsOf: $0) } == bytes,
                  "Suppressing re-hang preserves both saved files byte for byte")

        let newCapture = try png("New capture.png", in: desktop)
        let newDeadline = Date().addingTimeInterval(5)
        while line.liveCount == 0 && Date() < newDeadline { try await Task.sleep(nanoseconds: 100_000_000) }
        try check(line.items.filter { !$0.falling }.map { canonical($0.url) } == [canonical(newCapture)],
                  "A genuine new image is still picked up exactly once by overlapping watchers")
        line.putBack()
        try await Task.sleep(nanoseconds: 800_000_000)
        try check(try [first, second].map { try Data(contentsOf: $0) } == bytes,
                  "Undo after watcher callbacks restores the exported files byte for byte")
        try check(line.items.filter { !$0.falling }.map { canonical($0.url) } == [first, second, newCapture].map(canonical)
                  && targets.allSatisfy { !line.isDesktopSave($0) },
                  "Undo clears export suppression and restores the original order beside new arrivals")

        let firstID = line.items.first { canonical($0.url) == canonical(first) && !$0.falling }!.id
        line.saveToDesktop(firstID)
        try await Task.sleep(nanoseconds: 800_000_000)
        let changeCount = changes
        let parked = root.appendingPathComponent("Previously exported.png")
        try fm.moveItem(at: targets[0], to: parked)
        let removedDeadline = Date().addingTimeInterval(5)
        while changes == changeCount && Date() < removedDeadline { try await Task.sleep(nanoseconds: 100_000_000) }
        try await Task.sleep(nanoseconds: 400_000_000)
        let replacement = try png(first.lastPathComponent, in: desktop, red: 0.9)
        let replacedDeadline = Date().addingTimeInterval(5)
        while !line.items.contains(where: { canonical($0.url) == canonical(replacement) && !$0.falling }) && Date() < replacedDeadline { try await Task.sleep(nanoseconds: 100_000_000) }
        try check(line.items.contains { canonical($0.url) == canonical(replacement) && !$0.falling } && !line.isDesktopSave(replacement),
                  "A different file reusing an exported filename is accepted as a new image")
        try check(try Data(contentsOf: parked) == bytes[0],
                  "A later namesake never changes the earlier exported file")

        let secondID = line.items.first { canonical($0.url) == canonical(second) && !$0.falling }!.id
        line.saveToDesktop(secondID)
        let newID = line.items.first { canonical($0.url) == canonical(newCapture) && !$0.falling }!.id
        for _ in 0..<11 { line.pin(newID); line.unpin(newID) }
        try await Task.sleep(nanoseconds: 800_000_000)
        try check(line.isDesktopSave(targets[1]) && !line.items.contains { canonical($0.url) == canonical(targets[1]) && !$0.falling },
                  "Saved files remain suppressed after their bounded undo history expires")

        let originalBytes = try Data(contentsOf: newCapture)
        let desktopCopy = desktop.appendingPathComponent("New capture 2.png")
        line.saveToDesktop(newID)
        let copiedDeadline = Date().addingTimeInterval(5)
        while reports.filter({ canonical($0) == canonical(desktopCopy) }).count < 2 && Date() < copiedDeadline {
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        try check(reports.filter { canonical($0) == canonical(desktopCopy) }.count >= 2
                  && !line.items.contains { canonical($0.url) == canonical(desktopCopy) && !$0.falling }
                  && line.isDesktopSave(desktopCopy),
                  "Both watchers observe a numbered Desktop copy without hanging it again")
        line.putBack()
        try await Task.sleep(nanoseconds: 600_000_000)
        try check(try Data(contentsOf: newCapture) == originalBytes && !fm.fileExists(atPath: desktopCopy.path)
                  && line.items.filter { canonical($0.url) == canonical(newCapture) && !$0.falling }.count == 1,
                  "Undo after copy watcher callbacks preserves the original and restores exactly one card")
    }

    /// The data folder is named after the app, so the development copy's
    /// files can never land in the everyday copy's folder.
    func checkDataFolder() throws {
        let expected = URL(fileURLWithPath: NSHomeDirectory())
            .appendingPathComponent("Library", isDirectory: true)
            .appendingPathComponent("Application Support", isDirectory: true)
            .appendingPathComponent("EnCopy Checks", isDirectory: true)
        try check(DataFolder.url.standardizedFileURL.path == expected.standardizedFileURL.path,
                  "The data folder is in Application Support, named after the app")
        try check(NoteFolder.url.standardizedFileURL.path
                  == DataFolder.url.appendingPathComponent("Notes", isDirectory: true).standardizedFileURL.path,
                  "Notes are kept in the data folder")
    }

    /// Copied text, links and code: recorded out of the box, merged when
    /// the same words come again, kept in a history that outlives the
    /// line's length, and still found by the search after a restart.
    func checkHistory() throws {
        // Each scenario gets its own defaults, history file and line, so
        // nothing one check leaves can be mistaken for another's result.
        func freshLine(_ name: String, maxItems: Int = 100) -> Line {
            UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
            UserDefaults.standard.set(true, forKey: "soundOff")
            let desktop = root.appendingPathComponent("desktop-\(name)", isDirectory: true)
            try! FileManager.default.createDirectory(at: desktop, withIntermediateDirectories: true)
            let line = Line(pasteboard: pasteboard, desktop: desktop,
                            history: History(file: root.appendingPathComponent("\(name).sqlite")))
            line.maxItems = maxItems
            line.visibleCount = 8
            line.revealed = true
            return line
        }
        func watcher(for line: Line,
                     secure: @escaping () -> Bool = { false },
                     onNew: ((URL) -> Void)? = nil,
                     onAgain: ((URL) -> Void)? = nil) -> ClipboardWatcher {
            ClipboardWatcher(pasteboard: pasteboard, history: line.history, isSecureInput: secure,
                             onNew: onNew ?? { url in _ = line.hang(url, quietly: true) },
                             onAgain: onAgain ?? { url in _ = line.rehang(url) })
        }
        func copyText(_ text: String, types: [NSPasteboard.PasteboardType] = []) {
            let item = NSPasteboardItem()
            item.setString(text, forType: .string)
            for type in types { item.setData(Data(), forType: type) }
            pasteboard.clearContents()
            pasteboard.writeObjects([item])
        }
        defer { ClipboardWatcher.isPaused = false }

        // 1. Out of the box: text and links are recorded, images still ask.
        let first = freshLine("first")
        try check(!ClipboardWatcher.imagesEnabled && ClipboardWatcher.textEnabled
                  && ClipboardWatcher.linksEnabled && !ClipboardWatcher.isPaused,
                  "Text and links are recorded out of the box; images still ask first")
        let firstWatcher = watcher(for: first)
        copyText("basket baseline words")
        firstWatcher.check()
        try check(first.liveCount == 1 && first.items[0].url.pathExtension == "txt",
                  "A copied text hangs as a note")
        try check(try String(contentsOf: first.items[0].url, encoding: .utf8) == "basket baseline words",
                  "The note file holds exactly the copied text")
        copyText("https://example.com/some/page")
        firstWatcher.check()
        try check(first.items.contains { Note.cached($0.url)?.link != nil },
                  "A copied link hangs as a link")
        try check(first.history.count == 2, "Both copies are in the history")

        // 2. What must never be kept, each with proof the same words
        //    without the marker are recorded.
        func excluded(_ label: String, secure: Bool = false,
                      types: [NSPasteboard.PasteboardType] = []) throws {
            let line = freshLine(label)
            let words = "hidden \(label) words"
            let blocked = watcher(for: line, secure: { secure })
            copyText(words, types: types)
            blocked.check()
            try check(line.liveCount == 0 && line.history.count == 0,
                      "\(label): nothing is recorded")
            let allowed = watcher(for: line)
            copyText(words)
            allowed.check()
            try check(line.liveCount == 1 && line.history.count == 1,
                      "\(label): the same words without the marker are recorded")
        }
        try excluded("secure input", secure: true)
        try excluded("concealed copies", types: [.init("org.nspasteboard.ConcealedType")])
        try excluded("transient copies", types: [.init("org.nspasteboard.TransientType")])
        try excluded("remote clipboard", types: [.init("com.apple.is-remote-clipboard")])

        // 3. Paused: nothing is recorded until it is resumed.
        let pause = freshLine("pause")
        let pauseWatcher = watcher(for: pause)
        ClipboardWatcher.isPaused = true
        copyText("while recording is paused")
        pauseWatcher.check()
        try check(!ClipboardWatcher.isEnabled && pause.liveCount == 0 && pause.history.count == 0,
                  "Paused: the watcher is off and nothing is recorded")
        ClipboardWatcher.isPaused = false
        copyText("after recording resumes")
        pauseWatcher.check()
        try check(ClipboardWatcher.isEnabled && pause.liveCount == 1,
                  "Resumed: the next copy is recorded")

        // 4. A full line drops text into the basket, not the Trash.
        let basket = freshLine("basket", maxItems: 3)
        var saved: [URL] = []
        let basketWatcher = watcher(for: basket, onNew: { url in
            saved.append(url)
            _ = basket.hang(url, quietly: true)
        })
        for n in 1...5 {
            copyText("basket words \(n) tail\(n)")
            basketWatcher.check()
        }
        try check(basket.liveCount == 3, "Three texts hang on a line of three")
        try check(saved.count == 5 && saved.allSatisfy { FileManager.default.fileExists(atPath: $0.path) },
                  "All five note files still exist, none went to the Trash")
        try check(basket.history.count == 5, "The history keeps all five, basket included")
        let mode = try FileManager.default.attributesOfItem(atPath: root.appendingPathComponent("basket.sqlite").path)[.posixPermissions] as? NSNumber
        try check(mode?.intValue == 0o600, "The history file is private to the user")

        // 5. The same words again add nothing: the kept item comes back.
        copyText("basket words 1 tail1")
        basketWatcher.check()
        try check(basket.history.count == 5 && saved.count == 5,
                  "Copying a basket item's words again adds no row and no file")
        try check(basket.items.last?.url == saved[0] && basket.liveCount == 3,
                  "The basket item comes back as the newest on the line")
        copyText("basket words 4 tail4")
        basketWatcher.check()
        try check(basket.items.last?.url == saved[3] && basket.history.count == 5,
                  "Copying a line item's words again makes it newest, nothing added")
        copyText("BASKET WORDS 4 TAIL4")
        basketWatcher.check()
        try check(basket.history.count == 6, "The same words in a different case are a new item")

        // 6. The search finds the basket; Return pastes the pick.
        let seek = freshLine("seek", maxItems: 3)
        var seekSaved: [URL] = []
        let seekWatcher = watcher(for: seek, onNew: { url in
            seekSaved.append(url)
            _ = seek.hang(url, quietly: true)
        })
        let seekTexts = [
            "shared basket maple 一丁",
            "quantum notebook violet",
            "shared line item",
            "plain text here",
            "another normal note",
        ]
        for text in seekTexts {
            copyText(text)
            seekWatcher.check()
        }
        // The card that just fell off finishes falling, so the search
        // below sees only what hangs and what is in the basket.
        pump(0.7)
        seek.closeGaps()
        seek.query = "maple"
        try check(seek.shown.map(\.url) == [seekSaved[0]] && seek.basketCards.count == 1,
                  "A basket item's words show exactly its card")
        seek.query = "一丁"
        try check(seek.basketCards.map(\.url) == [seekSaved[0]],
                  "A two-character search finds its basket item")
        seek.query = "shared"
        try check(seek.basketCards.map(\.url) == [seekSaved[0]]
                  && seek.shown.map(\.url) == [seekSaved[0], seekSaved[2]]
                  && seek.matchCount == 2,
                  "A match in the basket hangs left of the one on the line")
        try check(seek.totalCount == 5, "The total counts what hangs and what is in the basket")
        seek.query = "ＭＡＰＬＥ"
        try check(seek.basketCards.map(\.url) == [seekSaved[0]],
                  "A full-width query finds the same basket item")
        seek.query = "maple"
        seek.pickNewest()
        var sent: [PasteKey] = []
        var order: [String] = []
        let trusted = Paster(line: seek, isTrusted: { true }, send: { key in
            sent.append(key)
            order.append("send")
        })
        trusted.tuckAway = { order.append("tuck") }
        trusted.pastePicked()
        try check(pasteboard.string(forType: .string) == seekTexts[0],
                  "Pasting the basket card copies exactly its text")
        try check(sent == [.commandV] && order == ["tuck", "send"],
                  "The picked basket card pastes with one ⌘V after the line tucks")
        try check(seek.items.last?.url == seekSaved[0] && seek.liveCount == 3,
                  "After pasting, the item hangs on the line as the newest")

        // 7. Taking a basket card down trashes file and row; Put back
        //    returns both, and the search finds it again.
        let down = freshLine("down", maxItems: 2)
        var downSaved: [URL] = []
        let downWatcher = watcher(for: down, onNew: { url in
            downSaved.append(url)
            _ = down.hang(url, quietly: true)
        })
        for n in 1...3 {
            copyText("take down words \(n) uniq\(n)")
            downWatcher.check()
        }
        down.query = "uniq1"
        try check(down.basketCards.map(\.url) == [downSaved[0]], "The basket card is found")
        down.pressCorner(down.basketCards[0].id)
        pump(0.7)
        down.closeGaps()
        try check(!FileManager.default.fileExists(atPath: downSaved[0].path) && down.history.count == 2,
                  "Taking the basket card down trashes its file and its row")
        try check(down.basketCards.isEmpty && !down.shown.contains { $0.url == downSaved[0] },
                  "The search no longer shows it")
        down.putBack()
        try check(FileManager.default.fileExists(atPath: downSaved[0].path) && down.history.count == 3,
                  "Put back restores the file and its row")
        try check(down.shown.contains { $0.url == downSaved[0] }, "The search finds it again")
        // Taking a line text down while not searching leaves no row
        // either: with no search running, nothing else would sweep it.
        down.query = ""
        copyText("line trash words gone")
        downWatcher.check()
        let doomed = down.items.first { $0.url == downSaved[3] }!
        down.pressCorner(doomed.id)
        try check(!FileManager.default.fileExists(atPath: downSaved[3].path) && down.history.count == 3,
                  "Taking a line text down trashes its file and its row too")

        // 8. Reopen: the same files hang and the basket is still found.
        let reopen = freshLine("reopen", maxItems: 2)
        var reopenSaved: [URL] = []
        let reopenWatcher = watcher(for: reopen, onNew: { url in
            reopenSaved.append(url)
            _ = reopen.hang(url, quietly: true)
        })
        for n in 1...3 {
            copyText("reopen words \(n) mark\(n)")
            reopenWatcher.check()
        }
        // A second connection to the same file and a line built without
        // wiping defaults, as after quitting and reopening.
        let reopened = Line(pasteboard: pasteboard,
                            desktop: root.appendingPathComponent("desktop-reopen2", isDirectory: true),
                            history: History(file: root.appendingPathComponent("reopen.sqlite")))
        reopened.maxItems = 2
        reopened.visibleCount = 8
        try check(reopened.items.filter { !$0.falling }.map(\.url)
                  == reopen.items.filter { !$0.falling }.map(\.url),
                  "A reopened line hangs the same items")
        reopened.query = "mark1"
        try check(reopened.basketCards.map(\.url) == [reopenSaved[0]],
                  "A reopened history still finds the basket item")
        try check(reopened.history.count == 3, "The reopened history kept its rows")
        reopened.query = "mark3"
        try check(reopened.shown.contains { $0.url == reopenSaved[2] },
                  "A line note is found by its words after reopening")

        // 9. Scale: two hundred texts, the line holds eight, the history all.
        let scale = freshLine("scale", maxItems: 8)
        var scaleSaved: [URL] = []
        let scaleWatcher = watcher(for: scale, onNew: { url in
            scaleSaved.append(url)
            _ = scale.hang(url, quietly: true)
        })
        func chineseTag(_ n: Int) -> String {
            let digits = String(format: "%03d", n)
            let kanji: [Character: Character] = ["0": "零", "1": "一", "2": "二", "3": "三", "4": "四",
                                                 "5": "五", "6": "六", "7": "七", "8": "八", "9": "九"]
            return String(digits.map { kanji[$0]! })
        }
        // Two hundred texts of the same shape, each with a tag of its own:
        // the 150th is found among the alike, not by a mark only it has.
        var scaleTexts: [String] = []
        for n in 1...200 {
            let text = "第\(n)条 大家都有的字 标\(chineseTag(n))号"
            scaleTexts.append(text)
            copyText(text)
            scaleWatcher.check()
        }
        try check(scale.history.count == 200, "Two hundred texts are all in the history")
        let url150 = scaleSaved[149]
        pump(0.7)
        scale.closeGaps()
        scale.query = "一五零"
        try check(scale.basketCards.map(\.url) == [url150],
                  "The three-character tag finds only item 150 in the basket")
        try check(try String(contentsOf: url150, encoding: .utf8) == scaleTexts[149],
                  "Item 150's file holds exactly the text that was copied")
        scale.query = "一五零号"
        try check(scale.basketCards.map(\.url) == [url150],
                  "The four-character tag finds only item 150 in the basket")
        try check(try String(contentsOf: url150, encoding: .utf8) == scaleTexts[149],
                  "Item 150's file still holds exactly the copied text")
        let scaleReopened = Line(pasteboard: pasteboard,
                                 desktop: root.appendingPathComponent("desktop-scale2", isDirectory: true),
                                 history: History(file: root.appendingPathComponent("scale.sqlite")))
        scaleReopened.maxItems = 8
        scaleReopened.visibleCount = 8
        scaleReopened.query = "一五零"
        try check(scaleReopened.basketCards.map(\.url) == [url150],
                  "After reopening, the same search finds only item 150")
        try check(try String(contentsOf: url150, encoding: .utf8) == scaleTexts[149],
                  "Item 150's file is unchanged after reopening")
        scaleReopened.query = "一五零号"
        try check(scaleReopened.basketCards.map(\.url) == [url150],
                  "After reopening, the four-character tag finds only item 150")
        copyText(scaleTexts[36])
        scaleWatcher.check()
        try check(scale.history.count == 200, "Copying item 37's text again leaves two hundred")

        // 10. A basket card on screen only comes back: it does not drag,
        //     its menu is Copy and Discard, and a click copies it onto the
        //     line's newest end.
        let peek = freshLine("peek", maxItems: 1)
        var peekSaved: [URL] = []
        let peekWatcher = watcher(for: peek, onNew: { url in
            peekSaved.append(url)
            _ = peek.hang(url, quietly: true)
        })
        let peekTexts = ["basket card words alpha", "the one still hanging"]
        for text in peekTexts {
            copyText(text)
            peekWatcher.check()
        }
        pump(0.7)
        peek.closeGaps()
        peek.query = "alpha"
        show(peek)
        // A basket card's view has no url: it is the only card without one.
        func basketCard() throws -> GrabView {
            func all(_ sub: NSView) -> [GrabView] {
                (sub as? GrabView).map { [$0] } ?? sub.subviews.flatMap(all)
            }
            guard let card = all(window.contentView!).first(where: { $0.url == nil }) else {
                throw CheckFailed(message: "No basket card on screen")
            }
            return card
        }
        let card = try basketCard()
        try check(card.url == nil, "A basket card does not drag")
        try check(card.menuProvider().items.map(\.title) == ["Copy", "Discard"],
                  "A basket card's menu is only Copy and Discard")
        click(card)
        pump(0.7)
        try check(pasteboard.string(forType: .string) == peekTexts[0],
                  "Clicking the basket card copies exactly its text")
        try check(peek.items.last?.url == peekSaved[0] && peek.liveCount == 1,
                  "The basket card hangs on the line as the newest")
    }

    func cleanUp() {
        window?.orderOut(nil)
        pasteboard.releaseGlobally()
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
    }
}

@main struct RunChecks {
    @MainActor static func main() {
        guard Bundle.main.bundleIdentifier == "local.encopy.checks", CommandLine.arguments.count == 2,
              NSHomeDirectory() != String(cString: getpwuid(getuid())!.pointee.pw_dir) else {
            fatalError("Run with scripts/check.sh")
        }
        _ = NSApplication.shared
        NSApp.setActivationPolicy(.accessory)
        let checks = Checks(root: URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true))
        defer { checks.cleanUp() }
        do { try checks.run() }
        catch {
            print("FAIL: \(error)")
            checks.cleanUp()
            exit(1)
        }
        // Watcher callbacks arrive on DispatchQueue.main. Yield between
        // checks inside the application loop so delivery does not depend
        // on reentrant RunLoop draining in a synchronous main-actor call.
        Task { @MainActor in
            do {
                try await checks.checkDesktopWatchers()
                print("\(checks.passed) checks passed")
                checks.cleanUp()
                exit(0)
            } catch {
                print("FAIL: \(error)")
                checks.cleanUp()
                exit(1)
            }
        }
        NSApp.run()
    }
}
