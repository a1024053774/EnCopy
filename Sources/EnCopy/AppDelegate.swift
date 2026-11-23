import AppKit
import Carbon
import Combine
import Quartz
import ServiceManagement
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let line = Line()
    private var panel: LinePanel!
    private var statusItem: NSStatusItem!
    private var watcher: ScreenshotWatcher!
    /// In inbox mode, a second watcher on the Desktop. If a macOS version
    /// ignores the screenshot settings (macOS 27 renamed one), captures keep
    /// landing on the Desktop, and they still hang on the line.
    /// Folders chosen in "Also watch folders", like the one another
    /// screenshot app saves to.
    private var folderWatchers: [ScreenshotWatcher] = []
    private var clipboardWatcher: ClipboardWatcher!
    private var noteWatcher: NoteWatcher!
    private var signalSources: [DispatchSourceSignal] = []
    private var hotKey: HotKey?
    private var searchBar: SearchBar!
    private var paster: Paster!
    private var pasteHint: PasteHint!
    private let zoom = Zoom()
    private var cancellables = Set<AnyCancellable>()
    private var mouseTimer: Timer?
    /// Once a minute: copies whose own time ran out are cleared.
    private var expiryTimer: Timer?

    /// Whether the panel is ordered in. It can be in and still tucked away
    /// above the top edge, like an auto-hiding Dock.
    private var isPresent = false
    /// Whether the line has slid down into view.
    private var isRevealed = false
    /// Opened on purpose with the shortcut or the menu: it stays down until
    /// the cursor has visited it and left, or the shortcut is pressed again.
    private var pinned = false
    /// A new screenshot shows itself for a moment, then tucks away.
    private var peekUntil = Date.distantPast
    private var hotZoneSince: Date?
    /// After a click in the menu bar the line stays up there hidden until the
    /// pointer leaves the menu bar, so it does not come back over a menu.
    private var menuBarSuppressed = false
    private var clickMonitors: [Any] = []
    private var awaySince: Date?
    /// Whether the line should be up, if nothing prevents it. A full screen
    /// app on that screen does: the line waits until you leave full screen.
    private var wanted = false
    /// Set when you open the line on purpose, so it stays up while empty.
    private var keepOpen = false
    /// Opened on purpose with the shortcut or the menu, which counts over a
    /// full screen app too, until the line tucks away.
    private var openedOnPurpose = false

    // What brings the line down, from the menu bar's "Bring the line down".
    // Out of the box, the first two are on and the third off.

    /// Resting the pointer in the menu bar. Off, only the shortcut and the
    /// menu open it.
    static var opensFromMenuBar: Bool {
        get { !UserDefaults.standard.bool(forKey: "menuBarRevealOff") }
        set { UserDefaults.standard.set(!newValue, forKey: "menuBarRevealOff") }
    }

    /// Something new hanging shows itself for a moment. Off, it hangs
    /// quietly and waits for you to bring the line down.
    static var peeksAtNew: Bool {
        get { !UserDefaults.standard.bool(forKey: "quietHang") }
        set { UserDefaults.standard.set(!newValue, forKey: "quietHang") }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let host = NSHostingView(rootView: LineView(line: line))
        host.sizingOptions = []
        panel = LinePanel(content: host)
        panel.placeOnScreen()
        updateCapacity()

        if Inbox.isEnabled { Inbox.apply() }
        restoreSettingsOnTermination()
        startWatcher()
        clipboardWatcher = ClipboardWatcher(history: line.history,
                                           onNew: { [weak self] url in
                    guard let self, !self.line.isKnown(url) else { return }
                    self.hangCapture(url)
                },
                                           onAgain: { [weak self] url in self?.hangAgain(url) })
        clipboardWatcher.isOnLine = { [weak self] url in
            self?.line.items.contains { $0.url.standardizedFileURL == url.standardizedFileURL && !$0.falling } ?? false
        }
        if ClipboardWatcher.isEnabled { clipboardWatcher.start() }
        line.clearExpired()
        retentionIfDue()
        let expiry = Timer(timeInterval: 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                // Not while you are picking: nothing leaves under your hand.
                guard let self, self.searchBar?.isShown != true else { return }
                self.line.clearExpired()
                self.retentionIfDue()
            }
        }
        RunLoop.main.add(expiry, forMode: .common)
        expiryTimer = expiry
        noteWatcher = NoteWatcher { [weak self] in self?.line.refreshNotes() }
        noteWatcher.start()

        searchBar = SearchBar(line: line)
        searchBar.onMove = { [weak self] in self?.line.movePick($0) }
        searchBar.onReturn = { [weak self] in self?.paster.pastePicked() }
        searchBar.onShelf = { [weak self] step in
            self?.line.moveShelf(step)
            self?.line.pickNewest()
        }
        searchBar.onTab = { [weak self] step in
            self?.line.cycleKind(step)
            self?.line.pickNewest()
        }
        searchBar.onPlainReturn = { [weak self] in self?.paster.pastePicked(plain: true) }
        searchBar.onPin = { [weak self] in if let id = self?.line.picked?.id { self?.line.togglePin(id) } }
        // ⌘Y opens Quick Look on the pick, and closes it again.
        searchBar.onQuickLook = { [weak self] in
            if QuickLook.shared.isOpen { QuickLook.shared.close(); return }
            if let id = self?.line.picked?.id { self?.line.quickLook(id) }
        }
        QuickLook.shared.onClosed = { [weak self] in self?.searchBar.refocus() }
        searchBar.onEnd = { [weak self] in self?.endSearch(tuckAway: true) }
        paster = Paster(line: line)
        line.onPaste = { [weak self] id in self?.paster.paste(id) }
        paster.tuckAway = { [weak self] in self?.endSearch(tuckAway: true) }
        paster.flyOut = { [weak self] id in self?.flyToPointer(id) }
        paster.showHint = { [weak self] in
            guard let self else { return }
            self.pasteHint.show(on: self.panel.screen)
        }
        pasteHint = PasteHint()
        registerShortcut()

        setUpStatusItem()
        watchMenuBarClicks()

        // Hang on EnCopy, in the menu of images in Finder and other apps.
        NSApp.servicesProvider = self
        NSUpdateDynamicServices()

        Markup.shared.onSaved = { [weak self] url in self?.line.reloadThumbnail(for: url) }
        Share.shared.clearAll()
        Trim.shared.onSaved = { [weak self] url in self?.line.reloadThumbnail(for: url) }
        panel.onScroll = { [weak self] event in
            // Sideways on a trackpad, or the wheel of a mouse.
            let delta = abs(event.scrollingDeltaX) >= abs(event.scrollingDeltaY)
                ? event.scrollingDeltaX : event.scrollingDeltaY
            // Older items hang to the right: the line moves with the fingers.
            self?.line.scroll(by: -(event.hasPreciseScrollingDeltas ? delta : delta * 12))
        }
        line.onFall = { [weak self] item in self?.fall(item) }
        line.onHold = { [weak self] id in self?.hold(id) }
        line.onHangBack = { id in Held.hangBack(id) }

        line.$items
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.itemsChanged() }
            .store(in: &cancellables)
        // Photos kept from last time show themselves for a moment.
        if line.liveCount > 0 { comeDown(on: nil) }

        // Entering or leaving full screen switches Space. Check again once the
        // switch animation has settled.
        let workspace = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.activeSpaceDidChangeNotification, NSWorkspace.didActivateApplicationNotification] {
            workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    // Opened on purpose counts for the Space it was opened on.
                    if name == NSWorkspace.activeSpaceDidChangeNotification { self?.openedOnPurpose = false }
                    self?.refresh()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) {
                        self?.refresh()
                        self?.keepOnActiveSpace()
                    }
                }
            }
        }

        NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.panel.placeOnScreen()
                self?.updateCapacity()
            }
        }

        if !Inbox.wasOffered {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [weak self] in
                self?.offerInbox()
                self?.explainRecording()
            }
        } else if !Self.recordingExplained {
            // Updated from a version that asked before keeping text: shown once.
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [weak self] in self?.explainRecording() }
        } else if Self.shouldOfferLogin {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { Self.offerLogin() }
        }

        if !UserDefaults.standard.bool(forKey: "welcomed") {
            UserDefaults.standard.set(true, forKey: "welcomed")
            keepOpen = true
            wanted = true
            refresh()
            reveal(pinned: true)
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
                guard let self, self.line.liveCount == 0 else { return }
                self.keepOpen = false
                self.wanted = false
                self.refresh()
            }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        if Inbox.isEnabled { Inbox.restore() }
        Share.shared.clearAll()
    }

    // MARK: Inbox mode

    private func startWatcher() {
        watcher?.stop()
        watcher = ScreenshotWatcher(
            // A file put back from Recently Cleared is not a new screenshot.
            onNew: { [weak self] url in
                guard let self, !self.line.isKnown(url) else { return }
                self.hangCapture(url, screenshot: true)
            },
            onChange: { [weak self] in self?.line.prune() })
        watcher.start()
        folderWatchers.forEach { $0.stop() }
        folderWatchers = []
        // A folder may also be watched for screenshots, like the Desktop; an
        // image both report hangs once.
        for folder in Self.extraFolders {
            let extra = ScreenshotWatcher(
                folder: folder, anyImage: true,
                onNew: { [weak self] url in self?.hangCapture(url) },
                onChange: { [weak self] in self?.line.prune() })
            extra.start()
            folderWatchers.append(extra)
        }
    }

    /// Up to three folders: enough for another screenshot app or an export
    /// folder, few enough that screenshots still have room on the line.
    static let maxExtraFolders = 3

    /// The folders from "Also watch folders" that still exist. New images in
    /// them hang like screenshots; taking one down leaves the file there.
    static var extraFolders: [URL] {
        get {
            let paths = UserDefaults.standard.stringArray(forKey: "extraFolders") ?? []
            return paths.compactMap { path in
                var isDir: ObjCBool = false
                guard FileManager.default.fileExists(atPath: path, isDirectory: &isDir), isDir.boolValue else { return nil }
                return URL(fileURLWithPath: path, isDirectory: true)
            }
        }
        set { UserDefaults.standard.set(newValue.map(\.path), forKey: "extraFolders") }
    }

    /// Each new screenshot goes to the clipboard as it hangs.
    static var copiesScreenshots: Bool {
        get { UserDefaults.standard.bool(forKey: "copyScreenshots") }
        set { UserDefaults.standard.set(newValue, forKey: "copyScreenshots") }
    }

    private func chooseExtraFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = L("Watch")
        panel.message = L("New images saved to this folder will hang on the line.")
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url, Self.extraFolders.count < Self.maxExtraFolders,
              !Self.extraFolders.contains(where: { $0.standardizedFileURL == url.standardizedFileURL }) else { return }
        Self.extraFolders.append(url)
        startWatcher()
    }

    private func setInbox(_ on: Bool) {
        Inbox.isEnabled = on
        if on { Inbox.apply() } else { Inbox.restore() }
        startWatcher()
    }

    /// Asked once. Changing system settings is the user's call, never ours.
    private func offerInbox() {
        Inbox.wasOffered = true
        let alert = NSAlert()
        alert.messageText = L("Let EnCopy handle your screenshots?")
        alert.informativeText = L(
            "Screenshots will hang on the line the instant you take them, without the floating thumbnail, and will not pile up on your Desktop. Drag one to a folder to keep it, or discard it with the cross. You can turn this off in Settings, and your settings come back when EnCopy quits.")
        alert.addButton(withTitle: L("Turn on"))
        alert.addButton(withTitle: L("Not now"))
        if let icon = NSImage(named: "EnCopy") ?? NSApp.applicationIconImage { alert.icon = icon }
        // A menu bar app that does not open at login is gone after the first
        // restart, so the first launch offers it too, ticked.
        let login = Self.shouldOfferLogin ? NSButton(checkboxWithTitle: L("Open at login"), target: nil, action: nil) : nil
        if let login {
            login.state = .on
            alert.accessoryView = login
            Self.loginOffered()
        }
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertFirstButtonReturn { setInbox(true) }
        if login?.state == .on { Self.openAtLogin() }
    }

    // MARK: Recording what you copy

    private static let recordingExplainedKey = "recordingExplained"
    private static var recordingExplained: Bool {
        get { UserDefaults.standard.bool(forKey: recordingExplainedKey) }
        set { UserDefaults.standard.set(newValue, forKey: recordingExplainedKey) }
    }

    /// Asked once, on the first launch or the first after an update: what
    /// is kept and where. Open at login rides along when it is due, so an
    /// update asks one thing, not two.
    private func explainRecording() {
        Self.recordingExplained = true
        let alert = NSAlert()
        alert.messageText = L("EnCopy keeps the text you copy")
        alert.informativeText = String(format: L("Text, links and code you copy hang on the line. When the line is full, the oldest go into the basket, where a search still finds them. Nothing is kept from a password field, from a copy a password manager marks as private, or from another device. It all stays on this Mac, in %@, where only you can read it. You can pause anytime from the menu bar, and turn on copied images in Settings."),
                                    (DataFolder.url.path as NSString).abbreviatingWithTildeInPath)
        alert.addButton(withTitle: L("OK"))
        alert.addButton(withTitle: L("Pause recording"))
        if let icon = NSImage(named: "EnCopy") ?? NSApp.applicationIconImage { alert.icon = icon }
        let login = Self.shouldOfferLogin ? NSButton(checkboxWithTitle: L("Open at login"), target: nil, action: nil) : nil
        login?.state = .on
        if login != nil { Self.loginOffered() }
        if let login {
            login.frame = NSRect(x: 0, y: 0, width: 240, height: login.fittingSize.height)
            alert.accessoryView = login
        }
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertSecondButtonReturn {
            ClipboardWatcher.isPaused = true
            refreshClipboardWatcher()
        }
        if login?.state == .on { Self.openAtLogin() }
    }

    // MARK: Open at login

    private static let loginOfferedKey = "loginOfferedVersion"
    private static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    }

    /// Each install or update asks once whether EnCopy should open at
    /// login, unless it already does.
    private static var shouldOfferLogin: Bool {
        SMAppService.mainApp.status != .enabled
            && UserDefaults.standard.string(forKey: loginOfferedKey) != version
    }

    private static func loginOffered() {
        UserDefaults.standard.set(version, forKey: loginOfferedKey)
    }

    private static func offerLogin() {
        loginOffered()
        let alert = NSAlert()
        alert.messageText = L("Open EnCopy at login?")
        alert.informativeText = L("EnCopy does not open when you log in, so after a restart the line is gone until you open it again.")
        alert.addButton(withTitle: L("Open at login"))
        alert.addButton(withTitle: L("Not now"))
        if let icon = NSImage(named: "EnCopy") ?? NSApp.applicationIconImage { alert.icon = icon }
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertFirstButtonReturn { openAtLogin() }
    }

    /// Turned off in System Settings, macOS asks for approval there.
    private static func openAtLogin() {
        guard SMAppService.mainApp.status != .enabled else { return }
        toggleLaunchAtLogin()
        if SMAppService.mainApp.status == .requiresApproval { SMAppService.openSystemSettingsLoginItems() }
    }

    /// The clipboard is watched while recording is not paused and some kind is on.
    private func refreshClipboardWatcher() {
        if ClipboardWatcher.isEnabled { clipboardWatcher.start() } else { clipboardWatcher.stop() }
        updateStatusIcon()
    }

    /// Quitting from the menu or logging out runs applicationWillTerminate.
    /// A plain kill does not, so settings are also restored on those signals.
    private func restoreSettingsOnTermination() {
        for sig in [SIGTERM, SIGINT, SIGHUP] {
            signal(sig, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: sig, queue: .main)
            source.setEventHandler {
                if Inbox.isEnabled { Inbox.restore() }
                exit(0)
            }
            source.resume()
            signalSources.append(source)
        }
    }

    // MARK: Showing and hiding

    /// An empty line goes away, unless it was opened on purpose.
    private func itemsChanged() {
        updateStatusIcon()
        Held.forget(except: Set(line.items.filter { !$0.falling && $0.held }.map(\.id)))
        guard line.liveCount == 0, !keepOpen else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.7) { [weak self] in
            guard let self, self.line.liveCount == 0, !self.keepOpen else { return }
            self.wanted = false
            self.refresh()
        }
    }

    /// The line comes down on that screen for a moment, then tucks away.
    /// Without a screen, it uses the one under the pointer.
    private func comeDown(on screen: NSScreen?) {
        panel.placeOnScreen(screen)
        updateCapacity()
        wanted = true
        refresh()
        if Self.peeksAtNew { reveal(peekFor: 2.5) } else if !isRevealed { swayMenuBarPegs() }
    }

    /// ⏎: the pasted paper leaves its peg and flies to the pointer,
    /// shrinking, while the line tucks away. The paste itself does not wait.
    private func flyToPointer(_ id: UUID) {
        guard isPresent, isRevealed, !Self.reducesMotion, CaptureFlight.flightsInProgress < 2,
              let screen = panel.screen, let item = line.items.first(where: { $0.id == id }),
              let card = cardFrame(for: id),
              let image = item.thumb.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return }
        let p = NSEvent.mouseLocation
        let w = card.width * Motion.flyOutScale, h = card.height * Motion.flyOutScale
        CaptureFlight.fly(image: image, from: card, to: CGRect(x: p.x - w / 2, y: p.y - h, width: w, height: h),
                          tilt: CGFloat(item.tilt), on: screen, duration: Motion.flyOut) {}
    }

    /// Copied while the line stays tucked away: the pegs in the menu bar
    /// sway once, and nothing comes down.
    private func swayMenuBarPegs() {
        guard !Self.reducesMotion, let button = statusItem.button else { return }
        let step = Motion.menuBarSwayTotal / Double(Motion.menuBarSway.count)
        for (i, tilt) in Motion.menuBarSway.enumerated() {
            DispatchQueue.main.asyncAfter(deadline: .now() + step * Double(i + 1)) { [weak self] in
                guard let self else { return }
                if tilt == 0 { self.updateStatusIcon(); return }
                let image = MenuBarIcon.image(watching: ClipboardWatcher.isEnabled, tilt: tilt)
                image.accessibilityDescription = button.image?.accessibilityDescription
                button.image = image
            }
        }
    }

    // MARK: The capture flying to the line

    /// A new screenshot lifts off from where it was taken and flies to its
    /// place on the line. Without a known capture area it simply drops in.
    private func hangCapture(_ url: URL, screenshot: Bool = false) {
        // Several watchers may report a file we just saved. Keep ignoring
        // that same file after either a move or a copy.
        guard !line.isDesktopSave(url) else { return }
        // Already hanging: a photo put back with ⌘Z comes out of the Trash
        // into the folder, and the watcher sees it arrive. It must not pull
        // the line over to the screen it was first taken on.
        guard !line.items.contains(where: { $0.url.standardizedFileURL == url.standardizedFileURL && !$0.falling })
        else { return }
        let from = captureRect(of: url)
        let screen = from.flatMap { from in
            NSScreen.screens.first { NSMouseInRect(CGPoint(x: from.midX, y: from.midY), $0.frame, false) }
        }
        // Every capture brings the line down before it hangs, on the screen it
        // was taken on. On a full line the oldest photo then falls in view as
        // the new one flies in.
        comeDown(on: screen)
        // A screenshot macOS took already played its shutter, so it hangs
        // without a chime of its own. Copied images and watched folders,
        // which come with no sound, keep it.
        guard let id = line.hang(url, quietly: from != nil, flying: from != nil) else {
            // Nothing hung after all, so an empty line goes away again.
            itemsChanged()
            return
        }
        // Recordings hang from the same folder, but the option is for screenshots.
        if screenshot, Self.copiesScreenshots, !isRecording(url) { line.copy(id) }
        guard let from else { return }
        // Let the line come down and lay out before measuring the landing spot.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.03) { [weak self] in
            self?.fly(id, from: from)
        }
    }

    /// The same words copied again: the item comes back to the head of the
    /// line, from the basket if that is where it was.
    private func hangAgain(_ url: URL) {
        comeDown(on: nil)
        line.rehang(url)
    }

    private func fly(_ id: UUID, from: CGRect) {
        guard isPresent, isRevealed, !Self.reducesMotion, CaptureFlight.flightsInProgress < 2,
              let screen = panel.screen,
              let to = cardFrame(for: id),
              let item = line.items.first(where: { $0.id == id }) else {
            line.land(id)
            return
        }
        // Sharp enough while it starts out at the captured size, which a
        // full Retina screen would otherwise take at full resolution.
        let pixels = Int(max(from.width, from.height) * screen.backingScaleFactor)
        guard let image = makeThumbnail(item.url, maxPixels: min(1500, max(400, pixels)))?
            .cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            line.land(id)
            return
        }
        CaptureFlight.fly(image: image, from: from, to: to, tilt: CGFloat(item.tilt), on: screen) { [weak self] in
            self?.line.land(id)
        }
    }

    /// A discarded card falls over the whole screen, from where it hangs.
    /// Reduce motion in Accessibility settings: photos land and leave
    /// without flying across the screen or falling down it.
    private static var reducesMotion: Bool { Motion.reduced }

    private func fall(_ item: Pegged) {
        guard isPresent, isRevealed, !item.flying, !Self.reducesMotion, let screen = panel.screen,
              let card = cardFrame(for: item.id),
              let image = item.thumb.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return }
        CaptureFlight.fall(image: image, card: card, tilt: CGFloat(item.tilt), pinned: item.kept, on: screen)
    }

    /// Where a card will hang, in screen coordinates, using the same layout
    /// as the line view.
    private func cardFrame(for id: UUID) -> CGRect? {
        guard let index = line.items.firstIndex(where: { $0.id == id }) else { return nil }
        let width = panel.frame.width
        let x = Layout.x(index: index, count: line.items.count, width: width,
                         visible: line.visibleCount, scroll: line.scroll, pinned: line.pinnedLead)
        let viewTop = Layout.ropeY(x: x, width: width) - Layout.pinAbove
        let cardTop = viewTop + PeggedView.cardOffsetBelowTop
        let size = PeggedView.cardSize(for: line.items[index].thumb.size)
        return CGRect(x: panel.frame.minX + x - size.width / 2,
                      y: panel.frame.maxY - cardTop - size.height,
                      width: size.width, height: size.height)
    }

    /// Decides whether the panel is ordered in at all: something to show,
    /// and no full screen app on that screen unless you asked for the line.
    private func refresh() {
        let blocked = !openedOnPurpose && (panel.screen.map(FullScreen.blocksLine(on:))
            ?? LinePanel.screenUnderPointer().map(FullScreen.blocksLine(on:)) ?? false)
        if wanted && !blocked {
            present()
        } else {
            dismiss()
        }
        // The cursor is watched while there is a line, even tucked away,
        // to notice it pushing against the top edge.
        if wanted { startMouseTracking() } else { stopMouseTracking() }
    }

    private func present() {
        guard !isPresent else { return }
        isPresent = true
        panel.alphaValue = 1
        // The window itself only comes in while the line is down; see setRevealed.
        if isRevealed { panel.orderFrontRegardless() }
    }

    private func dismiss() {
        guard isPresent else { return }
        isPresent = false
        setRevealed(false)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { [weak self] in
            guard let self, !self.isPresent else { return }
            self.panel.orderOut(nil)
        }
    }

    /// The line is one window on every Space. After a long time running,
    /// macOS can leave it bound to a single Space, where ordering it in does
    /// nothing on the others while captures keep arriving (#80). Quitting and
    /// opening the app again fixes it because the window is new, so do the
    /// same here: a fresh window with the same contents, in the same place.
    private func keepOnActiveSpace() {
        guard isPresent, isRevealed, panel.isVisible, !panel.isOnActiveSpace else { return }
        log.notice("The line was not on the active Space; making its window again")
        let old = panel!
        let content = old.contentView
        old.contentView = NSView()
        old.orderOut(nil)
        let fresh = LinePanel(content: content ?? NSView())
        fresh.onScroll = old.onScroll
        fresh.setFrame(old.frame, display: false)
        fresh.ignoresMouseEvents = old.ignoresMouseEvents
        fresh.alphaValue = old.alphaValue
        panel = fresh
        fresh.orderFrontRegardless()
    }

    private func reveal(pinned: Bool = false, peekFor seconds: TimeInterval = 0, bySummon: Bool = false) {
        guard isPresent else { return }
        if pinned { self.pinned = true }
        if seconds > 0 { peekUntil = Date().addingTimeInterval(seconds) }
        awaySince = nil
        setRevealed(true, bySummon: bySummon)
    }

    private func setRevealed(_ on: Bool, bySummon: Bool = false) {
        guard on != isRevealed else { return }
        isRevealed = on
        // Set first, so the panel's spring is chosen in the same update.
        if on { line.landed(bySummon: bySummon) }
        line.revealed = on
        // It always comes down showing the newest.
        if on { line.scroll = 0 }
        // Tucked away, the line leaves no window behind: an invisible strip
        // over the top of the screen would still sit above other apps for
        // anything that checks what is on top, like screen automation.
        if on {
            if isPresent {
                panel.orderFrontRegardless()
                DispatchQueue.main.async { [weak self] in self?.keepOnActiveSpace() }
            }
        } else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
                guard let self, !self.isRevealed else { return }
                self.panel.orderOut(nil)
            }
        }
        if !on {
            // Tucked away by any means, a search under way ends with it.
            if searchBar?.isShown == true { endSearch(tuckAway: false) }
            zoom.hide()
            line.clearSelection()
            line.clearPick()
            pinned = false
            peekUntil = .distantPast
            panel.ignoresMouseEvents = true
            // Opened on purpose lasts until the line tucks away, however that
            // happens: an empty line then goes, as with the shortcut, and over
            // a full screen app the line goes too.
            if keepOpen || openedOnPurpose {
                keepOpen = false
                openedOnPurpose = false
                if line.liveCount == 0 { wanted = false }
                refresh()
            }
        }
    }

    // MARK: Summoning the line

    /// ⌥C, or the menu's first item: the line comes down on the screen under
    /// the pointer, takes the keyboard without activating EnCopy, and the
    /// newest item is picked. ⌥C again, Escape or a click elsewhere tucks
    /// the line away. Already down on that screen, it stays where it is;
    /// down on another one — showing something new, say — it comes over.
    private func summon() {
        if searchBar?.isShown == true {
            endSearch(tuckAway: true)
            return
        }
        if isRevealed && panel.screen == LinePanel.screenUnderPointer() {
            keepOpen = true
            pinned = true
        } else {
            keepOpen = true
            wanted = true
            openedOnPurpose = true
            panel.placeOnScreen()
            updateCapacity()
            refresh()
            reveal(pinned: true, bySummon: true)
        }
        line.pickNewest()
        searchBar.show(below: panel.frame)
    }

    /// The whole line comes back. With Escape it also tucks away; after a
    /// click elsewhere it stays as it is.
    private func endSearch(tuckAway: Bool) {
        line.query = ""
        line.kindFilter = nil
        line.shelf = .line
        line.clearPick()
        searchBar.hide()
        if tuckAway, isRevealed { setRevealed(false) }
    }

    private func startMouseTracking() {
        guard mouseTimer == nil else { return }
        let timer = Timer(timeInterval: 1.0 / 30.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        mouseTimer = timer
    }

    private func stopMouseTracking() {
        mouseTimer?.invalidate()
        mouseTimer = nil
        panel.ignoresMouseEvents = true
    }

    /// How long the cursor rests against the top edge before the line comes
    /// down. Short enough to feel instant, long enough that a quick trip to
    /// the menu bar does not trigger it.
    private static let revealDelay: TimeInterval = 0.25

    /// The menu bar strip at the top of a screen. With an auto-hiding menu
    /// bar the visible frame reaches the top, so the system thickness is used.
    static func menuBarBand(of screen: NSScreen) -> NSRect {
        var h = screen.frame.maxY - screen.visibleFrame.maxY
        if h < 1 { h = max(NSStatusBar.system.thickness, screen.safeAreaInsets.top) }
        return NSRect(x: screen.frame.minX, y: screen.frame.maxY - h, width: screen.frame.width, height: h)
    }

    /// The part of the menu bar that brings the line down: the middle third,
    /// so the menus on the left and the icons on the right stay easy to reach.
    static func revealBand(of screen: NSScreen) -> NSRect {
        let bar = menuBarBand(of: screen)
        let width = bar.width / 3
        return NSRect(x: bar.midX - width / 2, y: bar.minY, width: width, height: bar.height)
    }

    /// A click anywhere in the top bar of any screen, a menu or an icon, puts the line away.
    private func watchMenuBarClicks() {
        let handler: (NSEvent?) -> Void = { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let p = NSEvent.mouseLocation
                guard NSScreen.screens.contains(where: { NSMouseInRect(p, Self.menuBarBand(of: $0), false) }) else { return }
                self.menuBarSuppressed = true
                self.hotZoneSince = nil
                if self.isRevealed {
                    self.pinned = false
                    self.setRevealed(false)
                }
            }
        }
        if let global = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown], handler: handler) {
            clickMonitors.append(global)
        }
        if let local = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown], handler: { e in handler(e); return e }) {
            clickMonitors.append(local)
        }
    }
    /// How long the cursor is away before the line tucks back up.
    private static let retractDelay: TimeInterval = 0.5

    private func tick() {
        let mouse = NSEvent.mouseLocation
        let now = Date()

        let screenUnderPointer = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) }
        let inMenuBar = screenUnderPointer.map { NSMouseInRect(mouse, Self.menuBarBand(of: $0), false) } ?? false
        if !inMenuBar { menuBarSuppressed = false }
        let inRevealBand = screenUnderPointer.map { NSMouseInRect(mouse, Self.revealBand(of: $0), false) } ?? false

        guard isRevealed else {
            line.closeGaps()
            // Resting in the middle of the menu bar brings the line down on
            // that screen. Pushing against the top edge is part of it, and it
            // also works when another display sits above and the pointer
            // never stops.
            // An empty line stays up there: only the shortcut and the menu
            // bring it down on purpose.
            if line.liveCount > 0, Self.opensFromMenuBar, let screen = screenUnderPointer,
               inRevealBand, !menuBarSuppressed, !FullScreen.blocksLine(on: screen) {
                let since = hotZoneSince ?? now
                hotZoneSince = since
                if now.timeIntervalSince(since) >= Self.revealDelay {
                    hotZoneSince = nil
                    if panel.screen != screen {
                        panel.placeOnScreen()
                        updateCapacity()
                    }
                    refresh()
                    reveal()
                }
            } else {
                hotZoneSince = nil
            }
            return
        }

        updateMousePassThrough(mouse)
        updateZoom(mouse)

        // The line's zone runs from its lowest point up to the top of the
        // screen, menu bar included, so moving up never hides it.
        var zone = panel.frame
        if let screen = panel.screen { zone.size.height = screen.frame.maxY - zone.minY }
        let inside = NSMouseInRect(mouse, zone, false)
        // While summoned, the keyboard belongs to the search field: the
        // line stays pinned until the field is dismissed.
        let searching = searchBar?.isShown == true
        if inside && pinned && !searching { pinned = false }
        // Gaps left by discarded photos close once the pointer moves away.
        if !inside { line.closeGaps() }

        // A search keeps the line down until Escape or a click elsewhere.
        let busy = pinned || searching || GrabView.isDragging || GrabView.isShowingMenu || Share.shared.isOpen
            || line.pressedID != nil || now < peekUntil
        if inside || busy {
            awaySince = nil
        } else {
            let since = awaySince ?? now
            awaySince = since
            if now.timeIntervalSince(since) >= Self.retractDelay {
                awaySince = nil
                setRevealed(false)
            }
        }
    }

    /// The card under the pointer opens larger below the line. Not while
    /// dragging, in a card's menu, or while one of our windows is in front,
    /// like Quick Look.
    private func updateZoom(_ mouse: NSPoint) {
        guard Zoom.isEnabled, !GrabView.isDragging, !GrabView.isShowingMenu, NSApp.keyWindow == nil,
              let screen = panel.screen else {
            zoom.hide()
            return
        }
        let local = panel.convertPoint(fromScreen: mouse)
        let flipped = CGPoint(x: local.x, y: panel.frame.height - local.y)
        let hit = line.hitRects.first { $0.value.contains(flipped) }
        let item = hit.flatMap { hit in line.items.first { $0.id == hit.key && !$0.falling && !$0.flying } }
        let card = hit.map {
            CGRect(x: panel.frame.minX + $0.value.minX, y: panel.frame.maxY - $0.value.maxY,
                   width: $0.value.width, height: $0.value.height)
        }
        zoom.update(item, card: card, on: screen)
    }

    /// The panel spans the whole width of the screen, so it only accepts the
    /// mouse while the cursor is over a photo. Everywhere else, clicks go to
    /// whatever is underneath.
    private func updateMousePassThrough(_ mouse: NSPoint) {
        guard !GrabView.isDragging else { return }
        let local = panel.convertPoint(fromScreen: mouse)
        let flipped = CGPoint(x: local.x, y: panel.frame.height - local.y)
        var overPhoto = line.hitRects.values.contains { $0.insetBy(dx: -4, dy: -4).contains(flipped) }
        // A line longer than the screen also takes the band the photos hang
        // in, gaps included, so a swipe anywhere along it moves it.
        if !overPhoto, line.items.count > line.visibleCount, Line.keepOnLine != nil {
            let band = line.hitRects.values.reduce(CGRect.null) { $0.union($1) }
            overPhoto = !band.isNull && flipped.y >= band.minY - 4 && flipped.y <= band.maxY + 4
        }
        if panel.ignoresMouseEvents == overPhoto {
            panel.ignoresMouseEvents = !overPhoto
        }
    }

    // MARK: Holding

    /// Takes a photo off the line to hold in front of you. The line goes
    /// up, so it is out of the way of the photo you are looking at.
    private func hold(_ id: UUID) {
        guard let item = line.items.first(where: { $0.id == id && !$0.falling && !$0.held }) else { return }
        line.setHeld(id, true)
        Held.show(id, url: item.url, line: line) { [weak self] id, frame in self?.hangBack(id, from: frame) }
    }

    /// Back onto its clip: the line comes down and the photo flies home.
    private func hangBack(_ id: UUID, from frame: CGRect) {
        line.setHeld(id, false)
        comeDown(on: NSScreen.screens.first { $0.frame.intersects(frame) })
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.03) { [weak self] in
            self?.fly(id, from: frame)
        }
    }

    // MARK: Hang on EnCopy

    /// The service macOS lists for images and text files in Finder and
    /// elsewhere. What you hang yourself is pinned, and the line comes down
    /// to show it. A text file is copied in as a note.
    @objc func hangOnLine(_ pasteboard: NSPasteboard, userData: String?,
                          error: AutoreleasingUnsafeMutablePointer<NSString?>) {
        let urls = pasteboard.readObjects(forClasses: [NSURL.self],
                                          options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        guard !urls.isEmpty else { return }
        comeDown(on: nil)
        var any = false
        for url in urls {
            let hung = NoteFolder.isTextFile(url) ? NoteFolder.adopt(url) : url
            if let hung, line.hangKept(hung) { any = true }
        }
        if !any { NSSound.beep() }
    }

    // MARK: Putting back

    /// From the menu the line is usually up, so it comes down for a moment
    /// to show the photos going back.
    private func putBack() {
        if !isRevealed {
            panel.placeOnScreen()
            updateCapacity()
            wanted = true
            refresh()
            reveal(peekFor: 2.5)
        }
        line.putBack()
    }

    // MARK: Shortcut

    private func registerShortcut() {
        hotKey = nil
        guard let shortcut = Shortcut.current else { return }
        hotKey = HotKey(shortcut) { [weak self] in self?.summon() }
    }

    /// The shortcut is off while recording, so pressing it records it
    /// instead of summoning the line.
    private func changeShortcut() {
        hotKey = nil
        ShortcutRecorder.shared.record { [weak self] result in
            guard let self else { return }
            if let result {
                let previous = Shortcut.current
                Shortcut.current = result
                registerShortcut()
                // Taken by another app: keep the one that worked.
                if result != nil && hotKey == nil {
                    NSSound.beep()
                    Shortcut.current = previous
                }
            }
            registerShortcut()
        }
    }

    private func setKeep(_ n: Int?) {
        guard n != Line.keepOnLine else { return }
        Line.keepOnLine = n
        updateCapacity()
        line.trim()
    }

    private func setSize(_ size: Layout.Size) {
        guard size != Layout.size else { return }
        Layout.size = size
        panel.placeOnScreen(panel.screen)
        updateCapacity()
        line.reloadThumbnails()
    }

    private func updateCapacity() {
        let usable = panel.frame.width - 200
        let fits = max(3, min(12, Int(usable / Layout.spacing)))
        line.visibleCount = fits
        line.maxItems = max(fits, Line.keepOnLine ?? fits)
    }

    // MARK: Menu bar

    private func setUpStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        updateStatusIcon()
        let menu = NSMenu()
        // Items say themselves whether they can be used, like Take everything
        // down on an empty line.
        menu.autoenablesItems = false
        menu.delegate = self
        statusItem.menu = menu
    }

    /// The pegs fill in while copied things are hung, so it shows at a
    /// glance that the clipboard is being watched. It is dimmed while the
    /// line is empty, so resting in the menu bar and seeing nothing come down
    /// does not look broken.
    private func updateStatusIcon() {
        let on = ClipboardWatcher.isEnabled
        let count = line.liveCount
        let description = on ? L("EnCopy, hanging copied images") : "EnCopy"
        let state = count == 0 ? L("The line is empty") : String(format: L("%d on the line"), count)
        let image = MenuBarIcon.image(watching: on)
        image.accessibilityDescription = "\(description), \(state)"
        statusItem.button?.image = image
        statusItem.button?.appearsDisabled = count == 0
        statusItem.button?.toolTip = "\(description)\n\(state)"
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        let toggleItem = ClosureMenuItem(isRevealed ? L("Hide line") : L("Show line")) { [weak self] in
            self?.summon()
        }
        if let shortcut = Shortcut.current, shortcut.key.count == 1 {
            toggleItem.keyEquivalent = shortcut.key
            toggleItem.keyEquivalentModifierMask = shortcut.modifiers
        }
        menu.addItem(toggleItem)

        let putBackItem = ClosureMenuItem(L("Put back"), key: "z") { [weak self] in
            self?.putBack()
        }
        putBackItem.isEnabled = line.canPutBack
        menu.addItem(putBackItem)

        let clearItem = ClosureMenuItem(L("Take everything down")) { [weak self] in
            self?.line.clear()
        }
        clearItem.isEnabled = line.liveCount > 0
        menu.addItem(clearItem)

        if !Held.heldIDs.isEmpty {
            menu.addItem(ClosureMenuItem(L("Hang everything back")) { Held.hangEverythingBack() })
        }

        // The menu keeps what you reach for every day; choices made once
        // live in Settings (T-039).
        menu.addItem(.separator())
        let pause = ClosureMenuItem(L("Pause recording")) { [weak self] in
            ClipboardWatcher.isPaused.toggle()
            self?.refreshClipboardWatcher()
        }
        pause.state = ClipboardWatcher.isPaused ? .on : .off
        pause.toolTip = L("Nothing you copy is kept while this is on")
        menu.addItem(pause)
        menu.addItem(ClosureMenuItem(L("Clean Up History…")) { [weak self] in
            guard let self else { return }
            CleanupPanel.shared.show(line: self.line)
        })
        menu.addItem(ClosureMenuItem(L("Settings…"), key: ",") { [weak self] in
            self?.showSettings()
        })

        menu.addItem(.separator())
        menu.addItem(ClosureMenuItem(L("Quit EnCopy"), key: "q") {
            NSApp.terminate(nil)
        })
    }

    /// The retention rules run at launch and then once a day (T-031).
    private func retentionIfDue() {
        let last = UserDefaults.standard.double(forKey: "retentionLastRun")
        let now = Date()
        guard now.timeIntervalSince1970 - last >= 86400 else { return }
        UserDefaults.standard.set(now.timeIntervalSince1970, forKey: "retentionLastRun")
        line.runRetention(now: now, inBackground: true)
    }

    /// EnCopy's releases on GitHub.
    static let updatesURL = URL(string: "https://github.com/a1024053774/EnCopy/releases")!

    /// Settings, with the app in front before it opened offered first under By app.
    private func showSettings() {
        let front = NSWorkspace.shared.frontmostApplication?.bundleIdentifier
        SettingsWindow.shared.show(actions: SettingsActions(
            setInbox: { [weak self] in self?.setInbox($0) },
            refreshClipboardWatcher: { [weak self] in self?.refreshClipboardWatcher() },
            chooseExtraFolder: { [weak self] in self?.chooseExtraFolder() },
            removeExtraFolder: { [weak self] folder in
                AppDelegate.extraFolders.removeAll { $0.standardizedFileURL == folder.standardizedFileURL }
                self?.startWatcher()
            },
            openScreenshotsFolder: { [weak self] in
                guard let self else { return }
                NSWorkspace.shared.open(self.watcher.folder)
            },
            setKeep: { [weak self] in self?.setKeep($0) },
            setSize: { [weak self] in self?.setSize($0) },
            hideZoom: { [weak self] in self?.zoom.hide() },
            refresh: { [weak self] in self?.refresh() },
            changeShortcut: { [weak self] in self?.changeShortcut() },
            toggleLaunchAtLogin: { AppDelegate.toggleLaunchAtLogin() },
            soundOn: { [weak self] in self?.line.soundOn ?? false },
            setSoundOn: { [weak self] in self?.line.soundOn = $0 }
        ), frontApp: front)
    }

    private static func toggleLaunchAtLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            let alert = NSAlert()
            alert.messageText = L("Could not change the login setting")
            alert.informativeText = L("Move EnCopy to the Applications folder and try again.")
            NSApp.activate(ignoringOtherApps: true)
            alert.runModal()
        }
    }

    // MARK: Quick Look

    /// With no window of ours in front, Quick Look finds its controller here.
    override func acceptsPreviewPanelControl(_ panel: QLPreviewPanel!) -> Bool { true }

    override func beginPreviewPanelControl(_ panel: QLPreviewPanel!) {
        QuickLook.shared.take(panel)
    }

    override func endPreviewPanelControl(_ panel: QLPreviewPanel!) {
        QuickLook.shared.release(panel)
    }

}

