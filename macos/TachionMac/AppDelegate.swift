import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let state = AppState()
    private var window: NSWindow?
    private var statusItem: NSStatusItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        state.onStatusChanged = { [weak self] running, connected in
            DispatchQueue.main.async {
                self?.updateStatusIcon(running: running, connected: connected)
            }
        }

        createStatusItem()
        showWindow()
        state.startSyncIfConfigured()
    }

    private func createStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = StatusIcon.make(running: false, connected: false)
        item.button?.imagePosition = .imageOnly
        item.button?.toolTip = "tachion \(AppState.appVersionText)"

        let menu = NSMenu()
        let title = NSMenuItem(title: "tachion \(AppState.appVersionText)", action: nil, keyEquivalent: "")
        title.isEnabled = false
        menu.addItem(title)
        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "Open tachion", action: #selector(showWindowAction), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Start Sync", action: #selector(startSyncAction), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Stop Sync", action: #selector(stopSyncAction), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "Open Sync Folder", action: #selector(openFolderAction), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "Quit", action: #selector(quitAction), keyEquivalent: "q"))

        for item in menu.items {
            item.target = self
        }

        item.menu = menu
        statusItem = item
    }

    private func updateStatusIcon(running: Bool, connected: Bool) {
        statusItem?.button?.image = StatusIcon.make(running: running, connected: connected)
    }

    @objc private func showWindowAction() {
        showWindow()
    }

    private func showWindow() {
        if window == nil {
            let view = SettingsView().environmentObject(state)
            let controller = NSHostingController(rootView: view)
            let newWindow = NSWindow(contentViewController: controller)
            newWindow.title = "tachion \(AppState.appVersionText)"
            newWindow.setContentSize(NSSize(width: 860, height: 600))
            newWindow.minSize = NSSize(width: 760, height: 540)
            newWindow.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            newWindow.isReleasedWhenClosed = false
            window = newWindow
        }

        window?.center()
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc private func startSyncAction() {
        state.startSync()
    }

    @objc private func stopSyncAction() {
        state.stopSync()
    }

    @objc private func openFolderAction() {
        state.openSyncFolder()
    }

    @objc private func quitAction() {
        state.stopSync()
        NSApp.terminate(nil)
    }
}
