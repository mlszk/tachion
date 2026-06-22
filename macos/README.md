# tachion macOS client

Native macOS menu-bar client for tachion.

Version: `0.1.4`

## What this version includes

- menu bar app
- settings window
- local folder chooser
- server URL / device name / token fields
- token stored in macOS Keychain
- manual Start / Stop sync
- optional `Start sync when opened`
- WebSocket sync using the same JSON protocol as the Windows client
- whole-file upload/download with SHA-256 verification
- local delete propagation
- remote delete handling
- cleanup of empty parent folders after remote delete
- simple polling scanner every 1 second
- whole-folder copy/move support through recursive polling
- version shown in window title/menu
- app icon asset catalog

## Notes

This is still an experimental macOS client. It uses polling instead of native FSEvents. That is simple and reliable enough for early testing, but a later version can replace polling with a real macOS file-system event stream.

The app is currently non-sandboxed for easier folder access during the experiment. If you later want App Store/sandbox-style distribution, folder access should use security-scoped bookmarks.

## Build / run

Open `TachionMac.xcodeproj` in Xcode.

1. Select the `TachionMac` scheme.
2. Set signing if Xcode asks.
3. Build and Run.
4. Open the tachion menu-bar icon if the window is hidden.
5. Enter your real server URL.
6. Enter your token.
7. Choose a local folder.
8. Click **Save**, then **Start Sync**.

The default URL is a placeholder. This ZIP contains no private token and no private server URL.
