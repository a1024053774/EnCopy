import Foundation

/// Where this copy of EnCopy keeps its files: a folder in Application
/// Support named after the app. The development copy has a name of its own,
/// so what it stores never mixes with the everyday copy's.
enum DataFolder {
    static let url: URL = {
        guard let name = Bundle.main.object(forInfoDictionaryKey: "CFBundleName") as? String, !name.isEmpty else {
            fatalError("EnCopy names its data folder after its app bundle, so it must run from one")
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(name, isDirectory: true)
    }()
}
