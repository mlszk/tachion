import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("tachion \(AppState.appVersionText)")
                    .font(.largeTitle.bold())
                Spacer()
                statusBadge
            }

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 10) {
                GridRow {
                    Text("Local folder")
                    HStack {
                        TextField("~/tachion", text: $state.syncDir)
                            .textFieldStyle(.roundedBorder)
                        Button("Choose…") {
                            state.chooseFolder()
                        }
                    }
                }

                GridRow {
                    Text("Server URL")
                    TextField("wss://tachion.example.com/ws", text: $state.syncUrl)
                        .textFieldStyle(.roundedBorder)
                }

                GridRow {
                    Text("Device name")
                    TextField("macbook", text: $state.syncName)
                        .textFieldStyle(.roundedBorder)
                }

                GridRow {
                    Text("Token")
                    SecureField("sync token", text: $state.token)
                        .textFieldStyle(.roundedBorder)
                }
            }

            Toggle("Start sync when opened", isOn: $state.startSyncOnLaunch)

            HStack(spacing: 8) {
                Button("Save") {
                    state.saveSettings()
                }

                Button("Start Sync") {
                    state.startSync()
                }
                .disabled(state.isRunning)

                Button("Stop Sync") {
                    state.stopSync()
                }
                .disabled(!state.isRunning)

                Button("Open Folder") {
                    state.openSyncFolder()
                }

                Spacer()
            }

            Text("Log")
                .font(.headline)

            TextEditor(text: $state.logText)
                .font(.system(.body, design: .monospaced))
                .frame(minHeight: 250)
                .border(Color.secondary.opacity(0.25))
        }
        .padding(16)
    }

    private var statusBadge: some View {
        let text: String
        let color: Color

        if state.isConnected {
            text = "Connected"
            color = .green
        } else if state.isRunning {
            text = "Reconnecting"
            color = .orange
        } else {
            text = "Stopped"
            color = .red
        }

        return HStack(spacing: 6) {
            Circle()
                .fill(color)
                .frame(width: 10, height: 10)
            Text(text)
                .font(.headline)
        }
    }
}
