# tachion Windows client

Small personal file-sync client for Windows.

Version: **0.1.4**

This is a lightweight C# WinForms tray app. It syncs a chosen local folder with a compatible WebSocket sync server.

## Features

- Windows tray app
- Simple settings window
- Local folder selection
- WebSocket sync
- Green/red tray state icon
- Optional run at Windows startup
- Optional start sync when tachion opens
- Configurable global hotkey for start/stop sync, default `Ctrl+Alt+T`
- Optional always-on-top main window
- Token stored with Windows DPAPI, not as plain text
- Settings stored in `%APPDATA%\tachion\tachion.config.json`

## Requirements

For framework-dependent builds:

- Windows
- .NET 8 Desktop Runtime

For building from source:

- .NET 8 SDK

## Build

```powershell
cd TachionWinForms

dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
```

Output:

```text
TachionWinForms\bin\Release\net8.0-windows\win-x64\publish\tachion.exe
```

Or run:

```bat
build-release.bat
```

## Default settings

Fresh installs use safe placeholder defaults:

```text
Sync folder: D:\tachion
Server URL:  wss://tachion.example.com/ws
Device name: current Windows machine name
Token:       empty
```

Change these in the app before syncing.

## Security note

Do not publish your real sync token. The app stores the token encrypted with Windows DPAPI on each machine, but the current server design still uses a shared token model. Anyone with the correct server URL and token can join the same sync space.

## Changelog

### 0.1.1

Locked-file upload fix:

- file changes are debounced before upload
- locked files are retried for about two minutes instead of being skipped after one short retry window
- reads use shared access when possible
- files are checked for stable size/mtime before upload to avoid half-written files
- common lock/temp files are ignored: `.dwl`, `.dwl2`, `.swp`, Office `~$...`, and `.tmp.*`

### 0.1.0

Initial GitHub-ready Windows WinForms tray client.


## Current version

Windows client version: `0.1.4`

This version includes delete propagation, improved handling of whole-folder copy/move operations, a 30-minute offline connection watchdog, and a configurable global start/stop hotkey.


## Startup behavior

`Run at Windows startup` only launches the tachion app at Windows login.

`Start sync when opened` is a separate option. Enable it if you want sync to start automatically when tachion opens, including after Windows startup.


## Global hotkey

`Global toggle hotkey` starts sync when it is stopped and stops sync when it is running.

Default:

```text
Ctrl+Alt+T
```

Click the hotkey field and press a new key combination to change it. The hotkey must include Ctrl, Alt, or Shift plus a normal key.


## Always on top

Enable `Always on top` to keep the tachion main window above other windows while it is open. Closing the window still hides it to the tray as before.


## Delete propagation note

Delete propagation requires the updated VPS `syncd_server.py`. The server now understands `delete` messages and keeps tombstones so offline clients do not resurrect deleted folders/files during reconciliation.
