import AppKit
import AVFoundation
import SQLite3
import Security
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

    /// A one-frame movie a thumbnail can be read from: four bytes named
    /// .mov get no first frame, so they never hang.
    func movie(_ name: String, in folder: URL? = nil) throws -> URL {
        let url = (folder ?? root).appendingPathComponent(name)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? FileManager.default.removeItem(at: url)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: 64, AVVideoHeightKey: 48])
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32ARGB, kCVPixelBufferWidthKey as String: 64,
            kCVPixelBufferHeightKey as String: 48])
        writer.add(input)
        guard writer.startWriting() else { throw CheckFailed(message: "Could not start the movie fixture") }
        writer.startSession(atSourceTime: .zero)
        var buffer: CVPixelBuffer?
        CVPixelBufferCreate(nil, 64, 48, kCVPixelFormatType_32ARGB, nil, &buffer)
        guard let buffer else { throw CheckFailed(message: "Could not make the movie frame") }
        while !input.isReadyForMoreMediaData { pump(0.01) }
        adaptor.append(buffer, withPresentationTime: .zero)
        input.markAsFinished()
        writer.endSession(atSourceTime: CMTime(value: 1, timescale: 30))
        var done = false
        writer.finishWriting { done = true }
        while !done { pump(0.01) }
        guard writer.status == .completed else { throw CheckFailed(message: "Could not write the movie fixture") }
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
        // ENCOPY_CHECKS_SCREEN names a screen (a virtual one, say) to open
        // on, out of the way of whoever is using the Mac meanwhile.
        let named = ProcessInfo.processInfo.environment["ENCOPY_CHECKS_SCREEN"]
        let screen = named.flatMap { name in NSScreen.screens.first { $0.localizedName == name } }
        let origin = screen.map { NSPoint(x: $0.frame.minX + 80, y: $0.frame.minY + 200) } ?? NSPoint(x: 80, y: 200)
        window = NSWindow(contentRect: NSRect(origin: origin, size: NSSize(width: 1100, height: 250)),
                          styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.title = "EnCopy checks — synthetic images"
        window.contentView = NSHostingView(rootView: LineView(line: line))
        // Placed again once built: the frame given at creation does not
        // always carry over to another screen.
        window.setFrameOrigin(origin)
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

    /// The same files, whichever way the temporary folder is spelled
    /// (/var or /private/var).
    func samePaths(_ a: [URL], _ b: [URL]) -> Bool {
        a.map { $0.resolvingSymlinksInPath().path } == b.map { $0.resolvingSymlinksInPath().path }
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
        try checkSourceApp()
        try checkAppRules()
        try checkVerificationCodes()
        try checkFiles()
        try checkImagesInBasket()
        try checkRetention()
        try checkCleanup()
        try checkBasketList()
        try checkBasketPapers()
        try checkBasketKeys()
        try checkKindChips()
        try checkFilesInBasket()
        try checkFileStack()
        try checkRetentionStatus()
        try checkNewestByKnot()
        try checkMotion()
        try checkMenuBarIcon()
        try checkMotionRest()
        try checkDataFolder()
        try checkHistory()
        try checkTakeDownSides()
        try checkCodeScope()
        try checkPutBackKeepsBatch()
        try checkDragOperations()
        try checkMarkupOnlyPictures()
        try checkPutBackNotNew()
        try checkCleanupAtClearTime()
        try checkCopiedImageApp()
        try checkFilesRespectImages()
        try checkRecopyComesBack()
        try checkBasketListSearch()
        try checkKindBeforeLimit()
        try checkKeptImageSearchOnLine()
        try checkQuickLookOrder()
        try checkShrinkOffMain()
        try checkPeekDuration()
        try checkSourceAppBadge()
        try checkThemes()
        try checkWhatHangs()
        try checkRecopyNeverShortens()
        try checkSearchStaysForQuickLook()
        try checkCleanupConfirmFresh()
        try checkShrinkRespectsClearing()
        try checkFinderPicturesWithoutFiles()
        try checkPutBackKeepsApp()
        try checkQuickLookWithFallingPin()
        try checkGroupDragOperations()
        try checkEditOnlyOwnPictures()
        try checkSearchBeforeLimit()
        try checkDiscardedCodesNotInTrash()
        try checkCopiedFilesClearToo()
        try checkCleanupPreviewOnDisk()
        try checkFinderPicturesByName()
        try checkTakeDownOnlyShown()
        try checkRecopyAnyPicture()
        try checkUpdater()
    }

    /// T-053: what the updater accepts and refuses, without the network.
    /// Each refusal leaves the app in place as it was.
    func checkUpdater() throws {
        try check(Updater.isNewer("0.10.0", than: "0.9.0") && Updater.isNewer("v0.4.2", than: "0.4.1")
                  && !Updater.isNewer("0.4", than: "0.4.0") && !Updater.isNewer("0.4.0", than: "v0.4")
                  && !Updater.isNewer("0.4.1", than: "0.4.2") && Updater.isNewer("1.0", than: "0.99.99"),
                  "Versions compare number by number; 0.4 is 0.4.0 and a leading v is ignored")

        func feed(tag: String, assets: [String], draft: Bool = false, prerelease: Bool = false) -> Data {
            let list = assets.map { "{\"name\":\"\($0)\",\"browser_download_url\":\"https://example.com/\($0)\"}" }
            return Data("{\"tag_name\":\"\(tag)\",\"body\":\"notes\",\"draft\":\(draft),\"prerelease\":\(prerelease),\"assets\":[\(list.joined(separator: ","))]}".utf8)
        }
        let found = try Updater.release(from: feed(tag: "v0.4.2", assets: ["EnCopy-0.4.2.dmg", "EnCopy.dmg"]))
        try check(found == Updater.Release(version: "0.4.2", notes: "notes", dmg: URL(string: "https://example.com/EnCopy.dmg")!),
                  "A release's version and its EnCopy.dmg are read from GitHub's answer")
        try check(try Updater.release(from: feed(tag: "v0.4.2", assets: ["EnCopy-0.4.2.dmg"])) == nil
                  && Updater.release(from: feed(tag: "v0.4.2", assets: ["EnCopy.dmg"], draft: true)) == nil
                  && Updater.release(from: feed(tag: "v0.4.2", assets: ["EnCopy.dmg"], prerelease: true)) == nil,
                  "A release without EnCopy.dmg, a draft or a prerelease offers no update")
        let notes = "## 新东西\n\n- **设置里写清 EnCopy 怎么存。** 「保留」页\n- 文字存成小文件\n\n## 安装\n\n下载 EnCopy.dmg\n\n---\n\nNew: the Keeping page"
        try check(Updater.whatsNew(notes) == "- 设置里写清 EnCopy 怎么存。 「保留」页\n- 文字存成小文件"
                  && Updater.whatsNew("Just a line\n---\nEnglish") == "Just a line",
                  "The update dialog shows only what is new, without the install steps, the English copy or Markdown marks")

        let fm = FileManager.default
        let dir = root.appendingPathComponent("updater", isDirectory: true)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        func makeApp(_ name: String, id: String, version: String, sign: Bool = true) throws -> URL {
            let app = dir.appendingPathComponent(name).appendingPathComponent("EnCopy.app")
            try fm.createDirectory(at: app.appendingPathComponent("Contents/MacOS"), withIntermediateDirectories: true)
            try fm.copyItem(at: URL(fileURLWithPath: "/usr/bin/true"), to: app.appendingPathComponent("Contents/MacOS/EnCopy"))
            let info: NSDictionary = ["CFBundleIdentifier": id, "CFBundleShortVersionString": version,
                                      "CFBundleExecutable": "EnCopy", "CFBundlePackageType": "APPL"]
            try info.write(to: app.appendingPathComponent("Contents/Info.plist"))
            if sign {
                let codesign = Process()
                codesign.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
                codesign.arguments = ["--force", "--sign", "-", app.path]
                codesign.standardError = FileHandle.nullDevice
                try codesign.run(); codesign.waitUntilExit()
                guard codesign.terminationStatus == 0 else { throw CheckFailed(message: "codesign failed for the updater fixture") }
            }
            return app
        }
        var requirement: SecRequirement?
        SecRequirementCreateWithString("identifier \"app.encopy.EnCopy\"" as CFString, [], &requirement)
        func refusal(_ app: URL, id: String = "app.encopy.EnCopy", current: String = "0.4.1",
                     requirement given: SecRequirement?) -> Updater.Failure? {
            do { try Updater.verify(app, bundleID: id, newerThan: current, requirement: given); return nil }
            catch { return error as? Updater.Failure }
        }
        let good = try makeApp("good", id: "app.encopy.EnCopy", version: "0.4.2")
        try check(refusal(good, requirement: requirement) == nil, "This app, newer and signed to the running copy's requirement, is accepted")
        try check(refusal(try makeApp("dev", id: "app.encopy.EnCopy.dev", version: "0.4.2"), requirement: requirement) == .wrongApp
                  && refusal(try makeApp("same", id: "app.encopy.EnCopy", version: "0.4.1"), requirement: requirement) == .notNewer
                  && refusal(try makeApp("unsigned", id: "app.encopy.EnCopy", version: "0.4.2", sign: false), requirement: requirement) == .signature
                  && refusal(good, requirement: nil) == .signature,
                  "Another app, an old version, an unsigned app, or no requirement to match is refused")
        var other: SecRequirement?
        SecRequirementCreateWithString("identifier \"app.encopy.EnCopy\" and certificate leaf = H\"0000000000000000000000000000000000000000\"" as CFString, [], &other)
        try check(refusal(good, requirement: other) == .signature, "An app signed by another certificate is refused")
        let tampered = try makeApp("tampered", id: "app.encopy.EnCopy", version: "0.4.2")
        try Data("changed".utf8).write(to: tampered.appendingPathComponent("Contents/MacOS/EnCopy"))
        try check(refusal(tampered, requirement: requirement) == .signature, "An app changed after it was signed is refused")

        // The image is unpacked next to the target, and the swap replaces it.
        let target = try makeApp("installed", id: "app.encopy.EnCopy", version: "0.4.1")
        let source = try makeApp("release", id: "app.encopy.EnCopy", version: "0.4.2")
        let dmg = dir.appendingPathComponent("EnCopy.dmg")
        func hdiutil(_ args: [String]) throws {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/hdiutil")
            p.arguments = args
            p.standardOutput = FileHandle.nullDevice
            try p.run(); p.waitUntilExit()
            guard p.terminationStatus == 0 else { throw CheckFailed(message: "hdiutil \(args[0]) failed") }
        }
        try hdiutil(["create", "-quiet", "-fs", "HFS+", "-srcfolder", source.deletingLastPathComponent().path, "-volname", "EnCopy", dmg.path])
        let staged = try Updater.unpack(dmg: dmg, beside: target)
        try check(refusal(staged, requirement: requirement) == nil
                  && !fm.fileExists(atPath: staged.deletingLastPathComponent().appendingPathComponent("mount/EnCopy.app").path),
                  "The app comes out of the image intact, and the image is detached")
        let work = staged.deletingLastPathComponent()
        try Updater.replace(target, with: staged)
        try? fm.removeItem(at: work)
        let installed = NSDictionary(contentsOf: target.appendingPathComponent("Contents/Info.plist"))
        try check(installed?["CFBundleShortVersionString"] as? String == "0.4.2", "The new version takes the old one's place")

        let empty = dir.appendingPathComponent("empty", isDirectory: true)
        try fm.createDirectory(at: empty, withIntermediateDirectories: true)
        try Data("x".utf8).write(to: empty.appendingPathComponent("readme.txt"))
        let emptyDMG = dir.appendingPathComponent("Empty.dmg")
        try hdiutil(["create", "-quiet", "-fs", "HFS+", "-srcfolder", empty.path, "-volname", "Empty", emptyDMG.path])
        var noApp: Updater.Failure?
        do { _ = try Updater.unpack(dmg: emptyDMG, beside: target) } catch { noApp = error as? Updater.Failure }
        let locked = dir.appendingPathComponent("locked", isDirectory: true)
        let lockedApp = try makeApp("locked", id: "app.encopy.EnCopy", version: "0.4.1")
        let spare = try makeApp("spare", id: "app.encopy.EnCopy", version: "0.4.2")
        try fm.setAttributes([.posixPermissions: 0o555], ofItemAtPath: locked.path)
        defer { try? fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: locked.path) }
        var replaceFailure: Updater.Failure?
        do { try Updater.replace(lockedApp, with: spare) } catch { replaceFailure = error as? Updater.Failure }
        let kept = NSDictionary(contentsOf: lockedApp.appendingPathComponent("Contents/Info.plist"))
        try check(noApp == .noApp && replaceFailure == .replace && kept?["CFBundleShortVersionString"] as? String == "0.4.1",
                  "An image without EnCopy, or a place that can't be written, leaves the installed app as it was")
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
        pickLine.movePick(1)
        try check(pickLine.picked?.url == pb, "Right moves the pick one older")
        pickLine.movePick(1)
        try check(pickLine.picked?.url == pa, "Right again reaches the oldest")
        pickLine.movePick(1)
        try check(pickLine.picked?.url == pa, "The pick stops at the oldest, no wrap")
        pickLine.movePick(-1)
        try check(pickLine.picked?.url == pb, "Left moves the pick one newer")
        pickLine.movePick(-1)
        pickLine.movePick(-1)
        try check(pickLine.picked?.url == pc, "The pick stops at the newest, no wrap")
        pickLine.clearPick()
        try check(pickLine.picked == nil, "Clearing leaves nothing picked")
        pickLine.movePick(-1)
        try check(pickLine.picked == nil, "Moving with nothing picked picks nothing")

        // What is taken down while picked yields the newest remaining.
        pickLine.pickNewest()
        pickLine.movePick(1)
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
        pasteLine.movePick(1)
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
        for _ in 0..<8 { scrollLine.movePick(1) }
        try check(scrollLine.scroll == Layout.spacing, "Picking older scrolls the line one place")
        for _ in 0..<8 { scrollLine.movePick(-1) }
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

        // ⌥C, → →, ⏎: the third newest is pasted and goes to the head.
        use.pickNewest()
        use.movePick(1)
        use.movePick(1)
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
        keys.pickNewest(); keys.movePick(1)
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
        try check(tab.kindFilter == .file && shownURLs().isEmpty, "Tab again shows only files (none here)")
        _ = bar.takes(tabKey([]), composing: false)
        try check(tab.kindFilter == nil && shownURLs().count == 3, "Tab again shows everything")
        _ = bar.takes(tabKey([.shift]), composing: false)
        try check(tab.kindFilter == .file, "⇧Tab goes back to files")
        tab.kindFilter = .text
        tab.query = "tab"
        try check(shownURLs() == [text], "A search inside the text filter shows only matching text")
        try check(!bar.takes(tabKey([]), composing: true), "Tab while composing stays with the input method")
    }

    /// T-026: the app in front when something is copied is kept with it and
    /// shown on the card beside the time.
    func checkSourceApp() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        let file = root.appendingPathComponent("source.sqlite")
        let history = History(file: file)
        var front: String? = "com.apple.finder"
        var hung: [URL] = []
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { front },
                                       onNew: { hung.append($0) }, onAgain: { _ in })
        func copy(_ text: String) { pasteboard.clearContents(); pasteboard.setString(text, forType: .string); watcher.check() }
        copy("source app words one")
        try check(hung.count == 1 && history.app(of: hung[0]) == "com.apple.finder", "The front app is kept with what was copied")
        let finder = History.appName("com.apple.finder")
        Note.sourceOf = { url in history.app(of: url).flatMap(History.appName) }
        let note = Note.reload(hung[0])
        try check(finder != nil && note?.app == finder && note?.caption.hasSuffix(" · \(finder!)") == true,
                  "The card shows the app's name after the time")
        let reopened = History(file: file)
        try check(reopened.app(of: hung[0]) == "com.apple.finder", "The source app is still there after reopening")
        front = "local.encopy.no-such-app"
        copy("source app words two")
        let unknown = Note.reload(hung[1])
        try check(unknown?.app == nil && unknown?.caption == unknown?.dateText, "An app not on this Mac shows only the time")
        front = nil
        copy("source app words three")
        try check(history.app(of: hung[2]) == nil, "No front app keeps no app")

        // A history written before the column existed is upgraded on open.
        let old = root.appendingPathComponent("old-schema.sqlite")
        var db: OpaquePointer?
        sqlite3_open(old.path, &db)
        sqlite3_exec(db, "CREATE TABLE item (id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, hash BLOB NOT NULL, modified REAL NOT NULL, used REAL NOT NULL)", nil, nil, nil)
        sqlite3_close(db)
        let upgraded = History(file: old)
        let watcher2 = ClipboardWatcher(pasteboard: pasteboard, history: upgraded, isSecureInput: { false }, frontApp: { "com.apple.finder" },
                                        onNew: { hung.append($0) }, onAgain: { _ in })
        pasteboard.clearContents(); pasteboard.setString("old schema words", forType: .string); watcher2.check()
        try check(upgraded.app(of: hung.last!) == "com.apple.finder", "A history from before gets the app column and keeps the app")
        Note.sourceOf = nil
    }

    /// T-027: per-app rules. Passwords and Keychain Access are preset to
    /// never record; "clear after an hour" keeps a copy for an hour after its
    /// last use, then deletes it outright.
    func checkAppRules() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("rules.sqlite"))
        let rules = Line(pasteboard: pasteboard, desktop: root, history: history)
        rules.maxItems = 100; rules.visibleCount = 8; rules.revealed = true
        var front = "com.apple.Passwords"
        var hung: [URL] = []
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { front },
                                       onNew: { hung.append($0); _ = rules.hang($0, quietly: true) }, onAgain: { _ in })
        func copy(_ text: String) { pasteboard.clearContents(); pasteboard.setString(text, forType: .string); watcher.check() }
        try check(ClipboardWatcher.rule(for: "com.apple.Passwords") == .skip && ClipboardWatcher.rule(for: "com.apple.keychainaccess") == .skip
                  && ClipboardWatcher.rule(for: "com.apple.finder") == .normal, "Passwords and Keychain Access are preset to never record")
        ClipboardWatcher.appRules = ["com.example.not-installed": .skip, "com.apple.finder": .skip]
        try check(ClipboardWatcher.listedApps(front: "com.apple.TextEdit") == ["com.apple.TextEdit", "com.apple.finder"],
                  "By app lists the front app first, then rules for apps on this Mac, never a bare bundle id")
        UserDefaults.standard.removeObject(forKey: "appRules")
        copy("secret from passwords")
        try check(hung.isEmpty && history.count == 0 && rules.liveCount == 0
                  && history.search(SearchIndex.words("secret"), onLine: [], limit: 5).basketTotal == 0,
                  "A copy from a never-record app leaves nothing on the line, in the history or in the index")
        var all = ClipboardWatcher.appRules
        all["com.apple.finder"] = .hourly
        ClipboardWatcher.appRules = all
        front = "com.apple.finder"
        let start = Date()
        copy("hourly words from finder")
        try check(hung.count == 1 && history.ttl(of: hung[0]) == ClipboardWatcher.hourly, "An hourly app's copy is kept with an hour to live")
        Note.clearsOf = { url in history.ttl(of: url).map(Line.clearsText) }
        try check(Note.reload(hung[0])?.caption.contains(Line.clearsText(3600)) == true, "Its card says it clears in an hour")
        rules.clearExpired(now: start.addingTimeInterval(3000))
        try check(rules.liveCount == 1 && FileManager.default.fileExists(atPath: hung[0].path), "Before the hour is up it stays")
        rules.clearExpired(now: start.addingTimeInterval(3700))
        pump(0.7); rules.closeGaps()
        try check(rules.liveCount == 0 && !FileManager.default.fileExists(atPath: hung[0].path) && history.count == 0,
                  "After the hour its card, its file and its row are gone")
        all["com.apple.Passwords"] = .normal
        ClipboardWatcher.appRules = all
        front = "com.apple.Passwords"
        copy("passwords now recorded")
        try check(hung.count == 2 && history.ttl(of: hung[1]) == nil, "Set to record normally, that app's copies are kept for good")
        Note.clearsOf = nil
        UserDefaults.standard.removeObject(forKey: "appRules")
    }

    /// T-028: verification codes stay ten minutes, then go for good.
    func checkVerificationCodes() throws {
        let yes: [(String, String?)] = [("482913", "com.apple.MobileSMS"), (" 7031 ", "com.apple.mail"),
                                        ("您的验证码是 553201，5 分钟内有效", nil), ("Your code: 90817264", "com.apple.Safari"), ("Use code 4821 to sign in", nil)]
        let no: [(String, String?)] = [("482913", "com.apple.finder"), ("订单号 20261122 0458", nil), ("123", "com.apple.MobileSMS"),
                                       ("code review at 10", nil), ("验证码", nil), ("1234567890 code", nil)]
        try check(yes.allSatisfy { ClipboardWatcher.isCode($0.0, app: $0.1) }, "Codes from Messages, Mail and texts that say code are recognised")
        try check(no.allSatisfy { !ClipboardWatcher.isCode($0.0, app: $0.1) }, "Order numbers, short numbers and long numbers are not codes")

        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("codes.sqlite"))
        let codes = Line(pasteboard: pasteboard, desktop: root, history: history)
        codes.maxItems = 100; codes.visibleCount = 8; codes.revealed = true
        var hung: [URL] = []
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { "com.apple.MobileSMS" },
                                       onNew: { hung.append($0); _ = codes.hang($0, quietly: true) }, onAgain: { _ in })
        let start = Date()
        pasteboard.clearContents(); pasteboard.setString("663104", forType: .string); watcher.check()
        pasteboard.clearContents(); pasteboard.setString("see you at the cafe", forType: .string); watcher.check()
        try check(hung.count == 2 && history.ttl(of: hung[0]) == ClipboardWatcher.codeLife && history.ttl(of: hung[1]) == nil,
                  "A code from Messages gets ten minutes; a message that is not a code stays")
        try check(Line.clearsText(ClipboardWatcher.codeLife) != Line.clearsText(ClipboardWatcher.hourly), "A code's card says ten minutes, not an hour")
        codes.clearExpired(now: start.addingTimeInterval(540))
        try check(codes.liveCount == 2, "Nine minutes later the code is still there")
        codes.clearExpired(now: start.addingTimeInterval(660))
        pump(0.7); codes.closeGaps()
        try check(codes.liveCount == 1 && !FileManager.default.fileExists(atPath: hung[0].path)
                  && history.search(SearchIndex.words("663104"), onLine: [], limit: 5).basketTotal == 0,
                  "Eleven minutes later the code is gone from the line, the disk and the index")
    }

    /// T-029, and T-049 since: a file copied in Finder hangs as the list
    /// EnCopy keeps of it, a folder card found by the file's name; copying it
    /// back gives the file; taking it down leaves the file alone.
    func checkFiles() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let docs = root.appendingPathComponent("Quarterly Docs", isDirectory: true)
        try FileManager.default.createDirectory(at: docs, withIntermediateDirectories: true)
        let pdf = docs.appendingPathComponent("Budget plan 2027.pdf")
        try Data("%PDF-1.4 fixture".utf8).write(to: pdf)
        try check(isFile(pdf) && isFile(docs) && !isFile(try png("Not a file.png")), "A PDF and a folder are files; a picture is not")
        let history = History(file: root.appendingPathComponent("files.sqlite"))
        let files = Line(pasteboard: pasteboard, desktop: root, history: history)
        files.maxItems = 100; files.visibleCount = 8; files.revealed = true
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { "com.apple.finder" },
                                       onNew: { _ = files.hang($0, quietly: true) }, onAgain: { _ in })
        pasteboard.clearContents(); pasteboard.writeObjects([pdf as NSURL]); watcher.check()
        guard let list = files.items.first?.url else { throw CheckFailed(message: "Nothing hung for the copied file") }
        try check(files.items.count == 1 && isFileList(list) && samePaths(FileList.paths(list), [pdf]) && files.kind(of: list) == .file,
                  "A file copied in Finder hangs as the list EnCopy keeps of it, a file")
        try check(files.items.first.map { $0.thumb.size.width > 0 } == true, "It has a card to show")
        files.search.update([list])
        for _ in 0..<40 where !files.search.matches(list, SearchIndex.words("budget")) { pump(0.05) }
        try check(files.search.matches(list, SearchIndex.words("budget 2027")) && files.search.matches(list, SearchIndex.words("quarterly")),
                  "It is found by its name and by its folder")
        files.copy(files.items[0].id)
        try check(samePaths(copiedURLs, [pdf]) && pasteboard.data(forType: .png) == nil, "Copying it back gives the file, with no picture")
        watcher.check()
        try check(files.items.count == 1, "Copying it from the line does not hang it a second time")
        files.kindFilter = .file
        try check(files.shown.map(\.url) == [list], "Tab's file filter shows it")
        files.kindFilter = nil
        files.discard(files.items[0].id)
        try check(FileManager.default.fileExists(atPath: pdf.path), "Taking it down leaves the file where it is")
        ClipboardWatcher.filesEnabled = false
        pasteboard.clearContents(); pasteboard.writeObjects([docs as NSURL]); watcher.check()
        try check(files.liveCount == 0, "With files turned off, a copied file does not hang")
        ClipboardWatcher.filesEnabled = true
    }

    /// T-030: EnCopy's own pictures fall into the basket instead of the
    /// Trash, are found there by the words read from them, and come back.
    func checkImagesInBasket() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Basket check shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("images.sqlite"))
        let shots = Line(pasteboard: pasteboard, desktop: root, history: history)
        shots.maxItems = 2; shots.visibleCount = 8; shots.revealed = true
        let a = try png("Screenshot A.png", in: Inbox.folder), b = try png("Screenshot B.png", in: Inbox.folder, red: 0.6)
        let c = try png("Screenshot C.png", in: Inbox.folder, red: 0.9)
        let outside = try png("Outside.png")
        try check(shots.isKeptImage(a) && !shots.isKeptImage(outside), "A screenshot in EnCopy's folder is kept; a picture elsewhere is not")
        _ = shots.hang(a, quietly: true)
        shots.search.onRead(a, "INVOICE 7731 paid")
        _ = shots.hang(b, quietly: true)
        _ = shots.hang(c, quietly: true)
        pump(0.7); shots.closeGaps()
        try check(shots.items.map(\.url) == [b, c] && FileManager.default.fileExists(atPath: a.path) && history.count == 3,
                  "A full line lets the oldest screenshot fall into the basket, file and row kept, not to the Trash")
        shots.query = "invoice"
        try check(shots.basketCards.map(\.url) == [a], "Typing the words read from it finds it in the basket")
        let card = shots.basketCards[0].id
        shots.copy(card)
        try check(shots.items.last?.url == a && copiedURLs == [a], "Copying it from the basket brings it back to the head of the line")
        shots.query = ""
        pump(0.7); shots.closeGaps()
        shots.query = "invoice"
        let id = shots.items.first { $0.url == a }!.id
        shots.query = ""
        shots.discard(id)
        try check(!FileManager.default.fileExists(atPath: a.path) && history.count == 2, "Taking it down trashes the file and forgets its row")
        shots.putBack()
        try check(FileManager.default.fileExists(atPath: a.path) && history.count == 3, "Put back brings back both the file and its row")
        UserDefaults.standard.removeObject(forKey: "pegged")
        let other = Line(pasteboard: pasteboard, desktop: root, history: History(file: root.appendingPathComponent("images2.sqlite")))
        other.maxItems = 1; other.visibleCount = 8
        _ = other.hang(outside, quietly: true)
        _ = other.hang(try png("Outside 2.png"), quietly: true)
        pump(0.7); other.closeGaps()
        try check(FileManager.default.fileExists(atPath: outside.path) && other.history.count == 0, "A picture from elsewhere only leaves the line, as before")
    }

    /// A large picture in EnCopy's folder, for shrinking.
    func bigShot(_ name: String) throws -> URL {
        let url = Inbox.folder.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: Inbox.folder, withIntermediateDirectories: true)
        let context = CGContext(data: nil, width: 2000, height: 1200, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        for i in 0..<40 {
            context.setFillColor(CGColor(red: CGFloat(i % 7) / 7, green: CGFloat(i % 5) / 5, blue: CGFloat(i % 3) / 3, alpha: 1))
            context.fill(CGRect(x: i * 50, y: (i * 37) % 1100, width: 50, height: 100))
        }
        let output = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(output, context.makeImage()!, nil)
        guard CGImageDestinationFinalize(output) else { throw CheckFailed(message: "Could not write the big shot") }
        return url
    }

    /// T-031: text stays a year after its last use, a picture's original 30
    /// days or until the cap; what expires waits seven days in 「最近清掉」.
    func checkRetention() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Retention check shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("retention.sqlite"))
        let keep = Line(pasteboard: pasteboard, desktop: root, history: history)
        keep.maxItems = 1; keep.visibleCount = 8
        let cleared = Clearing(folder: root.appendingPathComponent("cleared-check", isDirectory: true))
        keep.clearing = cleared
        let now = Date()
        let day: TimeInterval = 86400
        let old = try adoptedNote("old", "words from long ago")
        let recent = try adoptedNote("recent", "words from last month")
        let onLine = try adoptedNote("online", "old words still hanging")
        for note in [old, recent, onLine] { _ = keep.hang(note, quietly: true) }
        history.setUsed(old, now.addingTimeInterval(-400 * day))
        history.setUsed(recent, now.addingTimeInterval(-40 * day))
        history.setUsed(onLine, now.addingTimeInterval(-500 * day))
        let shot = try bigShot("Old screenshot.png")
        _ = keep.hang(shot, quietly: true)
        keep.search.onRead(shot, "RECEIPT 4410")
        _ = keep.hang(onLine, quietly: true)
        pump(0.7); keep.closeGaps()
        try check(keep.items.filter { !$0.falling }.map(\.url) == [onLine], "Only the old note hangs now")
        history.setUsed(shot, now.addingTimeInterval(-40 * day))
        let before = (try FileManager.default.attributesOfItem(atPath: shot.path)[.size] as? Int) ?? 0
        let result = keep.runRetention(now: now)
        try check(result.expired == [old], "A note unused for over a year expires; one on the line does not, however old")
        try check(!FileManager.default.fileExists(atPath: old.path) && history.count == 3 && FileManager.default.fileExists(atPath: recent.path),
                  "The expired note leaves its place and the history; a month-old note stays")
        let after = (try FileManager.default.attributesOfItem(atPath: shot.path)[.size] as? Int) ?? 0
        let pixels = NSBitmapImageRep(data: try Data(contentsOf: shot))?.pixelsWide ?? 0
        try check(result.shrunk == [shot] && after < before && pixels <= Retention.thumbnailPixels,
                  "A 40-day-old screenshot shrinks to a thumbnail at the same place")
        try check(history.search(SearchIndex.words("receipt"), onLine: [], limit: 5).basket == [shot], "Its words are still found after shrinking")
        keep.search.onRead(shot, "blurry thumbnail text")
        keep.search.onRead(shot, "")
        try check(history.words(of: shot)?.contains("receipt") == true,
                  "Reading the thumbnail again does not replace the words read from the original")
        try check(keep.runRetention(now: now).shrunk.isEmpty, "A shrunk picture is not shrunk again")
        let batches = cleared.batches()
        try check(batches.count == 1 && batches[0].reason == "expired" && batches[0].entries.map(\.path) == [history.relative(old)!],
                  "The expired note waits in 「最近清掉」 as one batch")
        cleared.putBack(batches[0].id, history: history)
        try check(FileManager.default.fileExists(atPath: old.path) && history.count == 4 && cleared.batches().isEmpty
                  && keep.runRetention(now: now).expired == [old], "Put back, the note returns with its row and its old last use")
        cleared.purge(now: now.addingTimeInterval(8 * day))
        try check(cleared.batches().isEmpty && !FileManager.default.fileExists(atPath: old.path), "After seven days a batch is gone for good")

        // Over the cap, the oldest original shrinks first, sooner than 30 days.
        let a = try bigShot("Cap A.png"), b = try bigShot("Cap B.png")
        for url in [a, b] { history.recordImage(url, text: "cap") }
        history.setUsed(a, now.addingTimeInterval(-5 * day))
        history.setUsed(b, now.addingTimeInterval(-2 * day))
        let size = Int64(try a.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0)
        let capped = keep.runRetention(now: now, cap: size + size / 2)
        try check(capped.shrunk == [a], "Over the cap, the oldest original shrinks first")
    }

    /// T-032: the cleanup panel picks by kind, age, app and words, never
    /// what hangs; what it clears waits in 「最近清掉」 and goes back whole.
    func checkCleanup() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("cleanup.sqlite"))
        let tidy = Line(pasteboard: pasteboard, desktop: root, history: history)
        tidy.maxItems = 1; tidy.visibleCount = 8
        tidy.clearing = Clearing(folder: root.appendingPathComponent("cleared-cleanup", isDirectory: true))
        let now = Date(), day: TimeInterval = 86400
        let slack = try adoptedNote("slack", "standup notes from slack")
        let mail = try adoptedNote("mail", "invoice from mail")
        let linkSrc = root.appendingPathComponent("note-src/cleanup-link-\(UUID().uuidString.prefix(6)).txt")
        try "https://example.com/cleanup".write(to: linkSrc, atomically: true, encoding: .utf8)
        let link = NoteFolder.adopt(linkSrc)!
        let hanging = try adoptedNote("hanging", "invoice still hanging")
        for url in [slack, mail, link, hanging] { _ = tidy.hang(url, quietly: true) }
        history.record(slack, app: "com.tinyspeck.slackmacgap"); history.record(mail, app: "com.apple.mail")
        for (url, days) in [(slack, 40.0), (mail, 3.0), (link, 100.0), (hanging, 400.0)] { history.setUsed(url, now.addingTimeInterval(-days * day)) }
        func pick(_ f: CleanupFilter) -> [URL] { tidy.cleanupCandidates(f, now: now) }
        try check(Set(pick(CleanupFilter())) == [slack, mail, link], "Without conditions everything but what hangs is picked")
        try check(Set(pick(CleanupFilter(olderThanDays: 30))) == [slack, link], "Over a month ago picks the two older ones")
        try check(pick(CleanupFilter(kind: .link)) == [link], "The link kind picks only the link")
        try check(pick(CleanupFilter(app: "com.apple.mail")) == [mail], "An app picks only what came from it")
        try check(pick(CleanupFilter(text: "invoice")) == [mail], "Words pick what holds them, never the one on the line")
        guard let batch = tidy.cleanUp(pick(CleanupFilter(olderThanDays: 30)), now: now) else { throw CheckFailed(message: "Nothing was cleared") }
        try check(batch.reason == "manual" && batch.entries.count == 2 && !FileManager.default.fileExists(atPath: slack.path)
                  && history.count == 2, "Clearing moves them into 「最近清掉」 as one batch, out of the history")
        tidy.putBack(batch: batch.id)
        try check(FileManager.default.fileExists(atPath: slack.path) && history.count == 4 && history.app(of: slack) == "com.tinyspeck.slackmacgap",
                  "Put back, they return with their app")
        Line.originalsCap = 2 << 30
        try check(Line.originalsCap == 2 << 30, "The originals cap is kept as set")
        UserDefaults.standard.removeObject(forKey: "originalsCap")
        try check(Line.originalsCap == Retention.defaultCap, "Unset, the cap is 1 GB")
    }

    /// T-033: ↓ while summoned walks the basket by last use — today,
    /// yesterday, this week, earlier — and what is used comes back.
    /// T-035: the newest hangs right of the knot, older ones further right,
    /// pinned ones left of it; ← and → move the pick the way they point.
    func checkNewestByKnot() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let knot = line()
        let urls = try (0..<4).map { try png("Knot \($0).png", red: CGFloat($0) / 4) }
        for url in urls { _ = knot.hang(url, quietly: true) }
        knot.togglePin(knot.items.first { $0.url == urls[0] }!.id)
        try check(knot.pinnedLead == 1 && knot.items.first?.url == urls[0], "The pinned one is first in the store")
        let count = knot.items.count, width: CGFloat = 1400
        let xs = (0..<count).map { Layout.x(index: $0, count: count, width: width, visible: 8, scroll: 0, pinned: 1) }
        func x(_ url: URL) -> CGFloat { xs[knot.items.firstIndex { $0.url == url }!] }
        try check(x(urls[0]) < x(urls[3]) && x(urls[3]) < x(urls[2]) && x(urls[2]) < x(urls[1]),
                  "Left to right: pinned, then the newest, then older ones")
        try check(abs(x(urls[3]) - x(urls[0]) - Layout.spacing) < 0.5, "The newest hangs in the first place past the knot")
        try check((0..<count).allSatisfy { Layout.slot(Layout.slot($0, count: count, pinned: 1), count: count, pinned: 1) == $0 },
                  "Place and index map back to each other")

        knot.pickNewest()
        try check(knot.picked?.url == urls[3], "Summoning still picks the newest")
        knot.movePick(1)
        try check(knot.picked?.url == urls[2], "→ moves right, to the next older")
        knot.movePick(-1); knot.movePick(-1)
        try check(knot.picked?.url == urls[0], "← from the newest crosses the knot to the pinned one")
        knot.movePick(-1)
        try check(knot.picked?.url == urls[0], "← stops at the left end")
        for _ in 0..<5 { knot.movePick(1) }
        try check(knot.picked?.url == urls[1], "→ stops at the oldest, the right end")

        // While searching there is no knot: the newest match is leftmost.
        knot.query = "zzznomatch"
        try check(knot.layoutPinned == 0, "A search has no pinned end")
        knot.query = ""
        try check(knot.layoutPinned == 1, "Back on the line the pinned end returns")

        // On a long line, older ones come in from the right: scroll grows.
        let long = line()
        Line.keepOnLine = 25
        long.maxItems = 25; long.visibleCount = 4
        defer { Line.keepOnLine = nil }
        for i in 0..<6 { _ = long.hang(try png("Long \(i).png", red: CGFloat(i) / 6), quietly: true) }
        let lc = long.items.count
        let newestX = Layout.x(index: lc - 1, count: lc, width: width, visible: 4, scroll: 0)
        try check(abs(newestX - Layout.first(width: width, visible: 4)) < 0.5, "On a long line the newest is in the first place")
        let oldX = Layout.x(index: 0, count: lc, width: width, visible: 4, scroll: Layout.spacing)
        let oldX0 = Layout.x(index: 0, count: lc, width: width, visible: 4, scroll: 0)
        try check(oldX < oldX0, "Scrolling brings older ones in from the right")
    }

    /// T-036: the motion table. Values from the design's table: paper
    /// weights, the panel's springs, the sweep, the breeze's quiet time.
    func checkMotion() throws {
        let expected: [Motion.Paper: (Double, Double)] = [.receipt: (38, 2.4), .tag: (42, 2.6), .polaroid: (46, 2.8),
                                                          .tractor: (50, 3.0), .folder: (58, 3.4)]
        try check(Motion.Paper.allCases.allSatisfy { Motion.weight($0) == Motion.Weight(stiffness: expected[$0]!.0, damping: expected[$0]!.1) },
                  "Each paper swings with the weight in the design's table")
        let settle = [Motion.Paper.receipt, .tag, .polaroid, .tractor, .folder].map { Motion.weight($0).settling }
        try check(zip(settle, settle.dropFirst()).allSatisfy { $0 < $1 }, "Receipt swings longest, the folder settles first")

        try check(Motion.panel(revealed: true, bySummon: true, reduced: false) == .spring(response: 0.28, dampingFraction: 0.86),
                  "⌥C drops the panel on the quick spring")
        try check(Motion.panel(revealed: true, bySummon: false, reduced: false) == .spring(response: 0.42, dampingFraction: 0.82),
                  "Sliding down past the edge keeps the slower upstream spring")
        try check(Motion.panel(revealed: false, bySummon: true, reduced: false) == .timingCurve(0.23, 1, 0.32, 1, duration: 0.16),
                  "Tucking away is 0.16 s ease-out with no bounce")
        try check(Motion.panel(revealed: true, bySummon: true, reduced: true) == .easeInOut(duration: 0.12)
                  && Motion.panel(revealed: false, bySummon: false, reduced: true) == .easeInOut(duration: 0.12),
                  "With Reduce motion the panel only fades")

        try check(Motion.sweepDelay(x: 0, width: 1000) == 0 && abs(Motion.sweepDelay(x: 1000, width: 1000) - 0.26) < 1e-9
                  && Motion.sweepDelay(x: 300, width: 1000) < Motion.sweepDelay(x: 700, width: 1000)
                  && Motion.sweepDelay(x: -50, width: 1000) == 0 && abs(Motion.sweepDelay(x: 1300, width: 1000) - 0.26) < 1e-9,
                  "The sweep runs left to right, the rightmost 0.26 s late")
        try check(Motion.summonSway == 1.5...3 && Motion.sweepSway == 5...9, "⌥C sways 1.5–3°, sliding down sweeps 5–9°")

        let t0 = Date()
        try check(!Motion.breezes(now: t0.addingTimeInterval(3.9), lastKey: t0, reduced: false)
                  && Motion.breezes(now: t0.addingTimeInterval(4), lastKey: t0, reduced: false),
                  "No breeze until 4 s after the last key")
        try check(!Motion.breezes(now: t0.addingTimeInterval(60), lastKey: t0, reduced: true), "No breeze with Reduce motion")
        let calm = Motion.gust(summoned: true), free = Motion.gust(summoned: false)
        try check(free == 1.6...3.4 && abs(calm.lowerBound * 2 - free.lowerBound) < 1e-9 && abs(calm.upperBound * 2 - free.upperBound) < 1e-9,
                  "The breeze is half as strong while summoned")

        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let keys = line()
        _ = keys.hang(try png("Motion A.png"), quietly: true)
        _ = keys.hang(try png("Motion B.png", red: 0.8), quietly: true)
        try check(keys.lastKeyAt == .distantPast, "A fresh line has heard no key")
        keys.pickNewest(); keys.movePick(1)
        try check(Date().timeIntervalSince(keys.lastKeyAt) < 1, "Picking counts as a key: the breeze waits")
        keys.lastKeyAt = .distantPast
        keys.query = "x"
        try check(Date().timeIntervalSince(keys.lastKeyAt) < 1, "Typing counts as a key")
        keys.lastKeyAt = .distantPast
        keys.cycleKind(1)
        try check(Date().timeIntervalSince(keys.lastKeyAt) < 1, "Tab counts as a key")
        let before = keys.landing
        keys.landed(bySummon: true)
        try check(keys.cameBySummon && keys.landing == before + 1, "Coming down by ⌥C is told to the papers")
        keys.landed(bySummon: false)
        try check(!keys.cameBySummon && keys.landing == before + 2, "Sliding down is told too")
    }

    /// T-037: the menu bar glyph is the design's rope with two pegs.
    func checkMenuBarIcon() throws {
        func inked(_ image: NSImage) -> (total: Int, ropeRow: Int, pegCore: Int) {
            let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 36, pixelsHigh: 24, bitsPerSample: 8,
                                       samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                       bytesPerRow: 0, bitsPerPixel: 0)!
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
            image.draw(in: NSRect(x: 0, y: 0, width: 36, height: 24))
            NSGraphicsContext.restoreGraphicsState()
            var total = 0, ropeRow = 0, core = 0
            for y in 0..<24 { for x in 0..<36 where (rep.colorAt(x: x, y: y)?.alphaComponent ?? 0) > 0.5 {
                total += 1
                if y <= 5 && (x < 6 || x > 30) { ropeRow += 1 }   // rope ends, near the top (bitmap rows go down)
                if (x == 10 || x == 24) && (10...14).contains(y) { core += 1 }   // the middle of each peg
            } }
            return (total, ropeRow, core)
        }
        let on = MenuBarIcon.image(watching: true), off = MenuBarIcon.image(watching: false)
        try check(on.isTemplate && off.isTemplate && on.size == NSSize(width: 18, height: 12),
                  "The menu bar glyph is an 18×12 template image")
        let a = inked(on), b = inked(off)
        try check(a.ropeRow > 0 && b.ropeRow > 0, "The rope runs to both ends, high at the edges")
        try check(a.pegCore >= 8 && b.pegCore == 0, "The pegs are filled while watching, hollow when not")
    }

    /// T-038: the rest of the motion table, all on springs that stop by
    /// themselves: the rope bounces, a slide swings the papers unless the key
    /// is held, the fall sinks then drops, ⏎ sends the paper to the pointer.
    func checkMotionRest() throws {
        let start = Motion.fallProgress(0), sunk = Motion.fallProgress(0.1), end = Motion.fallProgress(1)
        try check(start.sink == 0 && start.gravity == 0 && abs(sunk.sink - 5) < 1e-9 && sunk.gravity == 0
                  && abs(end.sink - 5) < 1e-9 && end.gravity == 1, "The fall sinks 5 points in its first tenth, then gravity takes it")
        try check(Motion.fallProgress(0.03).sink > 5 * 0.3, "The sink is eased out: the key shows at once")
        try check(Motion.fallProgress(0.4).gravity < Motion.fallProgress(0.7).gravity - 0.1
                  && Motion.fallProgress(0.7).gravity - Motion.fallProgress(0.4).gravity
                     < Motion.fallProgress(1).gravity - Motion.fallProgress(0.7).gravity, "The fall speeds up")
        try check(Motion.fall == 0.42 && Motion.flyOut == 0.3 && Motion.flyOutScale == 0.16 && Motion.menuBarSwayTotal == 0.6,
                  "Fall 0.42 s, flight to the pointer 0.3 s to 0.16×, menu bar sway 0.6 s")

        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        Motion.reducedOverride = false
        defer { Motion.reducedOverride = nil }
        let rope = line()
        rope.landed(bySummon: true)
        try check(rope.sagScale == Motion.ropeTaut, "Coming down, the rope starts pulled almost straight")
        RunLoop.main.run(until: Date().addingTimeInterval(0.3))
        try check(rope.sagScale == 1, "Then it is let go into its sag")
        rope.openBasket()
        try check(rope.sagScale == Motion.basketTaut, "↓ opening the basket list bounces the rope once")
        rope.closeBasket()
        RunLoop.main.run(until: Date().addingTimeInterval(0.3))
        Motion.reducedOverride = true
        rope.landed(bySummon: false)
        try check(rope.sagScale == 1, "With Reduce motion the rope does not bounce")
        Motion.reducedOverride = false

        let long = line()
        Line.keepOnLine = 25
        long.maxItems = 25; long.visibleCount = 8
        defer { Line.keepOnLine = nil }
        for i in 0..<12 { _ = long.hang(try png("Sway \(i).png", red: CGFloat(i) / 12), quietly: true) }
        long.pickNewest()
        for _ in 0..<7 { long.lastPickMove = .distantPast; long.movePick(1) }
        try check(long.scroll == 0 && long.slide.count == 0, "No slide while the pick stays in view")
        long.lastPickMove = .distantPast; long.movePick(1)
        try check(long.scroll == Layout.spacing && long.slide.count == 1 && long.slide.degrees == -Motion.slideSway,
                  "Sliding to older ones swings the papers the other way")
        long.movePick(1)
        try check(long.scroll == 2 * Layout.spacing && long.slide.count == 1, "A held arrow slides without swinging")

        let plainPegs = MenuBarIcon.image(watching: true), swung = MenuBarIcon.image(watching: true, tilt: 8)
        try check(plainPegs.tiffRepresentation != swung.tiffRepresentation, "The menu bar pegs can swing")

        let flyLine = line()
        _ = flyLine.hang(try adoptedNote("fly", "fly words"), quietly: true)
        var order: [String] = []
        let flyer = Paster(line: flyLine, isTrusted: { true }, send: { _ in order.append("send") })
        flyer.flyOut = { _ in order.append("fly") }
        flyer.tuckAway = { order.append("tuck") }
        flyLine.pickNewest()
        flyer.pastePicked()
        try check(order == ["fly", "tuck", "send"], "⏎ sends the paper flying while the line is still down, then tucks and pastes")
    }

    /// Review #6: "take down everything to the right" takes the older ones,
    /// which hang to the right since T-035; pinned ones never go.
    func checkTakeDownSides() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let sides = line()
        let urls = try (0..<5).map { try png("Side \($0).png", red: CGFloat($0) / 5) }
        for url in urls { _ = sides.hang(url, quietly: true) }
        func id(_ i: Int) -> UUID { sides.items.first { $0.url == urls[i] }!.id }
        // On screen: 4 3 2 1 0, newest at the left.
        try check(sides.neighbours(of: id(2), toTheLeft: false).map(\.url) == [urls[1], urls[0]],
                  "Right of the middle one are the two older ones")
        try check(sides.neighbours(of: id(2), toTheLeft: true).map(\.url) == [urls[3], urls[4]],
                  "Left of the middle one are the two newer ones")
        try check(sides.neighbours(of: id(4), toTheLeft: true).isEmpty, "Nothing is left of the newest")
        try check(sides.neighbours(of: id(0), toTheLeft: false).isEmpty, "Nothing is right of the oldest")
        sides.togglePin(id(0))
        try check(sides.neighbours(of: id(4), toTheLeft: true).isEmpty, "A pinned one, left of the knot, is never taken down")
    }

    /// Review #5: only a number next to a word that means a code is a code;
    /// a pinned code never expires; copied again as plain text, it stays.
    func checkCodeScope() throws {
        let notCodes = ["status code 500 on port 8080", "fix(T-1024): clean up dead code", "Postal code: 10001",
                        "HTTP error code 4040", "The zip code is 94110", "v2024 code freeze"]
        try check(notCodes.allSatisfy { !ClipboardWatcher.isCode($0, app: nil) }, "Status, postal and error codes, ticket numbers are not codes")
        let codes = ["您的验证码是 553201，5 分钟内有效", "Your code: 90817264", "Use code 4821 to sign in",
                     "123456 is your verification code", "【银行】动态码 883920，请勿泄露"]
        try check(codes.allSatisfy { ClipboardWatcher.isCode($0, app: nil) }, "Real codes are still recognised")

        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("code-scope.sqlite"))
        let pin = Line(pasteboard: pasteboard, desktop: root, history: history)
        pin.maxItems = 100; pin.visibleCount = 8; pin.revealed = true
        var app = "com.apple.MobileSMS"
        var hung: [URL] = []
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { app },
                                       onNew: { hung.append($0); _ = pin.hang($0, quietly: true) }, onAgain: { _ in })
        let start = Date()
        pasteboard.clearContents(); pasteboard.setString("771204", forType: .string); watcher.check()
        pasteboard.clearContents(); pasteboard.setString("880315", forType: .string); watcher.check()
        try check(hung.count == 2 && history.ttl(of: hung[1]) == ClipboardWatcher.codeLife, "Both are codes from Messages")
        pin.togglePin(pin.items.first { $0.url == hung[0] }!.id)
        app = "com.apple.Safari"
        pasteboard.clearContents(); pasteboard.setString("880315", forType: .string); watcher.check()
        try check(history.ttl(of: hung[1]) == nil, "Copied again as plain text elsewhere, the number no longer expires")
        pin.clearExpired(now: start.addingTimeInterval(700))
        try check(FileManager.default.fileExists(atPath: hung[0].path) && pin.items.contains { $0.url == hung[0] && $0.kept },
                  "A pinned code is not cleared when its ten minutes are up")
        try check(FileManager.default.fileExists(atPath: hung[1].path), "The other one, no longer a code, stays too")
    }

    /// Review #2: putting a batch back never loses a file: a name taken in
    /// the meantime gets the next free one, and what cannot go back stays
    /// in the batch.
    func checkPutBackKeepsBatch() throws {
        let history = History(file: root.appendingPathComponent("putback.sqlite"))
        let clearing = Clearing(folder: root.appendingPathComponent("cleared-putback", isDirectory: true))
        let a = try adoptedNote("putback-a", "first words of a"), b = try adoptedNote("putback-b", "words of b")
        for url in [a, b] { history.record(url, text: try String(contentsOf: url, encoding: .utf8)) }
        guard let batch = clearing.clear([a, b], reason: "manual", history: history) else { throw CheckFailed(message: "Nothing was cleared") }
        // Meanwhile a new note takes a's name.
        try "a newer note in the same place".write(to: a, atomically: true, encoding: .utf8)
        let back = clearing.putBack(batch.id, history: history)
        let fm = FileManager.default
        try check(back.count == 2 && (try String(contentsOf: a, encoding: .utf8)) == "a newer note in the same place",
                  "The newer file in the old place is left as it is")
        let renamed = back.first { $0 != b }!
        try check(renamed != a && (try String(contentsOf: renamed, encoding: .utf8)) == "first words of a"
                  && fm.fileExists(atPath: b.path), "The cleared one comes back under the next free name")
        try check(clearing.batches().allSatisfy { $0.id != batch.id }, "A batch that all came back is gone")

        // One cannot go back: its folder's place is taken by a file.
        let sub = DataFolder.url.appendingPathComponent("putback-sub", isDirectory: true)
        try fm.createDirectory(at: sub, withIntermediateDirectories: true)
        let c = sub.appendingPathComponent("c.png"), d = DataFolder.url.appendingPathComponent("putback-d.png")
        try fm.copyItem(at: try png("putback-c-src.png"), to: c)
        try fm.copyItem(at: try png("putback-d-src.png"), to: d)
        history.recordImage(c, text: ""); history.recordImage(d, text: "")
        guard let second = clearing.clear([c, d], reason: "manual", history: history) else { throw CheckFailed(message: "Nothing was cleared") }
        try fm.removeItem(at: sub)
        try "in the way".write(to: sub, atomically: true, encoding: .utf8)
        let some = clearing.putBack(second.id, history: history)
        try fm.removeItem(at: sub)
        let left = clearing.batches().first { $0.id == second.id }
        try check(some == [d] && left?.entries.count == 1 && left.map { fm.fileExists(atPath: clearing.folder.appendingPathComponent($0.id).appendingPathComponent($0.entries[0].path).path) } == true,
                  "What could not go back stays in the batch, file and all")
    }

    /// Review #10: dragging a card out never moves or trashes a file that is
    /// not EnCopy's own, and a basket card only gives a copy.
    func checkDragOperations() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("drag.sqlite"))
        let drag = Line(pasteboard: pasteboard, desktop: root, history: history)
        drag.maxItems = 2; drag.visibleCount = 8; drag.revealed = true
        let outside = root.appendingPathComponent("Drag contract.pdf")
        try Data("%PDF".utf8).write(to: outside)
        let old = try adoptedNote("drag-old", "dragbasketword old note")
        let note = try adoptedNote("drag-note", "drag note words")
        for url in [old, outside, note] {
            if isText(url) { history.record(url, text: try String(contentsOf: url, encoding: .utf8)) }
            _ = drag.hang(url, quietly: true)
        }
        pump(0.7); drag.closeGaps()
        func id(_ url: URL) -> UUID { drag.items.first { $0.url == url }!.id }
        try check(drag.dragOperations(for: id(outside)) == .copy, "A file from Finder drags out as a copy only: the original stays")
        try check(drag.dragOperations(for: id(note)) == [.copy, .move, .delete], "EnCopy's own note on the line can still be moved or trashed")
        drag.query = "dragbasketword"
        guard let card = drag.basketCards.first(where: { $0.url == old }) else { throw CheckFailed(message: "The old note is not in the basket") }
        try check(drag.dragOperations(for: card.id) == .copy, "A basket card drags out as a copy: it stays in the history")
    }

    /// Review #1: holding a card, or Markup, never rewrites or opens a file
    /// that is not a picture: a PDF, a folder or an app gets Quick Look.
    func checkMarkupOnlyPictures() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let edit = line()
        let pdf = root.appendingPathComponent("Markup contract.pdf"); try Data("%PDF".utf8).write(to: pdf)
        let app = root.appendingPathComponent("Markup Tool.app", isDirectory: true)
        try FileManager.default.createDirectory(at: app, withIntermediateDirectories: true)
        // A screenshot left on the Desktop: since review 2, only EnCopy's own
        // pictures and those are marked up in place.
        let picture = try png("Markup picture.png", in: root.appendingPathComponent("desktop", isDirectory: true))
        let note = try adoptedNote("markup-note", "markup note words")
        for url in [pdf, app, picture, note] { _ = edit.hang(url, quietly: true) }
        func id(_ url: URL) -> UUID { edit.items.first { $0.url == url }!.id }
        try check(edit.editAction(for: id(pdf)) == .quickLook, "Holding a PDF from Finder shows it; it is never rewritten")
        try check(edit.editAction(for: id(app)) == .quickLook, "Holding an app shows it; it is never opened")
        try check(edit.editAction(for: id(picture)) == .markup, "Holding a picture still opens Markup")
        try check(edit.editAction(for: id(note)) == .open, "Holding a note still opens it to edit")
        try check(!edit.canMarkUp(id(pdf)) && edit.canMarkUp(id(picture)), "The Markup menu item is only for pictures")
    }

    /// Review #7: a screenshot put back from Recently Cleared is known to the
    /// history again, so the screenshot watcher does not take it for a new one.
    func checkPutBackNotNew() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("PutBack shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("putback-new.sqlite"))
        let shots = Line(pasteboard: pasteboard, desktop: root, history: history)
        shots.clearing = Clearing(folder: root.appendingPathComponent("cleared-new", isDirectory: true))
        let old = try png("Old shot.png", in: Inbox.folder)
        history.recordImage(old, text: "")
        guard let batch = shots.clearing.clear([old], reason: "manual", history: history) else { throw CheckFailed(message: "Nothing was cleared") }
        try check(!shots.isKnown(old), "Cleared, it is no longer in the history")
        shots.putBack(batch: batch.id)
        try check(FileManager.default.fileExists(atPath: old.path) && shots.isKnown(old), "Put back, it is known again: not a new screenshot")
        let fresh = try png("Fresh shot.png", in: Inbox.folder)
        try check(!shots.isKnown(fresh), "A screenshot just taken is new")
    }

    /// Review #13: clearing re-checks what is on the line at that moment, and
    /// a row whose file is already gone is cleared too.
    func checkCleanupAtClearTime() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("cleanup-fresh.sqlite"))
        let tidy = Line(pasteboard: pasteboard, desktop: root, history: history)
        tidy.maxItems = 100; tidy.visibleCount = 8
        tidy.clearing = Clearing(folder: root.appendingPathComponent("cleared-fresh", isDirectory: true))
        let back = try adoptedNote("fresh-back", "comes back to the line")
        let ghost = try adoptedNote("fresh-ghost", "deleted in Finder")
        for url in [back, ghost] { history.record(url, text: try String(contentsOf: url, encoding: .utf8)) }
        let picked = tidy.cleanupCandidates(CleanupFilter())
        try check(Set(picked) == [back, ghost], "Both are candidates while in the basket")
        _ = tidy.hang(back, quietly: true)                      // used again meanwhile: back on the line
        try FileManager.default.removeItem(at: ghost)            // deleted in Finder meanwhile
        tidy.cleanUp(picked)
        try check(FileManager.default.fileExists(atPath: back.path) && history.contains(back),
                  "What came back to the line since the panel listed it is not cleared")
        try check(!history.contains(ghost) && tidy.cleanupCandidates(CleanupFilter()).isEmpty,
                  "A row whose file is already gone is cleared, not left behind")
    }

    /// Review #3: a copied picture keeps the app it came from and that app's
    /// rule, so "clear after an hour" and cleanup by app reach pictures too.
    func checkCopiedImageApp() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        ClipboardWatcher.imagesEnabled = true
        ClipboardWatcher.appRules = ["com.example.chat": .hourly]
        let saved = Inbox.folder
        defer { Inbox.folder = saved; ClipboardWatcher.appRules = [:] }
        Inbox.folder = DataFolder.url.appendingPathComponent("Copied app shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("image-app.sqlite"))
        var hung: [URL] = []
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { "com.example.chat" },
                                       onNew: { hung.append($0) }, onAgain: { _ in })
        watcher.start()
        defer { watcher.stop() }
        let image = NSImage(contentsOf: try png("Chat picture.png", red: 0.4))!
        pasteboard.clearContents(); pasteboard.writeObjects([image])
        watcher.check()
        pump(1.5)
        guard let url = hung.first else { throw CheckFailed(message: "The copied picture did not hang") }
        try check(history.app(of: url) == "com.example.chat", "A copied picture keeps the app it was copied from")
        try check(history.ttl(of: url) == ClipboardWatcher.hourly, "A picture from an hourly app is cleared after an hour, like its text")
    }

    /// Review #4: a picture or video copied in Finder goes by the Images
    /// switch, not Files, and auto-generated copies are not files to keep.
    func checkFilesRespectImages() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        ClipboardWatcher.imagesEnabled = false
        let history = History(file: root.appendingPathComponent("files-images.sqlite"))
        var hung: [URL] = []
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { "com.apple.finder" },
                                       onNew: { hung.append($0) }, onAgain: { _ in })
        let picture = try png("Finder picture.png")
        let movie = root.appendingPathComponent("Finder clip.mp4"); try Data([0, 0, 0, 24]).write(to: movie)
        let doc = root.appendingPathComponent("Finder notes.pdf"); try Data("%PDF".utf8).write(to: doc)
        for url in [picture, movie, doc] {
            pasteboard.clearContents(); pasteboard.writeObjects([url as NSURL]); watcher.check()
        }
        try check(hung.count == 1 && samePaths(FileList.paths(hung[0]), [doc]), "With Images off, pictures and videos copied in Finder are not kept; a PDF is")
        ClipboardWatcher.imagesEnabled = true
        pasteboard.clearContents(); pasteboard.writeObjects([picture as NSURL]); watcher.check()
        try check(hung.last == picture, "With Images on, a picture copied in Finder hangs")
        let item = NSPasteboardItem()
        item.setString(doc.absoluteString, forType: .fileURL)
        item.setData(Data(), forType: .init("org.nspasteboard.AutoGeneratedType"))
        pasteboard.clearContents(); pasteboard.writeObjects([item]); watcher.check()
        try check(hung.count == 2, "A copy marked auto-generated is not kept")
        ClipboardWatcher.imagesEnabled = false
    }

    /// Review #11: copying again a file already on the line, or the picture
    /// copied last that has since fallen into the basket, brings it back to
    /// the head instead of being ignored.
    func checkRecopyComesBack() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        ClipboardWatcher.imagesEnabled = true
        let saved = Inbox.folder
        defer { Inbox.folder = saved; ClipboardWatcher.imagesEnabled = false }
        Inbox.folder = DataFolder.url.appendingPathComponent("Recopy shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("recopy.sqlite"))
        var new: [URL] = [], again: [URL] = []
        var onLine: Set<URL> = []
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { "com.apple.finder" },
                                       onNew: { new.append($0); onLine.insert($0) }, onAgain: { again.append($0) })
        watcher.isOnLine = { onLine.contains($0) }
        watcher.start()
        defer { watcher.stop() }
        let doc = root.appendingPathComponent("Recopy plan.pdf"); try Data("%PDF".utf8).write(to: doc)
        pasteboard.clearContents(); pasteboard.writeObjects([doc as NSURL]); watcher.check()
        pasteboard.clearContents(); pasteboard.setString("something else", forType: .string); watcher.check()
        pasteboard.clearContents(); pasteboard.writeObjects([doc as NSURL]); watcher.check()
        let docList = new.first(where: isFileList)
        try check(new.filter(isFileList).count == 1 && docList.map { samePaths(FileList.paths($0), [doc]) } == true && again == [docList!],
                  "A file copied again comes back to the head, as the list kept for it, not a second one")

        let image = NSImage(contentsOf: try png("Recopy picture.png", red: 0.3))!
        pasteboard.clearContents(); pasteboard.writeObjects([image]); watcher.check(); pump(1.5)
        guard let picture = new.last, !isFileList(picture) else { throw CheckFailed(message: "The picture did not hang") }
        onLine.remove(picture)                                   // it fell into the basket
        pasteboard.clearContents(); pasteboard.setString("more words", forType: .string); watcher.check()
        pasteboard.clearContents(); pasteboard.writeObjects([image]); watcher.check(); pump(1.5)
        try check(again.last == picture, "The same picture copied again comes back from the basket")
    }

    /// T-046 (was review #9 on shelves): with the list open, typing narrows
    /// the list across the whole basket, the groups follow, the line stays
    /// as it hangs, and clearing the words brings the whole list back.
    func checkBasketListSearch() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("shelf-search.sqlite"))
        let shelves = Line(pasteboard: pasteboard, desktop: root, history: history)
        shelves.maxItems = 1; shelves.visibleCount = 8; shelves.revealed = true
        let now = Date(), start = Calendar.current.startOfDay(for: now)
        let todayA = try adoptedNote("ss-today-a", "alphaword from today")
        let todayB = try adoptedNote("ss-today-b", "betaword from today")
        let yesterdayA = try adoptedNote("ss-yesterday-a", "alphaword from yesterday")
        let hanging = try adoptedNote("ss-hanging", "alphaword hanging on the line")
        for url in [yesterdayA, todayB, todayA, hanging] { _ = shelves.hang(url, quietly: true) }
        pump(0.7); shelves.closeGaps()
        history.setUsed(yesterdayA, start.addingTimeInterval(-43200))
        history.setUsed(todayB, now.addingTimeInterval(-120)); history.setUsed(todayA, now.addingTimeInterval(-60))
        func shownURLs() -> [URL] { shelves.shown.filter { !$0.falling }.map(\.url) }
        shelves.openBasket()
        try check(shelves.basketRows.map(\.url) == [todayA, todayB, yesterdayA], "The list holds the three in the basket")
        shelves.query = "zzzznothing"
        try check(shelves.basketRows.isEmpty && shelves.basketGroups.isEmpty, "Nothing matches, no groups")
        shelves.query = ""
        try check(shelves.basketRows.map(\.url) == [todayA, todayB, yesterdayA], "Clearing the words brings the whole list back")
        shelves.query = "alphaword"
        try check(shelves.basketRows.map(\.url) == [todayA, yesterdayA]
                  && shelves.basketGroups.map(\.title) == [L("Today"), L("Yesterday")],
                  "Typing narrows the list to today's and yesterday's matches, not the line's; the groups follow")
        try check(shownURLs() == [hanging], "The line stays as it hangs while the list takes the words")
        shelves.query = ""; shelves.closeBasket()
    }

    /// Review #14: Tab narrows by kind before the line's width cuts the
    /// basket, so a picture behind eight notes still shows under Images.
    func checkKindBeforeLimit() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Kind shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("kind-limit.sqlite"))
        let kinds = Line(pasteboard: pasteboard, desktop: root, history: history)
        kinds.maxItems = 1; kinds.visibleCount = 8; kinds.revealed = true
        let now = Date()
        let picture = try png("Kind picture.png", in: Inbox.folder)
        history.recordImage(picture, text: "kindword picture")
        history.setUsed(picture, now.addingTimeInterval(-3000))
        var notes: [URL] = []
        for i in 0..<9 {
            let note = try adoptedNote("kind-note-\(i)", "kindword note \(i)")
            history.record(note, text: "kindword note \(i)")
            history.setUsed(note, now.addingTimeInterval(-Double(100 + i)))
            notes.append(note)
        }
        kinds.openBasket()
        kinds.kindFilter = .image
        try check(kinds.basketRows.map(\.url) == [picture], "Tab to Images in the list shows the picture behind nine notes")
        kinds.kindFilter = nil
        try check(kinds.basketRows.count == 10, "Tab back to all shows the ten")
        kinds.closeBasket()
        kinds.query = "kindword"
        kinds.kindFilter = .image
        try check(kinds.shown.map(\.url) == [picture] && kinds.matchCount == 1, "A search under Images finds the picture and counts one")
        kinds.query = ""; kinds.kindFilter = nil
    }

    /// Review #8: a picture of EnCopy's own on the line is found by the words
    /// the history keeps for it, read from its original even after shrinking.
    func checkKeptImageSearchOnLine() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Search shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("search-kept.sqlite"))
        let finder = Line(pasteboard: pasteboard, desktop: root, history: history)
        finder.maxItems = 100; finder.visibleCount = 8; finder.revealed = true
        let shot = try png("Shrunk shot.png", in: Inbox.folder)
        history.recordImage(shot, text: "zebrawordfromoriginal")
        history.setShrunk(shot, true)
        _ = finder.hang(shot, quietly: true)
        pump(1.0)
        finder.query = "zebrawordfromoriginal"
        try check(finder.shown.contains { $0.url == shot }, "A shrunk picture back on the line is found by its original's words")
        finder.query = ""
    }

    /// Review #12: Quick Look goes through what is shown, in the order it
    /// hangs on screen, basket cards and Tab's narrowing included.
    func checkQuickLookOrder() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("quicklook.sqlite"))
        let look = Line(pasteboard: pasteboard, desktop: root, history: history)
        look.maxItems = 3; look.visibleCount = 8; look.revealed = true
        let notes = try (0..<4).map { try adoptedNote("ql-\($0)", "qlword note \($0)") }
        for note in notes { history.record(note, text: try String(contentsOf: note, encoding: .utf8)); _ = look.hang(note, quietly: true) }
        pump(0.7); look.closeGaps()
        // On the line: 3 2 1, newest at the left; note 0 fell into the basket.
        func id(_ url: URL) -> UUID { look.shown.first { $0.url == url }!.id }
        let onLine = look.quickLookItems(for: id(notes[2]))
        try check(onLine?.urls == [notes[3], notes[2], notes[1]] && onLine?.index == 1, "On the line, Quick Look goes left to right as they hang")
        look.query = "qlword"
        guard let basketCard = look.basketCards.first(where: { $0.url == notes[0] }) else { throw CheckFailed(message: "Note 0 is not in the basket") }
        let found = look.quickLookItems(for: basketCard.id)
        try check(found.map { $0.urls.contains(notes[0]) && $0.urls[$0.index] == notes[0] } == true, "A basket card found by a search opens in Quick Look")
        look.query = ""
    }

    /// Review #15: shrinking keeps a picture's own format, and the daily run
    /// does the decoding off the main thread.
    func checkShrinkOffMain() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Shrink format shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("shrink-format.sqlite"))
        let keep = Line(pasteboard: pasteboard, desktop: root, history: history)
        keep.clearing = Clearing(folder: root.appendingPathComponent("cleared-shrink-format", isDirectory: true))
        let png = try bigShot("Shrink me.png")
        let jpg = Inbox.folder.appendingPathComponent("Shrink me.jpg")
        let source = CGImageSourceCreateWithURL(png as CFURL, nil)!
        let output = CGImageDestinationCreateWithURL(jpg as CFURL, UTType.jpeg.identifier as CFString, 1, nil)!
        CGImageDestinationAddImageFromSource(output, source, 0, nil)
        guard CGImageDestinationFinalize(output) else { throw CheckFailed(message: "Could not write the JPEG") }
        try FileManager.default.removeItem(at: png)
        history.recordImage(jpg, text: "jpeg words")
        history.setUsed(jpg, Date().addingTimeInterval(-40 * 86400))
        let before = (try FileManager.default.attributesOfItem(atPath: jpg.path)[.size] as? Int) ?? 0
        keep.runRetention(inBackground: true)
        try check(!history.kept().contains { $0.url == jpg && $0.shrunk }, "The daily run returns before the picture is shrunk")
        pump(3)
        let data = try Data(contentsOf: jpg)
        try check(history.kept().contains { $0.url == jpg && $0.shrunk } && data.count < before, "Then it is shrunk and marked")
        try check(data.prefix(2) == Data([0xFF, 0xD8]), "A JPEG stays a JPEG when shrunk")
    }

    /// T-040: how long the line shows something new is a setting: short by
    /// default, one of a few choices; anything else stored falls back.
    func checkPeekDuration() throws {
        UserDefaults.standard.removeObject(forKey: "peekSeconds")
        try check(Line.peekSeconds == 1.5, "Out of the box the line shows something new for 1.5 seconds")
        try check(Line.peekChoices == [0.8, 1.5, 2.5, 4], "The choices are 0.8, 1.5, 2.5 and 4 seconds")
        Line.peekSeconds = 0.8
        try check(Line.peekSeconds == 0.8, "A shorter time is kept")
        UserDefaults.standard.set(7.0, forKey: "peekSeconds")
        try check(Line.peekSeconds == 1.5, "A time that is not one of the choices falls back to the default")
        UserDefaults.standard.removeObject(forKey: "peekSeconds")
    }

    /// T-042: every card can show the app it was copied from, as a small
    /// icon; it is a setting, on unless turned off.
    func checkSourceAppBadge() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        try check(Line.showsSourceApp, "Source app icons show out of the box")
        let history = History(file: root.appendingPathComponent("badge.sqlite"))
        let badge = Line(pasteboard: pasteboard, desktop: root, history: history)
        badge.maxItems = 100; badge.visibleCount = 8
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Badge shots", isDirectory: true)
        let note = try adoptedNote("badge-note", "badge words")
        history.record(note, text: "badge words", app: "com.apple.finder")
        let picture = try png("Badge picture.png", in: Inbox.folder)
        history.recordImage(picture, text: nil); history.setApp(picture, "com.apple.Safari")
        let unknown = try adoptedNote("badge-unknown", "no app")
        history.record(unknown, text: "no app")
        try check(badge.sourceApp(of: note) == "com.apple.finder" && badge.sourceApp(of: picture) == "com.apple.Safari",
                  "A note and a picture each know the app they came from")
        try check(badge.sourceApp(of: unknown) == nil, "Something with no app recorded shows no icon")
        try check(AppIcons.icon(for: "com.apple.finder") != nil, "An installed app's icon is found")
        try check(AppIcons.icon(for: "com.example.not-installed") == nil, "An app not on this Mac has no icon")
        Line.showsSourceApp = false
        try check(!Line.showsSourceApp, "The icons can be turned off")
        Line.showsSourceApp = true
    }

    /// T-041: six themes, paper by default; the choice is kept, and a theme
    /// name this version does not know falls back to paper.
    func checkThemes() throws {
        UserDefaults.standard.removeObject(forKey: "theme")
        try check(Theme.current == .paper && Theme.allCases.count == 6, "Paper out of the box, five more to choose from")
        let themed = line()
        try check(themed.theme == .paper, "A new line starts in the chosen theme")
        themed.theme = .pixel
        let next = Line(pasteboard: pasteboard, desktop: root)
        try check(Theme.current == .pixel && next.theme == .pixel, "A theme picked on the line is kept for next time")
        UserDefaults.standard.set("vaporwave", forKey: "theme")
        try check(Theme.current == .paper, "An unknown theme falls back to paper")
        try check(Set(Theme.allCases.map(\.title)).count == 6, "Each theme has its own name")
        UserDefaults.standard.removeObject(forKey: "theme")
    }

    /// Review 2, finding 1: what the clipboard brings always hangs, even
    /// though it is written to the history first; only a file a folder
    /// watcher reports that the history already knows (put back) does not.
    func checkWhatHangs() throws {
        let history = History(file: root.appendingPathComponent("what-hangs.sqlite"))
        let hangs = Line(pasteboard: pasteboard, desktop: root, history: history)
        let known = try adoptedNote("hangs-known", "known words")
        history.record(known, text: "known words")
        let fresh = try png("Hangs fresh.png")
        try check(hangs.shouldHang(known, from: .clipboard), "A copy hangs, though the history already has it")
        try check(!hangs.shouldHang(known, from: .screenshots) && !hangs.shouldHang(known, from: .folder),
                  "A file put back is not a new screenshot or a new file in a watched folder")
        try check(hangs.shouldHang(fresh, from: .screenshots) && hangs.shouldHang(fresh, from: .folder), "A new screenshot hangs")
    }

    /// Review 2, finding 2: copying again never turns something kept for good
    /// into something that expires; it can only take an expiry away.
    func checkRecopyNeverShortens() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        let history = History(file: root.appendingPathComponent("recopy-ttl.sqlite"))
        var app = "com.apple.Safari"
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { app },
                                       onNew: { _ in }, onAgain: { _ in })
        pasteboard.clearContents(); pasteboard.setString("440912", forType: .string); watcher.check()
        guard let note = history.note(holding: "440912") else { throw CheckFailed(message: "The note was not kept") }
        try check(history.ttl(of: note) == nil, "Copied in Safari, the number is kept for good")
        app = "com.apple.MobileSMS"
        pasteboard.clearContents(); pasteboard.setString("440912", forType: .string); watcher.check()
        try check(history.ttl(of: note) == nil, "Copied again in Messages, it is still kept for good, not a ten-minute code")
        ClipboardWatcher.appRules = ["com.example.chat": .hourly]
        defer { ClipboardWatcher.appRules = [:] }
        app = "com.example.chat"
        pasteboard.clearContents(); pasteboard.setString("440912", forType: .string); watcher.check()
        try check(history.ttl(of: note) == nil, "Copied again in an hourly app, it is still kept for good")
    }

    /// Review 2, finding 3: while Quick Look borrows the keyboard, the search
    /// stays; any other loss of the keyboard ends it as before.
    func checkSearchStaysForQuickLook() throws {
        let bar = SearchBar(line: line())
        var ended = 0
        bar.onEnd = { ended += 1 }
        // On the named screen, out of the way, as the checks window is.
        let named = ProcessInfo.processInfo.environment["ENCOPY_CHECKS_SCREEN"]
        let screen = named.flatMap { name in NSScreen.screens.first { $0.localizedName == name } }
        let base = screen?.frame.origin ?? .zero
        bar.show(below: NSRect(x: base.x + 200, y: base.y + 600, width: 800, height: 200))
        pump(0.3)
        bar.keepsOpen = { true }
        bar.windowDidResignKey(Notification(name: NSWindow.didResignKeyNotification))
        try check(ended == 0, "Losing the keyboard to Quick Look does not end the search")
        bar.keepsOpen = { false }
        bar.windowDidResignKey(Notification(name: NSWindow.didResignKeyNotification))
        try check(ended == 1, "Losing it to anything else ends the search, as before")
        bar.hide()
    }

    /// Review 2, finding 4: changing the filter cancels a confirmation, and
    /// what is cleared is worked out from the filter as it is now.
    func checkCleanupConfirmFresh() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("cleanup-confirm.sqlite"))
        let tidy = Line(pasteboard: pasteboard, desktop: root, history: history)
        tidy.clearing = Clearing(folder: root.appendingPathComponent("cleared-confirm", isDirectory: true))
        let keep = try adoptedNote("confirm-keep", "words to keep")
        let drop = try adoptedNote("confirm-drop", "tmpword to drop")
        for url in [keep, drop] { history.record(url, text: try String(contentsOf: url, encoding: .utf8)) }
        let model = CleanupModel(line: tidy)
        model.filter = CleanupFilter()
        model.confirming = true
        model.filter.text = "tmpword"
        try check(!model.confirming, "Changing the filter cancels the confirmation at once")
        model.clear()
        try check(history.contains(keep) && !history.contains(drop), "Clearing right away clears what the new filter picks, not the old list")
    }

    /// Review 2, finding 5: a picture cleared while the daily run shrinks it
    /// in the background is not written back; an original already deleted
    /// is not waiting to be shrunk for ever.
    func checkShrinkRespectsClearing() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Shrink race shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("shrink-race.sqlite"))
        let keep = Line(pasteboard: pasteboard, desktop: root, history: history)
        keep.clearing = Clearing(folder: root.appendingPathComponent("cleared-race", isDirectory: true))
        let shot = try bigShot("Race me.png")
        history.recordImage(shot, text: "race words")
        history.setUsed(shot, Date().addingTimeInterval(-40 * 86400))
        keep.runRetention(inBackground: true)
        keep.cleanUp([shot])                                   // cleared before the background work is done
        pump(3)
        try check(!FileManager.default.fileExists(atPath: shot.path) && !history.contains(shot),
                  "A picture cleared meanwhile is not written back")
        let gone = try bigShot("Deleted in Finder.png")
        history.recordImage(gone, text: "gone words")
        history.setUsed(gone, Date().addingTimeInterval(-40 * 86400))
        try FileManager.default.removeItem(at: gone)
        let plan = Retention.plan(history: history, onLine: [], isKeptImage: keep.isKeptImage, cap: Line.originalsCap)
        try check(!plan.shrunk.contains(gone), "An original deleted in Finder is not waiting to be shrunk")
        keep.runRetention()
        try check(!history.contains(gone), "Its row is cleared")
    }

    /// Review 2, finding 6: with Files off and Images on, a picture copied in
    /// Finder still hangs; a PDF does not.
    func checkFinderPicturesWithoutFiles() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        ClipboardWatcher.imagesEnabled = true
        ClipboardWatcher.filesEnabled = false
        defer { ClipboardWatcher.imagesEnabled = false; ClipboardWatcher.filesEnabled = true }
        let history = History(file: root.appendingPathComponent("finder-pictures.sqlite"))
        var hung: [URL] = []
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { "com.apple.finder" },
                                       onNew: { hung.append($0) }, onAgain: { _ in })
        let picture = try png("No files picture.png")
        let doc = root.appendingPathComponent("No files notes.pdf"); try Data("%PDF".utf8).write(to: doc)
        for url in [picture, doc] { pasteboard.clearContents(); pasteboard.writeObjects([url as NSURL]); watcher.check() }
        try check(hung == [picture], "With Files off and Images on, a picture from Finder hangs and a PDF does not")
    }

    /// Review 2, finding 8: a picture put back keeps the app it came from.
    func checkPutBackKeepsApp() throws {
        let history = History(file: root.appendingPathComponent("putback-app.sqlite"))
        let clearing = Clearing(folder: root.appendingPathComponent("cleared-app", isDirectory: true))
        let picture = DataFolder.url.appendingPathComponent("putback-app.png")
        try FileManager.default.copyItem(at: try png("putback-app-src.png"), to: picture)
        history.recordImage(picture, text: "app words"); history.setApp(picture, "com.tencent.xinWeChat")
        guard let batch = clearing.clear([picture], reason: "manual", history: history) else { throw CheckFailed(message: "Nothing was cleared") }
        _ = clearing.putBack(batch.id, history: history)
        try check(history.app(of: picture) == "com.tencent.xinWeChat", "A picture put back still knows the app it was copied from")
    }

    /// Review 2, finding 9: Quick Look keeps the screen's order while a
    /// pinned card is still falling.
    func checkQuickLookWithFallingPin() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let look = line()
        let urls = try (0..<3).map { try png("Falling pin \($0).png", red: CGFloat($0) / 3) }
        for url in urls { _ = look.hang(url, quietly: true) }
        func id(_ url: URL) -> UUID { look.items.first { $0.url == url }!.id }
        look.togglePin(id(urls[0]))
        look.discard(id(urls[0]))                       // still falling, holding its place
        try check(look.items.contains { $0.url == urls[0] && $0.falling }, "The pinned card is falling")
        let found = look.quickLookItems(for: id(urls[2]))
        try check(found?.urls == [urls[2], urls[1]] && found?.index == 0, "Quick Look still goes newest first, as on screen")
    }

    /// Review 2, on #10: several cards dragged together may only do what
    /// every one of them may: a Finder file among them makes it a copy.
    func checkGroupDragOperations() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let drag = line()
        let outside = root.appendingPathComponent("Group contract.pdf"); try Data("%PDF".utf8).write(to: outside)
        let note = try adoptedNote("group-note", "group words")
        let other = try adoptedNote("group-other", "other words")
        for url in [outside, note, other] { _ = drag.hang(url, quietly: true) }
        func id(_ url: URL) -> UUID { drag.items.first { $0.url == url }!.id }
        try check(drag.dragOperations(for: [id(note), id(outside)]) == .copy, "A group with a Finder file in it drags out as a copy")
        try check(drag.dragOperations(for: [id(note), id(other)]) == [.copy, .move, .delete], "A group of EnCopy's own can still be moved")
    }

    /// Review 2, on #1: only EnCopy's own pictures and recordings, and
    /// screenshots left on the Desktop, are marked up or trimmed in place;
    /// a picture or video from anywhere else opens in Quick Look.
    func checkEditOnlyOwnPictures() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Edit own shots", isDirectory: true)
        let edit = line()
        let own = try png("Own shot.png", in: Inbox.folder)
        let desktop = root.appendingPathComponent("desktop", isDirectory: true)
        let onDesktop = try png("Desktop shot.png", in: desktop)
        let elsewhere = try png("Someone's photo.png")
        let movie = try self.movie("Holiday.mov")
        let ownMovie = try self.movie("Recording.mov", in: Inbox.folder)
        for url in [own, onDesktop, elsewhere, movie, ownMovie] { _ = edit.hang(url, quietly: true) }
        func id(_ url: URL) -> UUID { edit.items.first { $0.url == url }!.id }
        try check(edit.editAction(for: id(own)) == .markup && edit.editAction(for: id(onDesktop)) == .markup,
                  "EnCopy's own screenshot and one left on the Desktop are marked up")
        try check(edit.editAction(for: id(elsewhere)) == .quickLook, "A picture from Finder is shown, never rewritten")
        try check(edit.editAction(for: id(movie)) == .quickLook, "A video from Finder is shown, never trimmed in place")
        try check(edit.editAction(for: id(ownMovie)) == .trim, "EnCopy's own recording is still trimmed")
    }

    /// Review 2, finding 7: Tab's kind narrows the search itself, so answers
    /// behind a thousand newer matches still show; the list goes all the way.
    func checkSearchBeforeLimit() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("limit.sqlite"))
        let deep = Line(pasteboard: pasteboard, desktop: root, history: history)
        deep.maxItems = 1; deep.visibleCount = 8
        let now = Date(), day: TimeInterval = 86400
        for i in 0..<1005 { history.record(try adoptedNote("limit-\(i)", "zeta newer \(i)")) }
        let old = try adoptedNote("limit-old", "zeta from long ago")
        history.record(old); history.setUsed(old, now.addingTimeInterval(-30 * day))
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Limit check shots", isDirectory: true)
        let picture = try png("Limit receipt.png", in: Inbox.folder)   // the history keeps EnCopy's own pictures
        history.recordImage(picture, text: "zeta receipt"); history.setUsed(picture, now.addingTimeInterval(-20 * day))
        func paths(_ urls: [URL]) -> Set<String> { Set(urls.map(\.standardizedFileURL.path)) }

        deep.openBasket()
        deep.query = "zeta"
        try check(deep.basketRows.count == 1007 && paths(Array(deep.basketRows.suffix(2)).map(\.url)) == paths([picture, old]),
                  "In the list the old matches show, behind a thousand newer ones")
        deep.closeBasket()
        deep.kindFilter = .image
        try check(deep.matchCount == 1 && paths(deep.basketCards.map(\.url)) == paths([picture]),
                  "Tab to images finds the picture behind a thousand matching notes")
        deep.kindFilter = .text
        try check(deep.matchCount == 1006 && deep.basketCards.count == 8, "Tab to text counts every matching note, and shows a screenful")
    }

    /// Review 2, on #5: a code thrown away before its time is not left in
    /// the Trash. It waits for ⌘Z in EnCopy's folder, comes back with its
    /// time, and goes for good when the time is up.
    func checkDiscardedCodesNotInTrash() throws {
        let held = line()
        let code = try adoptedNote("held-code", "Your code is 482913")
        _ = held.hang(code, quietly: true)
        held.history.setTTL(code, 600)
        func id() -> UUID? { held.items.first { $0.url == code && !$0.falling }?.id }
        held.discard(id()!)
        let waiting = (try? FileManager.default.contentsOfDirectory(at: Line.holding, includingPropertiesForKeys: nil)) ?? []
        try check(!FileManager.default.fileExists(atPath: code.path) && waiting.count == 1,
                  "A code thrown away waits in EnCopy's folder, not the Trash")
        held.putBack()
        try check(FileManager.default.fileExists(atPath: code.path) && id() != nil && held.history.ttl(of: code) == 600,
                  "⌘Z brings it back, still clearing by itself")
        held.discard(id()!)
        held.clearExpired(now: Date().addingTimeInterval(700))
        let left = (try? FileManager.default.contentsOfDirectory(at: Line.holding, includingPropertiesForKeys: nil)) ?? []
        held.putBack()
        try check(left.isEmpty && !FileManager.default.fileExists(atPath: code.path) && id() == nil,
                  "When its time is up it is gone for good, and ⌘Z cannot bring it back")
    }

    /// Review 2, on #3: a file copied in an app set to clear in an hour
    /// leaves the line after the hour; the file itself stays. Since T-049 the
    /// card is the list EnCopy keeps of the file.
    func checkCopiedFilesClearToo() throws {
        let files = line()
        let rules = ClipboardWatcher.appRules
        defer { ClipboardWatcher.appRules = rules }
        ClipboardWatcher.appRules = rules.merging(["com.tencent.xinWeChat": .hourly]) { _, new in new }
        var front = "com.tencent.xinWeChat"
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: files.history, isSecureInput: { false }, frontApp: { front },
                                       onNew: { _ = files.hang($0, quietly: true) }, onAgain: { files.rehang($0) })
        watcher.isOnLine = { url in files.items.contains { $0.url == url && !$0.falling } }
        watcher.fileLife = { files.setCardLife($0, $1) }
        let doc = root.appendingPathComponent("Hourly contract.pdf"); try Data("%PDF".utf8).write(to: doc)
        let other = root.appendingPathComponent("Everyday notes.pdf"); try Data("%PDF".utf8).write(to: other)
        // Since T-049 a copied file hangs as the list EnCopy keeps of it.
        func onLine(_ url: URL) -> Bool {
            files.items.contains { !$0.falling && isFileList($0.url) && samePaths(FileList.paths($0.url), [url]) }
        }
        func copy(_ url: URL) { pasteboard.clearContents(); pasteboard.writeObjects([url as NSURL]); watcher.check() }
        copy(doc)
        front = "com.apple.finder"
        copy(other)
        files.clearExpired(now: Date().addingTimeInterval(1800))
        try check(onLine(doc) && onLine(other), "Within the hour both copied files hang")
        files.clearExpired(now: Date().addingTimeInterval(3700))
        try check(!onLine(doc) && onLine(other) && FileManager.default.fileExists(atPath: doc.path),
                  "After the hour the file copied in the hourly app leaves the line; the file itself stays")

        front = "com.tencent.xinWeChat"
        copy(doc)
        front = "com.apple.finder"
        copy(doc)
        files.clearExpired(now: Date().addingTimeInterval(3700))
        try check(onLine(doc), "Copied again in an everyday app, it stays")
    }

    /// Review 2, on #13 and #15: the cleanup panel lists only what is still
    /// on disk, and tells links from text without reading every note.
    func checkCleanupPreviewOnDisk() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("cleanup-disk.sqlite"))
        let tidy = Line(pasteboard: pasteboard, desktop: root, history: history)
        tidy.maxItems = 100; tidy.visibleCount = 8
        let kept = try adoptedNote("disk-kept", "still on disk")
        let gone = try adoptedNote("disk-gone", "deleted in Finder before the panel opened")
        let link = try adoptedNote("disk-link", "https://example.com/cleanup-preview")
        for url in [kept, gone, link] { history.record(url, text: try String(contentsOf: url, encoding: .utf8)) }
        try FileManager.default.removeItem(at: gone)
        try check(Set(tidy.cleanupCandidates(CleanupFilter())) == [kept, link] && !history.contains(gone),
                  "A file already gone is not listed as going to Recently Cleared, and its row goes")
        var links = CleanupFilter(); links.kind = .link
        try check(tidy.cleanupCandidates(links) == [link] && Note.peek(link) == nil && Note.peek(kept) == nil,
                  "Links are told from text by the words kept, without reading the notes")
    }

    /// Review 2, on #4: a picture copied in Finder is found by its name and
    /// folder, as a file is; EnCopy's own screenshots only by what they show.
    func checkFinderPicturesByName() throws {
        let named = line()
        let picture = try png("Taxi receipt.png", in: root.appendingPathComponent("Receipts 2027", isDirectory: true))
        let own = try png("Own taxi shot.png", in: DataFolder.url.appendingPathComponent("Name check shots", isDirectory: true))
        for url in [picture, own] { _ = named.hang(url, quietly: true) }      // what hangs is what is indexed
        // Text recognition runs alongside; under load it can take several seconds.
        for _ in 0..<400 where !named.search.matches(picture, SearchIndex.words("taxi")) { pump(0.05) }
        try check(named.search.matches(picture, SearchIndex.words("taxi receipt")) && named.search.matches(picture, SearchIndex.words("receipts 2027")),
                  "A picture from Finder is found by its name and its folder")
        try check(!named.search.matches(own, SearchIndex.words("taxi")), "EnCopy's own screenshot is not found by its file name")
    }

    /// Review 2, on #6: with Tab narrowing the line, "take down everything to
    /// the right" takes only what is shown; hidden cards stay.
    func checkTakeDownOnlyShown() throws {
        let sides = line()
        let urls = [try png("Shown side 0.png", red: 0.1), try adoptedNote("hidden-side-1", "a note Tab hides"),
                    try png("Shown side 2.png", red: 0.5), try adoptedNote("hidden-side-3", "another hidden note"),
                    try png("Shown side 4.png", red: 0.9)]
        for url in urls { _ = sides.hang(url, quietly: true) }
        func id(_ i: Int) -> UUID { sides.items.first { $0.url == urls[i] }!.id }
        sides.kindFilter = .image
        // Shown: 4 2 0, newest at the left; the notes 3 and 1 are hidden between them.
        try check(sides.neighbours(of: id(2), toTheLeft: false).map(\.url) == [urls[0]]
                  && sides.neighbours(of: id(2), toTheLeft: true).map(\.url) == [urls[4]],
                  "With only images shown, the sides hold only the images")
    }

    /// Review 2, on #11: a picture copied again after another one comes
    /// back from where it is, found by its bytes, instead of saved twice.
    func checkRecopyAnyPicture() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        ClipboardWatcher.imagesEnabled = true
        let saved = Inbox.folder
        defer { Inbox.folder = saved; ClipboardWatcher.imagesEnabled = false }
        Inbox.folder = DataFolder.url.appendingPathComponent("Recopy any shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("recopy-any.sqlite"))
        var new: [URL] = [], again: [URL] = []
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { "com.apple.Preview" },
                                       onNew: { new.append($0) }, onAgain: { again.append($0) })
        watcher.start()
        defer { watcher.stop() }
        let first = NSImage(contentsOf: try png("Recopy first.png", red: 0.2))!
        let second = NSImage(contentsOf: try png("Recopy second.png", red: 0.7))!
        for image in [first, second, first] {
            pasteboard.clearContents(); pasteboard.writeObjects([image]); watcher.check(); pump(1.5)
        }
        let files = (try? FileManager.default.contentsOfDirectory(atPath: Inbox.folder.path)) ?? []
        try check(new.count == 2 && again == [new[0]] && files.count == 2,
                  "The first picture copied again after another comes back, not saved a second time")
    }

    /// T-044: ↓ opens the whole basket as a list, grouped by when things were
    /// last used, all the way back; ⏎ pastes the list's pick back to the head.
    func checkBasketList() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("basket-list.sqlite"))
        let basket = Line(pasteboard: pasteboard, desktop: root, history: history)
        basket.maxItems = 1; basket.visibleCount = 8; basket.revealed = true
        let now = Date(), day: TimeInterval = 86400
        let start = Calendar.current.startOfDay(for: now)
        // Twelve in the basket, more than a shelf of 8 held, five of them
        // over a week old: newest first as the list should show them.
        let ages: [(String, Date)] = [
            ("t1", now.addingTimeInterval(-60)), ("t2", now.addingTimeInterval(-120)), ("t3", now.addingTimeInterval(-180)),
            ("y1", start.addingTimeInterval(-day / 3)), ("y2", start.addingTimeInterval(-day / 2)),
            ("w1", start.addingTimeInterval(-3 * day)), ("w2", start.addingTimeInterval(-4 * day)),
            ("m1", start.addingTimeInterval(-40 * day)), ("m2", start.addingTimeInterval(-40 * day - 60)), ("m3", start.addingTimeInterval(-40 * day - 120)),
            ("o1", start.addingTimeInterval(-100 * day)), ("o2", start.addingTimeInterval(-100 * day - 60))]
        var urls: [String: URL] = [:]
        for (name, _) in ages { urls[name] = try adoptedNote("list-\(name)", "list words \(name)"); _ = basket.hang(urls[name]!, quietly: true) }
        let hanging = try adoptedNote("list-hanging", "list words hanging")
        _ = basket.hang(hanging, quietly: true)
        let gone = try adoptedNote("list-gone", "list words gone")
        _ = basket.hang(gone, quietly: true)
        _ = basket.hang(hanging, quietly: true)
        pump(0.7); basket.closeGaps()
        for (name, date) in ages { history.setUsed(urls[name]!, date) }
        history.setUsed(gone, now)
        try FileManager.default.removeItem(at: gone)
        basket.countBasket()
        let bar = SearchBar(line: basket)
        bar.onVertical = { basket.verticalArrow($0) }
        func arrow(_ code: Int) -> NSEvent {
            NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: 0, context: nil,
                             characters: "", charactersIgnoringModifiers: "", isARepeat: false, keyCode: UInt16(code))!
        }
        let expected = ages.map { urls[$0.0]! }
        try check(basket.items.filter { !$0.falling }.map(\.url) == [hanging], "Only one hangs; the rest are in the basket")
        try check(bar.takes(arrow(kVK_DownArrow), composing: false) && basket.basketOpen, "↓ opens the basket list")
        try check(basket.basketRows.map(\.url) == expected,
                  "The list holds all twelve, newest first, past a shelf's eight and back past a week; not what hangs, not a gone file")
        try check(basket.basketGroups.map(\.rows.count) == [3, 2, 2, 3, 2], "Grouped today, yesterday, this week, then by month")
        try check(Array(basket.basketGroups.prefix(3).map(\.title)) == [L("Today"), L("Yesterday"), L("This week")],
                  "The first groups are named today, yesterday, this week")
        try check(basket.basketGroups[3].title != basket.basketGroups[4].title, "Two months back are two groups")
        try check(basket.basketPick == urls["t1"], "The newest in the basket is picked")
        for _ in 0..<20 { _ = bar.takes(arrow(kVK_DownArrow), composing: false) }
        try check(basket.basketPick == urls["o2"], "↓ goes all the way to the oldest and stops there")
        for _ in 0..<11 { _ = bar.takes(arrow(kVK_UpArrow), composing: false) }
        try check(basket.basketOpen && basket.basketPick == urls["t1"], "↑ comes back up the list")
        _ = bar.takes(arrow(kVK_UpArrow), composing: false)
        try check(!basket.basketOpen && basket.picked?.url == hanging, "↑ on the top row closes the list and picks on the line again")
        _ = bar.takes(arrow(kVK_DownArrow), composing: false)
        for _ in 0..<7 { _ = bar.takes(arrow(kVK_DownArrow), composing: false) }
        try check(basket.basketPick == urls["m1"], "The eighth row down is the first of last month's")
        var sent = 0
        let paster = Paster(line: basket, isTrusted: { true }, send: { _ in sent += 1 })
        paster.pastePicked()
        try check(sent == 1 && basket.items.filter { !$0.falling }.last?.url == urls["m1"]
                  && pasteboard.string(forType: .string)?.contains("list words m1") == true,
                  "⏎ pastes the list's pick, and it comes back to the head of the line")
    }

    /// T-045: each row of the list is the paper it would hang on, and says
    /// what it is: a note's lines, a link's site and path, a picture's words.
    func checkBasketPapers() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Basket paper shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("basket-papers.sqlite"))
        let papers = Line(pasteboard: pasteboard, desktop: root, history: history)
        papers.maxItems = 1; papers.visibleCount = 8; papers.revealed = true
        let text = try adoptedNote("paper-text", "Meeting moved to Thursday\nRoom 4B, bring the review notes")
        let code = try adoptedNote("paper-code", "func paste(_ id: UUID) {\n    line.copy(id)\n}")
        let link = try adoptedNote("paper-link", "https://github.com/a1024053774/EnCopy")
        let shot = try png("Paper shot.png", in: Inbox.folder)
        for url in [text, code, link] { _ = papers.hang(url, quietly: true) }
        _ = papers.hang(shot, quietly: true)
        papers.search.onRead(shot, "INVOICE 2038 due Friday")
        let last = try adoptedNote("paper-last", "the one left hanging")
        _ = papers.hang(last, quietly: true)
        pump(0.7); papers.closeGaps()
        papers.openBasket()
        let rows = Set(papers.basketRows.map(\.url))
        try check(rows == [text, code, link, shot], "The four are in the basket list")
        try check(papers.paperCard(of: text) == .text && papers.paperCard(of: code) == .code
                  && papers.paperCard(of: link) == .link && papers.paperCard(of: shot) == .image,
                  "Each row is its own paper: receipt, tractor-feed code, a link's tag, a polaroid")
        let t = papers.basketSummary(text)
        try check(t.title == "Meeting moved to Thursday" && t.detail == "Room 4B, bring the review notes",
                  "A note's row shows its first line and the next")
        let l = papers.basketSummary(link)
        try check(l.title == "github.com" && l.detail == "/a1024053774/EnCopy", "A link's row shows its site and path")
        let s = papers.basketSummary(shot)
        try check(s.title.lowercased().contains("invoice 2038"), "A picture's row shows the words read from it")
        papers.closeBasket()
    }

    /// T-046: in the list, ← → jump between groups, Tab narrows it, ⌘P pins
    /// the pick back onto the line, ⇧⏎ pastes its words only.
    func checkBasketKeys() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let history = History(file: root.appendingPathComponent("basket-keys.sqlite"))
        let keys = Line(pasteboard: pasteboard, desktop: root, history: history)
        keys.maxItems = 1; keys.visibleCount = 8; keys.revealed = true
        let now = Date(), day: TimeInterval = 86400, start = Calendar.current.startOfDay(for: now)
        let ages: [(String, Date)] = [("t1", now.addingTimeInterval(-60)), ("t2", now.addingTimeInterval(-120)),
                                      ("y1", start.addingTimeInterval(-day / 3)), ("y2", start.addingTimeInterval(-day / 2)),
                                      ("m1", start.addingTimeInterval(-40 * day))]
        var urls: [String: URL] = [:]
        for (name, _) in ages { urls[name] = try adoptedNote("keys-\(name)", "keys words \(name)"); _ = keys.hang(urls[name]!, quietly: true) }
        let link = try adoptedNote("keys-link", "https://example.com/keys")
        _ = keys.hang(link, quietly: true)
        let hanging = try adoptedNote("keys-hanging", "keys words hanging")
        _ = keys.hang(hanging, quietly: true)
        pump(0.7); keys.closeGaps()
        for (name, date) in ages { history.setUsed(urls[name]!, date) }
        history.setUsed(link, start.addingTimeInterval(-41 * day))
        keys.maxItems = 3
        let bar = SearchBar(line: keys)
        bar.onVertical = { keys.verticalArrow($0) }
        bar.onMove = { keys.horizontalArrow($0) }
        bar.onTab = { keys.cycleKind($0) }
        func key(_ code: Int, _ flags: NSEvent.ModifierFlags = [], _ chars: String = "") -> NSEvent {
            NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0, windowNumber: 0, context: nil,
                             characters: chars, charactersIgnoringModifiers: chars, isARepeat: false, keyCode: UInt16(code))!
        }
        _ = bar.takes(key(kVK_DownArrow), composing: false)
        try check(keys.basketPick == urls["t1"], "The list opens on the newest")
        _ = bar.takes(key(kVK_RightArrow), composing: false)
        try check(keys.basketPick == urls["y1"], "→ jumps to the first of yesterday")
        _ = bar.takes(key(kVK_RightArrow), composing: false)
        try check(keys.basketPick == urls["m1"], "→ again jumps to the first of the month before")
        _ = bar.takes(key(kVK_RightArrow), composing: false)
        try check(keys.basketPick == urls["m1"], "→ on the last group stays")
        _ = bar.takes(key(kVK_LeftArrow), composing: false)
        _ = bar.takes(key(kVK_DownArrow), composing: false)
        try check(keys.basketPick == urls["y2"], "← goes back a group; ↓ moves inside it")
        _ = bar.takes(key(kVK_LeftArrow), composing: false)
        try check(keys.basketPick == urls["y1"], "← inside a group goes to its first row first")
        _ = bar.takes(key(kVK_Tab), composing: false)
        try check(keys.kindFilter == .text && !keys.basketRows.contains { $0.url == link } && keys.basketRows.count == 5,
                  "Tab narrows the list to text: the link leaves it")
        _ = bar.takes(key(kVK_Tab), composing: false)
        try check(keys.basketRows.map(\.url) == [link], "Tab again narrows it to links")
        keys.kindFilter = nil
        keys.pickBasket(urls["y2"]!)
        keys.pinPick()
        try check(keys.items.first { $0.url == urls["y2"] }?.kept == true && !keys.basketRows.contains { $0.url == urls["y2"] },
                  "⌘P in the list pins its pick back on the line, and it leaves the list")
        keys.pickBasket(urls["t2"]!)
        var sent = 0
        let paster = Paster(line: keys, isTrusted: { true }, send: { _ in sent += 1 })
        paster.pastePicked(plain: true)
        try check(sent == 1 && pasteboard.string(forType: .string) == "keys words t2" && pasteboard.data(forType: .rtf) == nil
                  && keys.items.filter { !$0.falling }.last?.url == urls["t2"],
                  "⇧⏎ in the list pastes the pick's words only, and it comes back to the head of the line")
    }

    /// T-047: the chips above the list count each kind of what the list
    /// holds, follow the words typed, and narrow it as Tab does; every card
    /// and row is tagged with what it is, code apart from text.
    func checkKindChips() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let saved = Inbox.folder
        defer { Inbox.folder = saved }
        Inbox.folder = DataFolder.url.appendingPathComponent("Kind chip shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("kind-chips.sqlite"))
        let chips = Line(pasteboard: pasteboard, desktop: root, history: history)
        chips.maxItems = 1; chips.visibleCount = 8; chips.revealed = true
        let t1 = try adoptedNote("chip-t1", "chip words budget sheet")
        let t2 = try adoptedNote("chip-t2", "chip words meeting room")
        let code = try adoptedNote("chip-code", "func chip() {\n    return budget\n}")
        let l1 = try adoptedNote("chip-l1", "https://example.com/budget")
        let l2 = try adoptedNote("chip-l2", "https://example.org/other")
        let shot = try png("Chip shot.png", in: Inbox.folder)
        for url in [t1, t2, code, l1, l2] { _ = chips.hang(url, quietly: true) }
        _ = chips.hang(shot, quietly: true)
        chips.search.onRead(shot, "BUDGET 2027 draft")
        let last = try adoptedNote("chip-last", "chip words left hanging")
        _ = chips.hang(last, quietly: true)
        pump(0.7); chips.closeGaps()
        chips.openBasket()
        try check(chips.basketMatchCount == 6 && chips.basketKindCounts[.text] == 3 && chips.basketKindCounts[.link] == 2
                  && chips.basketKindCounts[.image] == 1 && chips.basketKindCounts[.file] == nil,
                  "The chips count six: three text (code among them), two links, one image, no files")
        chips.query = "budget"
        try check(chips.basketMatchCount == 4 && chips.basketKindCounts[.text] == 2 && chips.basketKindCounts[.link] == 1
                  && chips.basketKindCounts[.image] == 1,
                  "Typing narrows the counts with the list: two text, one link, one image match")
        chips.query = ""
        chips.kindFilter = .link
        let byChip = chips.basketRows.map(\.url)
        chips.kindFilter = nil
        chips.cycleKind(1); chips.cycleKind(1)
        try check(chips.kindFilter == .link && chips.basketRows.map(\.url) == byChip && Set(byChip) == [l1, l2],
                  "A click on the links chip and Tab to links show the same two rows")
        try check(chips.basketMatchCount == 6 && chips.basketKindCounts[.text] == 3, "Narrowed to links, the chips still count every kind")
        chips.kindFilter = nil
        try check(chips.tag(of: t1) == .text && chips.tag(of: code) == .code && chips.tag(of: l1) == .link
                  && chips.tag(of: shot) == .image && chips.tag(of: try movie("Chip clip.mov")) == .video
                  && chips.tag(of: try png("Chip file.pdf")) == .file,
                  "Each thing is tagged with what it is: text, code, link, image, video, file")
        chips.closeBasket()
    }

    /// T-049: a file copied in Finder goes into the history and the basket
    /// as its list; it is found there by name and folder and pastes back as
    /// the file; when the file is gone the row says so and nothing pastes;
    /// it expires like text; clearing it never moves the file itself.
    func checkFilesInBasket() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let docs = root.appendingPathComponent("Basket Docs", isDirectory: true)
        try FileManager.default.createDirectory(at: docs, withIntermediateDirectories: true)
        let pdfA = docs.appendingPathComponent("Budget plan 2027.pdf"); try Data("%PDF a".utf8).write(to: pdfA)
        let pdfB = docs.appendingPathComponent("Old contract.pdf"); try Data("%PDF b".utf8).write(to: pdfB)
        let history = History(file: root.appendingPathComponent("files-basket.sqlite"))
        let line = Line(pasteboard: pasteboard, desktop: root, history: history)
        line.maxItems = 1; line.visibleCount = 8; line.revealed = true
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { "com.apple.finder" },
                                       onNew: { _ = line.hang($0, quietly: true) }, onAgain: { _ in })
        func copyFile(_ url: URL) { pasteboard.clearContents(); pasteboard.writeObjects([url as NSURL]); watcher.check() }
        copyFile(pdfA)
        guard let listA = line.items.last?.url, isFileList(listA) else { throw CheckFailed(message: "The file did not hang as a list") }
        try check(history.contains(listA), "A file copied in Finder goes into the history, as its list")
        // T-052, as the Keeping page says: no copy of the file, no link to
        // it, in EnCopy's folder; only the list naming its path.
        let kept = (FileManager.default.enumerator(at: DataFolder.url, includingPropertiesForKeys: nil)?.allObjects as? [URL]) ?? []
        let copies = kept.filter { $0.lastPathComponent == pdfA.lastPathComponent || (try? Data(contentsOf: $0)) == Data("%PDF a".utf8)
            || (try? FileManager.default.destinationOfSymbolicLink(atPath: $0.path)) != nil }
        try check(copies.isEmpty && listA.path.hasPrefix(DataFolder.url.path + "/") && samePaths(FileList.paths(listA), [pdfA]),
                  "EnCopy's folder holds no copy of the file and no link to it, only a list naming its path")
        copyFile(pdfB)
        guard let listB = line.items.last?.url, isFileList(listB), listB != listA else { throw CheckFailed(message: "The second file did not hang") }
        let note = try adoptedNote("files-basket-note", "a note to fill the line")
        _ = line.hang(note, quietly: true)
        pump(0.7); line.closeGaps()
        line.openBasket()
        try check(Set(line.basketRows.map(\.url)) == [listA, listB] && line.basketKindCounts[.file] == 2,
                  "Off a full line, both lists are in the basket, counted under files")
        line.query = "budget"
        try check(line.basketRows.map(\.url) == [listA], "Found in the basket by the file's name")
        line.query = "basket docs"
        try check(Set(line.basketRows.map(\.url)) == [listA, listB], "And by its folder's name")
        line.query = ""
        line.pickBasket(listA)
        var sent = 0
        let paster = Paster(line: line, isTrusted: { true }, send: { _ in sent += 1 })
        paster.pastePicked()
        try check(sent == 1 && samePaths(copiedURLs, [pdfA]) && line.items.filter { !$0.falling }.last?.url == listA,
                  "⏎ pastes the file itself, and its list comes back to the head of the line")
        line.closeBasket()
        let note2 = try adoptedNote("files-basket-note-2", "another note to fill the line")
        _ = line.hang(note2, quietly: true)
        pump(0.7); line.closeGaps()
        try FileManager.default.removeItem(at: pdfB)                 // moved away in Finder
        line.openBasket()
        let rowB = line.basketRows.first { $0.url == listB }
        try check(rowB?.moved == true && line.basketRows.first { $0.url == listA }?.moved == false,
                  "A file moved away leaves its row, marked moved; one still in place is not")
        pasteboard.clearContents(); pasteboard.setString("untouched", forType: .string)
        line.pickBasket(listB)
        paster.pastePicked()
        try check(sent == 1 && pasteboard.string(forType: .string) == "untouched", "A moved file's row pastes nothing")
        line.closeBasket()
        let plan = Retention.plan(history: history, onLine: line.onLinePaths, isKeptImage: line.isKeptImage)
        try check(plan.expired.contains(listB) && !plan.expired.contains(listA), "Retention clears a list whose files are gone, not one in place")
        let later = Retention.plan(history: history, onLine: [], isKeptImage: line.isKeptImage, now: Date().addingTimeInterval(400 * 86400))
        try check(later.expired.contains(listA), "A file unused for over a year expires, like text")
        let clearing = Clearing(folder: root.appendingPathComponent("files-cleared", isDirectory: true))
        clearing.clear([listA, listB], reason: "manual", history: history)
        try check(FileManager.default.fileExists(atPath: pdfA.path) && !FileManager.default.fileExists(atPath: listA.path)
                  && !history.contains(listA),
                  "Clearing moves only the list into 「最近清掉」; the file stays where it is")
    }

    /// T-050: one copy of many files in Finder is one stack, all of them, not
    /// twenty; it pastes back as every file; in the basket it is found by any
    /// file's name, opens to its files, and one of them pastes alone; the same
    /// files again add nothing; moved files leave the stack.
    func checkFileStack() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let docs = root.appendingPathComponent("Stack Docs", isDirectory: true)
        try FileManager.default.createDirectory(at: docs, withIntermediateDirectories: true)
        var files: [URL] = []
        for i in 1...25 {
            let f = docs.appendingPathComponent(String(format: "Report %02d.pdf", i)); try Data("%PDF \(i)".utf8).write(to: f); files.append(f)
        }
        let history = History(file: root.appendingPathComponent("file-stack.sqlite"))
        let line = Line(pasteboard: pasteboard, desktop: root, history: history)
        line.maxItems = 1; line.visibleCount = 8; line.revealed = true
        var again: [URL] = []
        let watcher = ClipboardWatcher(pasteboard: pasteboard, history: history, isSecureInput: { false }, frontApp: { "com.apple.finder" },
                                       onNew: { _ = line.hang($0, quietly: true) }, onAgain: { again.append($0) })
        func copyFiles(_ urls: [URL]) { pasteboard.clearContents(); pasteboard.writeObjects(urls.map { $0 as NSURL }); watcher.check() }
        copyFiles(files)
        guard let stack = line.items.last?.url, isFileList(stack) else { throw CheckFailed(message: "The files did not hang as a stack") }
        try check(line.items.count == 1 && samePaths(FileList.paths(stack), files), "Twenty-five files copied at once hang as one stack of all twenty-five")
        line.copy(line.items[0].id)
        try check(samePaths(copiedURLs, files), "Copying the stack back gives all twenty-five files, in order")
        copyFiles(files.reversed())
        try check(line.items.count == 1 && again == [stack], "The same files copied again, in any order, add nothing: the stack comes back")
        let note = try adoptedNote("stack-note", "a note to fill the line")
        _ = line.hang(note, quietly: true)
        pump(0.7); line.closeGaps()
        line.openBasket()
        line.query = "report 17"
        try check(line.basketRows.map(\.url) == [stack], "In the basket the stack is found by one of its files' names")
        line.query = ""
        try check(line.basketSummary(stack).title == String(format: L("%d files"), 25), "Its row says how many files")
        line.pickBasket(stack)
        line.toggleBasketStack()
        try check(line.basketExpanded == stack, "⌘↓ opens the stack")
        line.verticalArrow(1); line.verticalArrow(1)
        try check(line.basketSubPick.map { samePaths([$0], [files[1]]) } == true, "↓ walks into it, file by file")
        var sent = 0
        let paster = Paster(line: line, isTrusted: { true }, send: { _ in sent += 1 })
        paster.pastePicked()
        try check(sent == 1 && samePaths(copiedURLs, [files[1]]), "⏎ on one file of the stack pastes that file alone")
        line.closeBasket()
        try FileManager.default.removeItem(at: files[0]); try FileManager.default.removeItem(at: files[2])
        try check(FileList.present(stack).count == 23 && line.basketSummary(stack).title == String(format: L("%d files"), 23),
                  "Two files moved away: the stack shows the twenty-three still in place")
    }

    /// T-051: the 「保留」 page shows what the rules would do, the same as the
    /// rules' own plan; how much the originals take; and the cap as set,
    /// which the rules then use.
    func checkRetentionStatus() throws {
        UserDefaults.standard.removePersistentDomain(forName: Bundle.main.bundleIdentifier!)
        UserDefaults.standard.set(true, forKey: "soundOff")
        let saved = Inbox.folder
        defer { Inbox.folder = saved; UserDefaults.standard.removeObject(forKey: "originalsCap") }
        Inbox.folder = DataFolder.url.appendingPathComponent("Status check shots", isDirectory: true)
        let history = History(file: root.appendingPathComponent("retention-status.sqlite"))
        let keep = Line(pasteboard: pasteboard, desktop: root, history: history)
        keep.maxItems = 1; keep.visibleCount = 8; keep.revealed = true
        let a = try png("Status A.png", in: Inbox.folder), b = try png("Status B.png", in: Inbox.folder, red: 0.8)
        let old = try adoptedNote("status-old", "a note unused for over a year")
        for url in [a, b] { _ = keep.hang(url, quietly: true); history.recordImage(url, text: "status words") }
        history.record(old, text: "a note unused for over a year")
        let last = try adoptedNote("status-last", "the one left hanging")
        _ = keep.hang(last, quietly: true)
        pump(0.7); keep.closeGaps()
        let now = Date()
        history.setUsed(old, now.addingTimeInterval(-400 * 86400))
        history.setUsed(a, now.addingTimeInterval(-3 * 86400)); history.setUsed(b, now.addingTimeInterval(-2 * 86400))
        let bytes = [a, b].reduce(Int64(0)) { $0 + Int64((try? $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
        let status = keep.retentionStatus(now: now)
        let plan = Retention.plan(history: history, onLine: keep.onLinePaths, isKeptImage: keep.isKeptImage, now: now, cap: Line.originalsCap)
        try check(status.willExpire == plan.expired.count && status.willExpire == 1 && status.willShrink == 0,
                  "The page says one note will expire and no original shrink, as the rules plan")
        try check(status.originalsBytes == bytes && status.cap == Retention.defaultCap, "It shows what the two originals take, against 1 GB")
        Line.originalsCap = bytes / 2
        let tight = keep.retentionStatus(now: now)
        let tightPlan = Retention.plan(history: history, onLine: keep.onLinePaths, isKeptImage: keep.isKeptImage, now: now, cap: bytes / 2)
        try check(tight.cap == bytes / 2 && tight.willShrink == tightPlan.shrunk.count && tightPlan.shrunk == [a],
                  "A smaller cap set there is what the rules use: the older original would shrink")
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
        // The basket card is the one whose file is the basket's match.
        func basketCard() throws -> GrabView {
            func all(_ sub: NSView) -> [GrabView] {
                (sub as? GrabView).map { [$0] } ?? sub.subviews.flatMap(all)
            }
            guard let card = all(window.contentView!).first(where: { $0.url == peek.basketCards.first?.url }) else {
                throw CheckFailed(message: "No basket card on screen")
            }
            return card
        }
        let card = try basketCard()
        try check(card.url == peekSaved[0], "A basket card drags its file, like any card (T-033)")
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
