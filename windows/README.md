# tachion

Small personal file-sync client for Windows.

Version: **0.1.1**

This is a lightweight C# WinForms tray app. It syncs a chosen local folder with a compatible WebSocket sync server.

## Features

- Windows tray app
- Simple settings window
- Local folder selection
- WebSocket sync
- Green/red tray state icon
- Optional run at Windows startup
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
