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
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 460),
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

struct SettingsView: View {
    @ObservedObject var model: SettingsModel

    /// A setting's name with one line on what it does.
    private func described(_ title: String, _ detail: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
            Text(detail).font(.caption).foregroundStyle(.secondary)
        }
    }

    var body: some View {
        TabView {
            general.tabItem { Label(L("General"), systemImage: "gearshape") }
            recording.tabItem { Label(L("Hang what you copy"), systemImage: "doc.on.clipboard") }
            screenshots.tabItem { Label(L("Screenshots"), systemImage: "camera.viewfinder") }
            lineTab.tabItem { Label(L("The line"), systemImage: "line.diagonal") }
        }
        .padding(16)
        .frame(width: 520, height: 460)
    }

    private var general: some View {
        Form {
            Toggle(L("Open at login"), isOn: model.binding({ SMAppService.mainApp.status == .enabled },
                                                           { _ in model.actions.toggleLaunchAtLogin() }))
            Toggle(L("Sounds"), isOn: model.binding(model.actions.soundOn, model.actions.setSoundOn))
            LabeledContent(L("Shortcut")) {
                HStack {
                    Text({ _ = model.version; return Shortcut.current?.display ?? L("None") }())
                        .foregroundStyle(.secondary)
                    Button(L("Change Shortcut…")) { model.actions.changeShortcut() }
                }
            }
            LabeledContent("") {
                Button(L("Check for Updates…")) { NSWorkspace.shared.open(AppDelegate.updatesURL) }
            }
        }
        .formStyle(.grouped)
    }

    private var recording: some View {
        Form {
            Section {
                Toggle(isOn: model.binding({ ClipboardWatcher.imagesEnabled }, { ClipboardWatcher.imagesEnabled = $0 },
                                                        then: model.actions.refreshClipboardWatcher)) { described(L("Images"), L("Images you copy hang on the line too")) }
                Toggle(isOn: model.binding({ ClipboardWatcher.textEnabled }, { ClipboardWatcher.textEnabled = $0 },
                                                      then: model.actions.refreshClipboardWatcher)) { described(L("Text"), L("Text you copy hangs on the line too")) }
                Toggle(isOn: model.binding({ ClipboardWatcher.linksEnabled }, { ClipboardWatcher.linksEnabled = $0 },
                                                       then: model.actions.refreshClipboardWatcher)) { described(L("Links"), L("Links you copy hang on the line too")) }
                Toggle(isOn: model.binding({ ClipboardWatcher.filesEnabled }, { ClipboardWatcher.filesEnabled = $0 },
                                                       then: model.actions.refreshClipboardWatcher)) { described(L("Files"), L("Files you copy in Finder hang on the line, where they are")) }
            }
            Section(L("By app")) {
                ForEach(model.ruleApps, id: \.self) { id in
                    Picker(History.appName(id) ?? id, selection: model.rule(id)) {
                        Text(L("Record normally")).tag(ClipboardWatcher.AppRule.normal)
                        Text(L("Clear after an hour")).tag(ClipboardWatcher.AppRule.hourly)
                        Text(L("Don't record")).tag(ClipboardWatcher.AppRule.skip)
                    }
                }
            }
        }
        .formStyle(.grouped)
    }

    private var screenshots: some View {
        Form {
            Section {
                Toggle(isOn: model.binding({ Inbox.isEnabled }, model.actions.setInbox)) { described(L("Handle screenshots"), L("Screenshots hang instantly and skip the Desktop")) }
                Toggle(isOn: model.binding({ AppDelegate.copiesScreenshots },
                                                                      { AppDelegate.copiesScreenshots = $0 })) { described(L("Copy each screenshot"), L("Each new screenshot goes to the clipboard as it hangs")) }
                LabeledContent("") {
                    Button(L("Open screenshots folder")) { model.actions.openScreenshotsFolder() }
                }
            }
            Section(L("Also watch folders")) {
                let folders = { _ = model.version; return AppDelegate.extraFolders }()
                ForEach(folders, id: \.self) { folder in
                    LabeledContent(FileManager.default.displayName(atPath: folder.path)) {
                        HStack {
                            Button(String(format: L("Show “%@” in Finder"), FileManager.default.displayName(atPath: folder.path))) {
                                NSWorkspace.shared.open(folder)
                            }
                            Button(L("Stop watching")) { model.actions.removeExtraFolder(folder); model.reload() }
                        }
                    }
                }
                Button(L("Add Folder…")) { model.actions.chooseExtraFolder(); model.reload() }
                    .disabled(folders.count >= AppDelegate.maxExtraFolders)
            }
        }
        .formStyle(.grouped)
    }

    private var lineTab: some View {
        Form {
            Section(L("Bring the line down")) {
                Toggle(L("When the pointer rests in the menu bar"),
                       isOn: model.binding({ AppDelegate.opensFromMenuBar }, { AppDelegate.opensFromMenuBar = $0 }))
                Toggle(L("When something new hangs"),
                       isOn: model.binding({ AppDelegate.peeksAtNew }, { AppDelegate.peeksAtNew = $0 }))
                Toggle(L("Over full screen apps"),
                       isOn: model.binding({ FullScreen.showLineOver }, { FullScreen.showLineOver = $0 },
                                           then: model.actions.refresh))
            }
            Section {
                Picker(L("Keep on line"), selection: Binding(
                    get: { _ = model.version; return Line.keepOnLine ?? 0 },
                    set: { model.actions.setKeep($0 == 0 ? nil : $0); model.reload() })) {
                    Text(L("As many as fit")).tag(0)
                    ForEach(Line.keepChoices, id: \.self) { n in Text(String(format: L("%d photos"), n)).tag(n) }
                }
                Picker(L("Size"), selection: Binding(
                    get: { _ = model.version; return Layout.size },
                    set: { model.actions.setSize($0); model.reload() })) {
                    Text(L("Small")).tag(Layout.Size.small)
                    Text(L("Medium")).tag(Layout.Size.medium)
                    Text(L("Large")).tag(Layout.Size.large)
                }
                Toggle(L("Zoom on hover"), isOn: model.binding({ Zoom.isEnabled }, { Zoom.isEnabled = $0 },
                                                               then: { if !Zoom.isEnabled { model.actions.hideZoom() } }))
            }
        }
        .formStyle(.grouped)
    }
}
