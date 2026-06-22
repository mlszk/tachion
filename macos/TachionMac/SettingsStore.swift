import Foundation

struct TachionSettings: Codable {
    var syncDir: String
    var syncUrl: String
    var syncName: String
    var startSyncOnLaunch: Bool
}

enum SettingsStore {
    private static let suite = UserDefaults.standard
    private static let keySyncDir = "syncDir"
    private static let keySyncUrl = "syncUrl"
    private static let keySyncName = "syncName"
    private static let keyStartSyncOnLaunch = "startSyncOnLaunch"

    static var configDescription: String {
        "UserDefaults + Keychain"
    }

    static func load() -> TachionSettings {
        let defaultFolder = (FileManager.default.homeDirectoryForCurrentUser.path as NSString)
            .appendingPathComponent("tachion")

        return TachionSettings(
            syncDir: suite.string(forKey: keySyncDir) ?? defaultFolder,
            syncUrl: suite.string(forKey: keySyncUrl) ?? "wss://tachion.example.com/ws",
            syncName: suite.string(forKey: keySyncName) ?? Host.current().localizedName ?? "mac",
            startSyncOnLaunch: suite.bool(forKey: keyStartSyncOnLaunch)
        )
    }

    static func save(_ settings: TachionSettings) {
        suite.set(settings.syncDir, forKey: keySyncDir)
        suite.set(settings.syncUrl, forKey: keySyncUrl)
        suite.set(settings.syncName, forKey: keySyncName)
        suite.set(settings.startSyncOnLaunch, forKey: keyStartSyncOnLaunch)
    }
}
