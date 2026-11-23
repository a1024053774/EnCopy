import AppKit
import Carbon

/// Hangs what you copy: images as PNGs in EnCopy's own folder, text and
/// links as notes, so on the line they behave like any screenshot.
///
/// Text and links are recorded out of the box; images are opt in, and one
/// switch pauses everything. The pasteboard has no change notification,
/// so its change count is checked twice a second; what was copied is only
/// read once its types say what it is and nothing private comes with it.
@MainActor
final class ClipboardWatcher {
    private let pasteboard: NSPasteboard
    private let history: History
    private let isSecureInput: () -> Bool
    /// The app in front when a copy is noticed: the one it came from.
    private let frontApp: () -> String?
    private let onNew: (URL) -> Void
    /// The same words were already kept: the item comes back instead.
    private let onAgain: (URL) -> Void
    private var timer: Timer?
    private var lastCount: Int
    /// The last image saved. Some apps put the same image on the clipboard
    /// again; it hangs once, unless it has been taken down since.
    private var last: (png: Data, url: URL)?

    private static let enabledKey = "clipboardEnabled"

    private static let textKey = "clipboardTextEnabled"
    private static let linksKey = "clipboardLinksEnabled"
    private static let pausedKey = "recordingPaused"

    /// Hang copied images.
    static var imagesEnabled: Bool {
        get { UserDefaults.standard.bool(forKey: enabledKey) }
        set { UserDefaults.standard.set(newValue, forKey: enabledKey) }
    }

    /// Hang copied text. On unless turned off: an explicit choice from
    /// before is respected.
    static var textEnabled: Bool {
        get { UserDefaults.standard.object(forKey: textKey) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: textKey) }
    }

    /// Hang copied links: a copy that is one web address and nothing else.
    /// On unless turned off, apart from text.
    static var linksEnabled: Bool {
        get { UserDefaults.standard.object(forKey: linksKey) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: linksKey) }
    }

    /// Nothing is recorded while paused.
    static var isPaused: Bool {
        get { UserDefaults.standard.bool(forKey: pausedKey) }
        set { UserDefaults.standard.set(newValue, forKey: pausedKey) }
    }

    /// What to do with copies from one app (T-027): keep them, keep them for
    /// an hour after their last use, or never record them.
    enum AppRule: String { case normal, hourly, skip }

    /// Passwords and Keychain Access are never recorded unless you say so.
    static let presetSkips = ["com.apple.Passwords", "com.apple.keychainaccess"]
    private static let rulesKey = "appRules"

    /// Every app with a rule of its own, by bundle id.
    static var appRules: [String: AppRule] {
        get {
            guard let raw = UserDefaults.standard.dictionary(forKey: rulesKey) as? [String: String] else {
                return Dictionary(uniqueKeysWithValues: presetSkips.map { ($0, AppRule.skip) })
            }
            return raw.compactMapValues(AppRule.init(rawValue:))
        }
        set { UserDefaults.standard.set(newValue.mapValues(\.rawValue), forKey: rulesKey) }
    }

    static func rule(for app: String?) -> AppRule { app.flatMap { appRules[$0] } ?? .normal }

    /// How long copies under the hourly rule stay after their last use.
    static let hourly: TimeInterval = 3600
    /// How long a verification code stays (T-028).
    static let codeLife: TimeInterval = 600

    /// A verification code: 4 to 8 digits on their own from Messages or Mail,
    /// or a short text that says 验证码 or "code" next to 4 to 8 digits.
    static func isCode(_ text: String, app: String?) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let digitsOnly = trimmed.range(of: #"^[0-9]{4,8}$"#, options: .regularExpression) != nil
        if digitsOnly, let app, ["com.apple.MobileSMS", "com.apple.mail"].contains(app) { return true }
        guard trimmed.count <= 160,
              trimmed.range(of: #"(?<![0-9])[0-9]{4,8}(?![0-9])"#, options: .regularExpression) != nil else { return false }
        return trimmed.range(of: #"验证码|校验码|动态码|\bcode\b"#, options: [.regularExpression, .caseInsensitive]) != nil
    }

    /// The clipboard is watched while anything is on and recording is not paused.
    static var isEnabled: Bool { !isPaused && (imagesEnabled || textEnabled || linksEnabled) }

    /// Copies that carry a picture but are not one. Text from a spreadsheet
    /// or a document also comes as an image of itself. A file copied in
    /// Finder brings its icon, and a photo copied from the line brings its
    /// file. Password managers and similar apps mark private or temporary
    /// copies, following nspasteboard.org.
    private static let ignoredTypes: Set<NSPasteboard.PasteboardType> = [
        // What EnCopy itself copies is never hung again.
        .string, .fileURL, selfCopyType,
        .init("org.nspasteboard.ConcealedType"),
        .init("org.nspasteboard.TransientType"),
        .init("org.nspasteboard.AutoGeneratedType"),
    ]

    /// Anything bigger is not a screenshot.
    private static let maxBytes = 64 * 1024 * 1024
    nonisolated private static let maxPixels = 80_000_000

    init(pasteboard: NSPasteboard = .general,
         history: History,
         isSecureInput: @escaping () -> Bool = { IsSecureEventInputEnabled() },
         frontApp: @escaping () -> String? = { NSWorkspace.shared.frontmostApplication?.bundleIdentifier },
         onNew: @escaping (URL) -> Void,
         onAgain: @escaping (URL) -> Void) {
        self.pasteboard = pasteboard
        self.history = history
        self.isSecureInput = isSecureInput
        self.frontApp = frontApp
        self.onNew = onNew
        self.onAgain = onAgain
        lastCount = pasteboard.changeCount
    }

    func start() {
        guard timer == nil else { return }
        // Whatever is on the clipboard already is old news.
        lastCount = pasteboard.changeCount
        let timer = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.check() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    func check() {
        // Paused: nothing is recorded, and what was copied meanwhile is
        // old news once recording resumes.
        guard !Self.isPaused else { lastCount = pasteboard.changeCount; return }
        let pb = pasteboard
        let count = pb.changeCount
        guard count != lastCount else { return }
        lastCount = count
        let app = frontApp()
        let rule = Self.rule(for: app)
        // An app you never want recorded leaves no trace, text or image.
        guard rule != .skip else { return }
        let ttl = rule == .hourly ? Self.hourly : nil
        if Self.textEnabled || Self.linksEnabled, let (text, rtf) = copiedText(pb) {
            // A verification code is only good for minutes: it stays ten.
            let ttl = Self.isCode(text, app: app) ? Self.codeLife : ttl
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            if Note.webAddress(trimmed) != nil {
                if Self.linksEnabled { save(text: trimmed, rtf: nil, link: true, app: app, ttl: ttl) }
            } else if Self.textEnabled {
                save(text: text, rtf: rtf, link: false, app: app, ttl: ttl)
            }
            return
        }
        guard Self.imagesEnabled, let items = pb.pasteboardItems, !items.isEmpty,
              items.allSatisfy({ Self.ignoredTypes.isDisjoint(with: $0.types) }),
              let type = pb.availableType(from: [.png, .tiff]),
              let data = pb.data(forType: type), data.count <= Self.maxBytes,
              pb.changeCount == count else { return }
        // Decoding a large image takes a moment, so it happens off the main thread.
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let png = ClipboardWatcher.png(from: data, type: type)
            DispatchQueue.main.async {
                // Copied over again in the meantime: that copy is the one that
                // counts. Turned off in the meantime: nothing hangs.
                guard let png, self?.pasteboard.changeCount == count,
                      let self, self.timer != nil else { return }
                self.save(png)
            }
        }
    }

    /// Text copied on its own: not a file, not from the line, not private,
    /// not from another device, not typed into a password field. A copy that
    /// also carries a picture, such as cells from a spreadsheet, is never
    /// text: it is up to the images switch.
    private static let maxChars = 100_000
    private func copiedText(_ pb: NSPasteboard) -> (String, Data?)? {
        guard let items = pb.pasteboardItems, !items.isEmpty,
              !isSecureInput(),
              pb.availableType(from: [.png, .tiff]) == nil else { return nil }
        let blocked: Set<NSPasteboard.PasteboardType> = [
            .fileURL, selfCopyType,
            .init("org.nspasteboard.ConcealedType"),
            .init("org.nspasteboard.TransientType"),
            .init("org.nspasteboard.AutoGeneratedType"),
            // Universal Clipboard: copied on an iPhone or another Mac.
            .init("com.apple.is-remote-clipboard"),
        ]
        guard items.allSatisfy({ blocked.isDisjoint(with: $0.types) }),
              let text = pb.string(forType: .string),
              !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              text.count <= Self.maxChars else { return nil }
        return (text, Self.richData(pb))
    }

    /// The styled version of what was copied, as RTF, when there is one.
    /// Web pages bring HTML, which is converted.
    private static func richData(_ pb: NSPasteboard) -> Data? {
        if let rtf = pb.data(forType: .rtf), rtf.count <= 5_000_000 { return lightPageRTF(rtf) }
        guard let html = pb.data(forType: .html), html.count <= 2_000_000,
              let attr = try? NSAttributedString(
                data: html,
                options: [.documentType: NSAttributedString.DocumentType.html,
                          .characterEncoding: String.Encoding.utf8.rawValue],
                documentAttributes: nil) else { return nil }
        let clean = lightPageStyled(attr)
        return try? clean.data(from: NSRange(location: 0, length: clean.length),
                               documentAttributes: [.documentType: NSAttributedString.DocumentType.rtf])
    }

    /// Notes and links go to their own folder, apart from the screenshots.
    /// The same words again add nothing: the kept note comes back instead.
    private func save(text: String, rtf: Data?, link: Bool, app: String?, ttl: TimeInterval?) {
        if let kept = history.note(holding: text) { onAgain(kept); return }
        let ext = rtf == nil ? "txt" : "rtf"
        let name = link ? "Link" : "Text"
        let fm = FileManager.default
        let folder = NoteFolder.url
        var url = folder.appendingPathComponent("\(name) \(Self.stamp()).\(ext)")
        var n = 2
        while fm.fileExists(atPath: url.path) {
            url = folder.appendingPathComponent("\(name) \(Self.stamp()) \(n).\(ext)")
            n += 1
        }
        do {
            try fm.createDirectory(at: folder, withIntermediateDirectories: true)
            try (rtf ?? Data(text.utf8)).write(to: url, options: .atomic)
            try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            history.record(url, text: text, app: app, ttl: ttl)
            onNew(url)
        } catch {
            try? fm.removeItem(at: url)
            log.error("Could not save copied text: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// PNG bytes are kept as they are; TIFF becomes PNG. Either way the image
    /// must decode and have a sane size.
    nonisolated private static func png(from data: Data, type: NSPasteboard.PasteboardType) -> Data? {
        guard let rep = NSBitmapImageRep(data: data),
              rep.pixelsWide > 0, rep.pixelsHigh > 0,
              rep.pixelsWide <= maxPixels / rep.pixelsHigh else { return nil }
        return type == .png ? data : rep.representation(using: .png, properties: [:])
    }

    private func save(_ png: Data) {
        if let last, last.png == png, FileManager.default.fileExists(atPath: last.url.path) { return }
        let fm = FileManager.default
        var url = Inbox.folder.appendingPathComponent("Clipboard \(Self.stamp()).png")
        var n = 2
        while fm.fileExists(atPath: url.path) {
            url = Inbox.folder.appendingPathComponent("Clipboard \(Self.stamp()) \(n).png")
            n += 1
        }
        do {
            try fm.createDirectory(at: Inbox.folder, withIntermediateDirectories: true)
            try png.write(to: url, options: .atomic)
            // What people copy can be private: only they can read it.
            try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            last = (png, url)
            onNew(url)
        } catch {
            try? fm.removeItem(at: url)
            log.error("Could not save a copied image: \(error.localizedDescription, privacy: .public)")
        }
    }

    private static func stamp() -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        return f.string(from: Date())
    }
}
