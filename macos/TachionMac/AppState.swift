import AppKit
import Foundation

@MainActor
final class AppState: ObservableObject {
    static var appVersionText: String {
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String
        return "v\(version ?? "0.1.4")"
    }

    @Published var syncDir: String
    @Published var syncUrl: String
    @Published var syncName: String
    @Published var token: String
    @Published var startSyncOnLaunch: Bool
    @Published var isRunning = false
    @Published var isConnected = false
    @Published var logText = ""

    var onStatusChanged: ((Bool, Bool) -> Void)?
    private var client: SyncClient?
    private var didAutoStart = false

    init() {
        let settings = SettingsStore.load()
        syncDir = settings.syncDir
        syncUrl = settings.syncUrl
        syncName = settings.syncName
        startSyncOnLaunch = settings.startSyncOnLaunch
        token = KeychainStore.loadToken() ?? ""
        log("Ready. Config: \(SettingsStore.configDescription)")
    }

    func chooseFolder() {
        let panel = NSOpenPanel()
        panel.title = "Choose tachion sync folder"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true

        if panel.runModal() == .OK, let url = panel.url {
            syncDir = url.path
        }
    }

    func saveSettings() {
        SettingsStore.save(TachionSettings(
            syncDir: syncDir,
            syncUrl: syncUrl,
            syncName: syncName,
            startSyncOnLaunch: startSyncOnLaunch
        ))
        KeychainStore.saveToken(token)
        log("Settings saved.")
    }

    func startSyncIfConfigured() {
        guard startSyncOnLaunch, !didAutoStart else { return }
        didAutoStart = true
        log("Auto-start sync enabled.")
        startSync()
    }

    func startSync() {
        saveSettings()

        guard URL(string: syncUrl) != nil else {
            log("Start failed: bad server URL.")
            return
        }

        stopSync()

        let config = SyncConfig(
            syncDir: syncDir,
            syncUrl: syncUrl,
            syncName: syncName,
            token: token
        )

        let newClient = SyncClient(
            config: config,
            log: { [weak self] text in
                Task { @MainActor in self?.log(text) }
            },
            status: { [weak self] connected in
                Task { @MainActor in
                    self?.isConnected = connected
                    self?.onStatusChanged?(self?.isRunning ?? false, connected)
                }
            }
        )

        client = newClient
        isRunning = true
        isConnected = false
        onStatusChanged?(true, false)
        newClient.start()
        log("Sync started.")
    }

    func stopSync() {
        client?.stop()
        client = nil
        isRunning = false
        isConnected = false
        onStatusChanged?(false, false)
    }

    func openSyncFolder() {
        let url = URL(fileURLWithPath: syncDir)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        NSWorkspace.shared.open(url)
    }

    func log(_ text: String) {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"

        logText.append("[\(formatter.string(from: Date()))] \(text)\n")

        if logText.count > 100_000 {
            logText.removeFirst(logText.count - 100_000)
        }
    }
}
