import AppKit
import ServiceManagement
import SwiftUI

/// What the Settings window asks of the app for the choices that need more
/// than storing a value: re-registering watchers, resizing the line.
struct SettingsActions {
    var setInbox: (Bool) -> Void
    var refreshClipboardWatcher: () -> Void
    var chooseExtraFolder: () -> Void
    var removeExtraFolder: (URL) -> Void
    var openScreenshotsFolder: () -> Void
    var setKeep: (Int?) -> Void
    var setSize: (Layout.Size) -> Void
    var hideZoom: () -> Void
    var refresh: () -> Void
    var changeShortcut: () -> Void
    var toggleLaunchAtLogin: () -> Void
    var soundOn: () -> Bool
    var setSoundOn: (Bool) -> Void
    var theme: () -> Theme
    var setTheme: (Theme) -> Void
    /// What the 「保留」 page shows (T-051).
    var retention: () -> Retention.Status
    var openCleanup: () -> Void
}

/// 设置 (T-039): the choices made once and rarely changed, out of the
/// menu bar menu, which keeps what you reach for every day.
@MainActor
final class SettingsWindow {
    static let shared = SettingsWindow()
    private var window: NSWindow?
    private var model: SettingsModel?

    /// `frontApp`: the app in front before the window opened, offered first
    /// under By app, since it is the one you are most likely deciding about.
    func show(actions: SettingsActions, frontApp: String?) {
        if window == nil {
            let model = SettingsModel(actions: actions)
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 580, height: 600),
                             styleMask: [.titled, .closable], backing: .buffered, defer: false)
            w.title = L("Settings")
            w.isReleasedWhenClosed = false
            w.contentView = NSHostingView(rootView: SettingsView(model: model))
            w.center()
            window = w
            self.model = model
        }
        model?.frontApp = frontApp
        model?.reload()
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

@MainActor
final class SettingsModel: ObservableObject {
    let actions: SettingsActions
    @Published var frontApp: String?
    /// Bumped to redraw after a change made outside SwiftUI's bindings.
    @Published private(set) var version = 0

    init(actions: SettingsActions) { self.actions = actions }

    func reload() { version += 1 }

    /// A binding to a stored setting that runs `then` after it changes.
    func binding(_ get: @escaping () -> Bool, _ set: @escaping (Bool) -> Void,
                 then: (() -> Void)? = nil) -> Binding<Bool> {
        Binding(get: { _ = self.version; return get() },
                set: { set($0); then?(); self.reload() })
    }

    /// The apps listed under By app: the one in front first, then every app
    /// that has a rule, by name.
    var ruleApps: [String] {
        _ = version
        var ids = ClipboardWatcher.appRules.keys.sorted { (History.appName($0) ?? $0) < (History.appName($1) ?? $1) }
        if let front = frontApp, front != Bundle.main.bundleIdentifier {
            ids.removeAll { $0 == front }
            ids.insert(front, at: 0)
        }
        return ids
    }

    func rule(_ id: String) -> Binding<ClipboardWatcher.AppRule> {
        Binding(get: { _ = self.version; return ClipboardWatcher.rule(for: id) },
                set: { rule in
                    var rules = ClipboardWatcher.appRules
                    rules[id] = rule
                    ClipboardWatcher.appRules = rules
                    self.reload()
                })
    }
}

/// 设置 in EnCopy's own look (T-051): tabs are paper tags, each part of a
/// page is a sheet of the line's paper, in the theme the line wears, and the
/// window changes with it. The controls inside stay the system's own, so the
/// keyboard, VoiceOver and Reduce Motion work as everywhere else.
struct SettingsView: View {
    @ObservedObject var model: SettingsModel
    @State private var tab: Int

    init(model: SettingsModel, initialTab: Int = 0) {
        self.model = model
        _tab = State(initialValue: initialTab)
    }

    private var theme: Theme { _ = model.version; return model.actions.theme() }

    private var tabs: [(String, String)] {
        [(L("General"), "gearshape"), (L("Hang what you copy"), "doc.on.clipboard"), (L("Screenshots"), "camera.viewfinder"),
         (L("The line"), "line.diagonal"), (L("Keeping"), "archivebox")]
    }

    var body: some View {
        let theme = self.theme
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                ForEach(Array(tabs.enumerated()), id: \.offset) { i, item in
                    SettingsTag(title: item.0, symbol: item.1, selected: tab == i, theme: theme) { tab = i }
                }
            }
            .padding(.horizontal, 18).padding(.top, 14).padding(.bottom, 10)
            ScrollView {
                VStack(spacing: 14) {
                    switch tab {
                    case 0: general
                    case 1: recording
                    case 2: screenshots
                    case 3: lineTab
                    default: keeping
                    }
                }
                .padding(.horizontal, 18).padding(.bottom, 18).padding(.top, 4)
            }
        }
        .frame(width: 580, height: 600)
        .background(SettingsBackdrop(theme: theme))
        .tint(theme.picked)
        .environment(\.colorScheme, theme == .neon ? .dark : .light)
        .environment(\.settingsTheme, theme)
    }

    private var general: some View {
        SettingsSheet(title: nil) {
            SettingsRow(L("Open at login")) {
                Toggle("", isOn: model.binding({ SMAppService.mainApp.status == .enabled },
                                               { _ in model.actions.toggleLaunchAtLogin() })).labelsHidden()
            }
            SettingsRow(L("Sounds")) {
                Toggle("", isOn: model.binding(model.actions.soundOn, model.actions.setSoundOn)).labelsHidden()
            }
            SettingsRow(L("Shortcut"), detail: { _ = model.version; return Shortcut.current?.display ?? L("None") }()) {
                Button(L("Change Shortcut…")) { model.actions.changeShortcut() }
            }
            SettingsRow(nil, last: true) {
                Button(L("Check for Updates…")) { NSWorkspace.shared.open(AppDelegate.updatesURL) }
            }
        }
    }

    private var recording: some View {
        Group {
            SettingsSheet(title: nil) {
                SettingsRow(L("Images"), detail: L("Images you copy hang on the line too")) {
                    Toggle("", isOn: model.binding({ ClipboardWatcher.imagesEnabled }, { ClipboardWatcher.imagesEnabled = $0 },
                                                   then: model.actions.refreshClipboardWatcher)).labelsHidden()
                }
                SettingsRow(L("Text"), detail: L("Text you copy hangs on the line too")) {
                    Toggle("", isOn: model.binding({ ClipboardWatcher.textEnabled }, { ClipboardWatcher.textEnabled = $0 },
                                                   then: model.actions.refreshClipboardWatcher)).labelsHidden()
                }
                SettingsRow(L("Links"), detail: L("Links you copy hang on the line too")) {
                    Toggle("", isOn: model.binding({ ClipboardWatcher.linksEnabled }, { ClipboardWatcher.linksEnabled = $0 },
                                                   then: model.actions.refreshClipboardWatcher)).labelsHidden()
                }
                SettingsRow(L("Files"), detail: L("Files you copy in Finder hang on the line, where they are"), last: true) {
                    Toggle("", isOn: model.binding({ ClipboardWatcher.filesEnabled }, { ClipboardWatcher.filesEnabled = $0 },
                                                   then: model.actions.refreshClipboardWatcher)).labelsHidden()
                }
            }
            SettingsSheet(title: L("By app")) {
                let apps = model.ruleApps
                ForEach(Array(apps.enumerated()), id: \.element) { i, id in
                    SettingsRow(History.appName(id) ?? id, last: i == apps.count - 1) {
                        Picker("", selection: model.rule(id)) {
                            Text(L("Record normally")).tag(ClipboardWatcher.AppRule.normal)
                            Text(L("Clear after an hour")).tag(ClipboardWatcher.AppRule.hourly)
                            Text(L("Don't record")).tag(ClipboardWatcher.AppRule.skip)
                        }
                        .labelsHidden().fixedSize()
                    }
                }
            }
        }
    }

    private var screenshots: some View {
        Group {
            SettingsSheet(title: nil) {
                SettingsRow(L("Handle screenshots"), detail: L("Screenshots hang instantly and skip the Desktop")) {
                    Toggle("", isOn: model.binding({ Inbox.isEnabled }, model.actions.setInbox)).labelsHidden()
                }
                SettingsRow(L("Copy each screenshot"), detail: L("Each new screenshot goes to the clipboard as it hangs")) {
                    Toggle("", isOn: model.binding({ AppDelegate.copiesScreenshots }, { AppDelegate.copiesScreenshots = $0 })).labelsHidden()
                }
                SettingsRow(nil, last: true) {
                    Button(L("Open screenshots folder")) { model.actions.openScreenshotsFolder() }
                }
            }
            SettingsSheet(title: L("Also watch folders")) {
                let folders = { _ = model.version; return AppDelegate.extraFolders }()
                ForEach(folders, id: \.self) { folder in
                    SettingsRow(FileManager.default.displayName(atPath: folder.path)) {
                        HStack {
                            Button(String(format: L("Show “%@” in Finder"), FileManager.default.displayName(atPath: folder.path))) {
                                NSWorkspace.shared.open(folder)
                            }
                            Button(L("Stop watching")) { model.actions.removeExtraFolder(folder); model.reload() }
                        }
                    }
                }
                SettingsRow(nil, last: true) {
                    Button(L("Add Folder…")) { model.actions.chooseExtraFolder(); model.reload() }
                        .disabled(folders.count >= AppDelegate.maxExtraFolders)
                }
            }
        }
    }

    private var lineTab: some View {
        Group {
            SettingsSheet(title: L("Bring the line down")) {
                SettingsRow(L("When the pointer rests in the menu bar")) {
                    Toggle("", isOn: model.binding({ AppDelegate.opensFromMenuBar }, { AppDelegate.opensFromMenuBar = $0 })).labelsHidden()
                }
                SettingsRow(L("When something new hangs")) {
                    Toggle("", isOn: model.binding({ AppDelegate.peeksAtNew }, { AppDelegate.peeksAtNew = $0 })).labelsHidden()
                }
                SettingsRow(L("Show it for")) {
                    Picker("", selection: Binding(
                        get: { _ = model.version; return Line.peekSeconds },
                        set: { Line.peekSeconds = $0; model.reload() })) {
                        ForEach(Line.peekChoices, id: \.self) { s in
                            Text(String(format: L("%@ seconds"), s.formatted(.number.precision(.fractionLength(0...1))))).tag(s)
                        }
                    }
                    .labelsHidden().fixedSize()
                    .disabled({ _ = model.version; return !AppDelegate.peeksAtNew }())
                }
                SettingsRow(L("Over full screen apps"), last: true) {
                    Toggle("", isOn: model.binding({ FullScreen.showLineOver }, { FullScreen.showLineOver = $0 },
                                                   then: model.actions.refresh)).labelsHidden()
                }
            }
            SettingsSheet(title: nil) {
                SettingsRow(L("Theme")) {
                    Picker("", selection: Binding(
                        get: { _ = model.version; return model.actions.theme() },
                        set: { model.actions.setTheme($0); model.reload() })) {
                        ForEach(Theme.allCases) { Text($0.title).tag($0) }
                    }
                    .labelsHidden().fixedSize()
                }
                SettingsRow(L("Keep on line")) {
                    Picker("", selection: Binding(
                        get: { _ = model.version; return Line.keepOnLine ?? 0 },
                        set: { model.actions.setKeep($0 == 0 ? nil : $0); model.reload() })) {
                        Text(L("As many as fit")).tag(0)
                        ForEach(Line.keepChoices, id: \.self) { n in Text(String(format: L("%d photos"), n)).tag(n) }
                    }
                    .labelsHidden().fixedSize()
                }
                SettingsRow(L("Size")) {
                    Picker("", selection: Binding(
                        get: { _ = model.version; return Layout.size },
                        set: { model.actions.setSize($0); model.reload() })) {
                        Text(L("Small")).tag(Layout.Size.small)
                        Text(L("Medium")).tag(Layout.Size.medium)
                        Text(L("Large")).tag(Layout.Size.large)
                    }
                    .labelsHidden().fixedSize()
                }
                SettingsRow(L("Show the app it came from")) {
                    Toggle("", isOn: model.binding({ Line.showsSourceApp }, { Line.showsSourceApp = $0 })).labelsHidden()
                }
                SettingsRow(L("Zoom on hover"), last: true) {
                    Toggle("", isOn: model.binding({ Zoom.isEnabled }, { Zoom.isEnabled = $0 },
                                                   then: { if !Zoom.isEnabled { model.actions.hideZoom() } })).labelsHidden()
                }
            }
        }
    }

    /// 保留 (T-051): T-004's rules as they run, with the cap to choose.
    private var keeping: some View {
        let status = { _ = model.version; return model.actions.retention() }()
        // Binary units, as the caps are counted: 1 << 30 reads "1 GB", as in the picker.
        let size = { (b: Int64) in ByteCountFormatter.string(fromByteCount: b, countStyle: .memory) }
        return Group {
            SettingsSheet(title: L("What EnCopy keeps")) {
                SettingsRow(L("Text"), detail: L("Kept a year after its last use")) { EmptyView() }
                SettingsRow(L("Screenshots and images"),
                            detail: L("Originals shrink to thumbnails after 30 days; the words in them can still be found")) { EmptyView() }
                SettingsRow(L("Files from Finder"), detail: L("Their paths are kept a year; the files themselves are never moved")) { EmptyView() }
                SettingsRow(L("On the line and pinned"), detail: L("Never expire"), last: true) { EmptyView() }
            }
            SettingsSheet(title: L("Room for originals")) {
                SettingsRow(L("Originals may use up to"),
                            detail: L("Past it, the oldest originals shrink first")) {
                    Picker("", selection: Binding(
                        get: { _ = model.version; return Line.originalsCap },
                        set: { Line.originalsCap = $0; model.reload() })) {
                        ForEach(Self.caps, id: \.0) { Text($0.1).tag($0.0) }
                    }
                    .labelsHidden().fixedSize()
                }
                SettingsRow(String(format: L("Originals use %@ of %@"), size(status.originalsBytes), size(status.cap))) {
                    ProgressView(value: Double(min(status.originalsBytes, status.cap)), total: Double(max(status.cap, 1)))
                        .frame(width: 140)
                }
                SettingsRow(String(format: L("Next run: %d expire, %d originals shrink."), status.willExpire, status.willShrink),
                            detail: L("What expires waits 7 days in Recently Cleared, and can be put back"), last: true) {
                    Button(L("Clean Up History…")) { model.actions.openCleanup() }
                }
            }
        }
    }

    static let caps: [(Int64, String)] = [(512 << 20, "512 MB"), (1 << 30, "1 GB"), (2 << 30, "2 GB"), (5 << 30, "5 GB")]
}

// MARK: The paper look

private struct SettingsThemeKey: EnvironmentKey { static let defaultValue: Theme = .paper }
extension EnvironmentValues {
    var settingsTheme: Theme {
        get { self[SettingsThemeKey.self] }
        set { self[SettingsThemeKey.self] = newValue }
    }
}

/// What the window's paper lies on, per theme.
private struct SettingsBackdrop: View {
    let theme: Theme
    var body: some View {
        switch theme {
        case .paper: Color(red: 0.90, green: 0.85, blue: 0.76)
        case .pixel: Color(red: 0.36, green: 0.58, blue: 0.93)
        case .skeuo: LinearGradient(colors: [Color(red: 0.60, green: 0.45, blue: 0.30), Color(red: 0.48, green: 0.35, blue: 0.22)],
                                    startPoint: .top, endPoint: .bottom)
        case .minimal: Color(white: 0.95)
        case .neon: Color(red: 0.04, green: 0.04, blue: 0.09)
        case .washi: Color(red: 0.96, green: 0.93, blue: 0.86)
        }
    }
}

/// One page tab, a small paper tag; the chosen one is the theme's pick colour.
private struct SettingsTag: View {
    let title: String
    let symbol: String
    let selected: Bool
    let theme: Theme
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                Image(systemName: symbol).font(.system(size: 11, weight: .semibold))
                Text(title).font(theme.font(11.5, bold: true)).lineLimit(1)
            }
            .padding(.horizontal, 9).padding(.vertical, 6)
            .foregroundStyle(selected ? Color.white : theme.ink(.text))
            .background {
                if selected {
                    RoundedRectangle(cornerRadius: max(4, theme.corner), style: .continuous).fill(theme.picked)
                } else {
                    ThemeCardBackground(theme: theme, card: .text)
                }
            }
            // On the paper, not the label: a shadow on the whole tab falls
            // behind every letter too, which the pixel theme's hard one smears.
            .background { Color.clear.shadow(color: .black.opacity(selected ? 0.25 : 0.12), radius: 2, y: 1) }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .focusEffectDisabled()
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// One part of a page: a sheet of the theme's paper with an optional title.
private struct SettingsSheet<Content: View>: View {
    let title: String?
    @ViewBuilder let content: Content
    @Environment(\.settingsTheme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if let title {
                Text(title).font(theme.font(13, bold: true)).foregroundStyle(theme.ink(.text))
                    .padding(.horizontal, 16).padding(.top, 14).padding(.bottom, 4)
            }
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        // The shadow goes on the paper alone: put on the sheet, it falls
        // behind every line of text as well, and the pixel theme's hard,
        // offset one doubles each letter.
        .background(ThemeCardBackground(theme: theme, card: .text)
            .shadow(color: theme.shadow(hovering: false).0.opacity(0.7), radius: theme.shadow(hovering: false).1 / 2,
                    x: theme.shadow(hovering: false).2, y: theme.shadow(hovering: false).3 / 2))
    }
}

/// A setting: its name and one line on what it does, the control at the right.
private struct SettingsRow<Control: View>: View {
    let title: String?
    let detail: String?
    let last: Bool
    @ViewBuilder let control: Control
    @Environment(\.settingsTheme) private var theme

    init(_ title: String?, detail: String? = nil, last: Bool = false, @ViewBuilder control: () -> Control) {
        self.title = title
        self.detail = detail
        self.last = last
        self.control = control()
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .center, spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    if let title { Text(title).font(theme.font(12.5, bold: true)).foregroundStyle(theme.ink(.text)) }
                    if let detail { Text(detail).font(theme.font(11)).foregroundStyle(theme.soft(.text)).fixedSize(horizontal: false, vertical: true) }
                }
                Spacer(minLength: 8)
                control
            }
            .padding(.horizontal, 16).padding(.vertical, 10)
            if !last {
                Rectangle().fill(theme.soft(.text).opacity(0.25)).frame(height: 0.5).padding(.horizontal, 16)
            }
        }
    }
}
