import AppKit
import Security

/// In-app updates (T-053). EnCopy asks GitHub for its latest release at launch
/// and then once a day, and only says so when there is a newer one; with
/// 自动更新 on it installs it by itself. Installing downloads the release's
/// EnCopy.dmg, takes the app out of it, checks that it is this app, newer,
/// and signed by the same certificate as the copy that is running, puts it
/// where the running copy is, and opens it again. Any check that fails leaves
/// the running copy as it was.
@MainActor
final class Updater {
    static let shared = Updater()

    struct Release: Equatable {
        let version: String
        let notes: String
        let dmg: URL
    }

    enum State: Equatable {
        case idle, checking, upToDate, available(Release), installing, failed(String)

        var release: Release? {
            if case .available(let release) = self { return release }
            return nil
        }
    }

    enum Failure: Error {
        case feed, noApp, wrongApp, notNewer, signature, replace

        var message: String {
            switch self {
            case .feed: return L("Couldn't reach GitHub")
            case .noApp: return L("The download has no EnCopy in it")
            case .wrongApp: return L("The download is not this EnCopy")
            case .notNewer: return L("The download is not newer than this copy")
            case .signature: return L("The download's signature doesn't match this copy")
            case .replace: return L("EnCopy couldn't replace itself where it is")
            }
        }
    }

    private(set) var state: State = .idle {
        didSet {
            onChange?()
            if (oldValue.release == nil) != (state.release == nil) { onAvailable?() }
        }
    }
    /// Settings redraws with this.
    var onChange: (() -> Void)?
    /// The menu bar icon, when a newer version appears or goes.
    var onAvailable: (() -> Void)?
    /// Whether restarting now would not get in your way; asked before an
    /// automatic update, which otherwise waits for the next hourly look.
    var canRestartNow: () -> Bool = { true }

    nonisolated static let feed = URL(string: "https://api.github.com/repos/a1024053774/EnCopy/releases/latest")!
    nonisolated static let releasesPage = URL(string: "https://github.com/a1024053774/EnCopy/releases/latest")!
    nonisolated static let assetName = "EnCopy.dmg"
    /// Releases are of the everyday copy; the development copy does not look.
    nonisolated static let dailyBundleID = "app.encopy.EnCopy"
    private static let autoKey = "autoUpdate"
    private static let lastCheckKey = "updateLastCheck"

    /// 自动更新, off unless you turn it on: then a newer version is
    /// installed and EnCopy restarts without asking.
    static var autoUpdate: Bool {
        get { UserDefaults.standard.bool(forKey: autoKey) }
        set { UserDefaults.standard.set(newValue, forKey: autoKey) }
    }

    static var isOffered: Bool { Bundle.main.bundleIdentifier == dailyBundleID }

    static var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    var available: Release? { state.release }

    private var timer: Timer?

    /// A look shortly after launch, then every hour whether a day has passed.
    func start() {
        guard Self.isOffered, timer == nil else { return }
        let timer = Timer(timeInterval: 3600, repeats: true) { _ in
            MainActor.assumeIsolated { Updater.shared.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        DispatchQueue.main.asyncAfter(deadline: .now() + 10) { self.check(manual: false) }
    }

    private func tick() {
        if let release = available, Self.autoUpdate, canRestartNow() {
            install(release, manual: false)
            return
        }
        let last = UserDefaults.standard.double(forKey: Self.lastCheckKey)
        if Date().timeIntervalSince1970 - last >= 86400 { check(manual: false) }
    }

    /// `manual`: asked for in Settings, so a failure is said; a look of its
    /// own fails quietly and tries again tomorrow.
    func check(manual: Bool) {
        guard Self.isOffered, state != .checking, state != .installing else { return }
        let before = state
        state = .checking
        UserDefaults.standard.set(Date().timeIntervalSince1970, forKey: Self.lastCheckKey)
        Task {
            do {
                var request = URLRequest(url: Self.feed, timeoutInterval: 20)
                request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
                let (data, response) = try await URLSession(configuration: .ephemeral).data(for: request)
                guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw Failure.feed }
                if let release = try Self.release(from: data), Self.isNewer(release.version, than: Self.currentVersion) {
                    state = .available(release)
                    if Self.autoUpdate, canRestartNow() { install(release, manual: false) }
                } else {
                    state = .upToDate
                }
            } catch {
                state = manual ? .failed(Failure.feed.message) : (before == .checking ? .idle : before)
            }
        }
    }

    /// The menu's and Settings' 更新并重启…: says which version and what is
    /// new, and installs on yes.
    func confirmAndInstall() {
        guard let release = available else { return }
        let alert = NSAlert()
        alert.messageText = String(format: L("Update EnCopy to %@?"), release.version)
        let notes = release.notes.trimmingCharacters(in: .whitespacesAndNewlines)
        let shortNotes = notes.count > 800 ? String(notes.prefix(800)) + "…" : notes
        alert.informativeText = [shortNotes, L("EnCopy will quit and open again.")].filter { !$0.isEmpty }.joined(separator: "\n\n")
        alert.addButton(withTitle: L("Update and Restart"))
        alert.addButton(withTitle: L("Later"))
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        install(release, manual: true)
    }

    private func install(_ release: Release, manual: Bool) {
        guard state != .installing else { return }
        state = .installing
        let target = Bundle.main.bundleURL
        let bundleID = Bundle.main.bundleIdentifier ?? ""
        let current = Self.currentVersion
        let requirement = Self.ownRequirement()
        Task {
            do {
                let (data, response) = try await URLSession(configuration: .ephemeral).download(from: release.dmg)
                guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw Failure.feed }
                try await Task.detached {
                    defer { try? FileManager.default.removeItem(at: data) }
                    let staged = try Self.unpack(dmg: data, beside: target)
                    defer { try? FileManager.default.removeItem(at: staged.deletingLastPathComponent()) }
                    try Self.verify(staged, bundleID: bundleID, newerThan: current, requirement: requirement)
                    try Self.replace(target, with: staged)
                }.value
                Self.relaunch(target)
            } catch {
                let message = (error as? Failure)?.message ?? error.localizedDescription
                state = .failed(message)
                if manual { Self.showFailure(message) }
            }
        }
    }

    private static func showFailure(_ message: String) {
        let alert = NSAlert()
        alert.messageText = L("EnCopy wasn't updated")
        alert.informativeText = message + "\n\n" + L("This copy is unchanged. You can download the new version from the releases page.")
        alert.addButton(withTitle: L("Open Releases Page"))
        alert.addButton(withTitle: L("OK"))
        if alert.runModal() == .alertFirstButtonReturn { NSWorkspace.shared.open(releasesPage) }
    }

    // MARK: The parts, without the app around them

    /// The release in GitHub's answer, or nil when it has no EnCopy.dmg.
    nonisolated static func release(from data: Data) throws -> Release? {
        struct Asset: Decodable { let name: String; let browser_download_url: URL }
        struct Feed: Decodable { let tag_name: String; let body: String?; let draft: Bool?; let prerelease: Bool?; let assets: [Asset] }
        let feed = try JSONDecoder().decode(Feed.self, from: data)
        guard feed.draft != true, feed.prerelease != true,
              let asset = feed.assets.first(where: { $0.name == assetName }) else { return nil }
        return Release(version: versionParts(feed.tag_name).map(String.init).joined(separator: "."),
                       notes: feed.body ?? "", dmg: asset.browser_download_url)
    }

    /// Compared number by number: 0.10.0 is newer than 0.9.0, 0.4 is 0.4.0,
    /// and a leading v is ignored.
    nonisolated static func isNewer(_ version: String, than other: String) -> Bool {
        let a = versionParts(version), b = versionParts(other)
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    nonisolated private static func versionParts(_ version: String) -> [Int] {
        let trimmed = version.hasPrefix("v") ? String(version.dropFirst()) : version
        return trimmed.split(separator: ".").map { Int($0.prefix { $0.isNumber }) ?? 0 }
    }

    /// The running copy's designated requirement: on this Mac's builds, the
    /// bundle id and the signing certificate (T-016).
    nonisolated static func ownRequirement() -> SecRequirement? {
        var code: SecCode?
        var staticCode: SecStaticCode?
        var requirement: SecRequirement?
        guard SecCodeCopySelf([], &code) == errSecSuccess, let code,
              SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode,
              SecCodeCopyDesignatedRequirement(staticCode, [], &requirement) == errSecSuccess else { return nil }
        return requirement
    }

    /// Mounts the image read-only and copies its EnCopy.app into a folder of
    /// its own on the same volume as `target`, so the swap is a rename. The
    /// caller removes that folder.
    nonisolated static func unpack(dmg: URL, beside target: URL) throws -> URL {
        let fm = FileManager.default
        let work = try fm.url(for: .itemReplacementDirectory, in: .userDomainMask, appropriateFor: target, create: true)
        let mount = work.appendingPathComponent("mount")
        try fm.createDirectory(at: mount, withIntermediateDirectories: true)
        guard run("/usr/bin/hdiutil", ["attach", dmg.path, "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount.path]) else {
            try? fm.removeItem(at: work)
            throw Failure.noApp
        }
        let inside = mount.appendingPathComponent(target.lastPathComponent)
        let source = fm.fileExists(atPath: inside.path) ? inside : mount.appendingPathComponent("EnCopy.app")
        let staged = work.appendingPathComponent(target.lastPathComponent)
        let copied = fm.fileExists(atPath: source.path) && run("/usr/bin/ditto", [source.path, staged.path])
        _ = run("/usr/bin/hdiutil", ["detach", mount.path, "-force"])
        try? fm.removeItem(at: mount)
        guard copied else {
            try? fm.removeItem(at: work)
            throw Failure.noApp
        }
        return staged
    }

    /// Refuses an app that is not this one, not newer, or not signed to
    /// `requirement`. No requirement (an unsigned running copy) refuses all.
    nonisolated static func verify(_ app: URL, bundleID: String, newerThan current: String, requirement: SecRequirement?) throws {
        let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist"))
        guard let id = info?["CFBundleIdentifier"] as? String, id == bundleID else { throw Failure.wrongApp }
        guard let version = info?["CFBundleShortVersionString"] as? String, isNewer(version, than: current) else { throw Failure.notNewer }
        var code: SecStaticCode?
        guard let requirement, SecStaticCodeCreateWithPath(app as CFURL, [], &code) == errSecSuccess, let code,
              SecStaticCodeCheckValidity(code, SecCSFlags(rawValue: kSecCSCheckAllArchitectures), requirement) == errSecSuccess else {
            throw Failure.signature
        }
    }

    /// Swaps `staged` into `target` in one step; on failure `target` stays.
    nonisolated static func replace(_ target: URL, with staged: URL) throws {
        do {
            _ = try FileManager.default.replaceItemAt(target, withItemAt: staged)
        } catch {
            throw Failure.replace
        }
    }

    /// Opens `app` once this process has gone, then quits the usual way, so
    /// the screenshot settings are put back first.
    private static func relaunch(_ app: URL) {
        let pid = ProcessInfo.processInfo.processIdentifier
        let waiter = Process()
        waiter.executableURL = URL(fileURLWithPath: "/bin/sh")
        waiter.arguments = ["-c", "while kill -0 \(pid) 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$0\"", app.path]
        try? waiter.run()
        NSApp.terminate(nil)
    }

    nonisolated private static func run(_ tool: String, _ arguments: [String]) -> Bool {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        guard (try? process.run()) != nil else { return false }
        process.waitUntilExit()
        return process.terminationStatus == 0
    }
}
