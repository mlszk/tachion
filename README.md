# tachion

Small personal file-sync client for Windows.

This is a lightweight WinForms tray app. It syncs a chosen local folder with a compatible WebSocket sync server.

## Features

- Windows tray app
- Simple settings window
- Local folder selection
- WebSocket sync
- Green/red tray state icon
- Optional run at Windows startup
- Token stored with Windows DPAPI, not as plain text
- Settings stored in `%APPDATA%\tachion\tachion.config.json`

## Important security note

Do not publish your real sync token.

The app stores the token encrypted with Windows DPAPI on each machine, but the server still uses a shared token model. Anyone with the correct server URL and token can join the same sync space.

For public/friend use, give each friend/server its own token and folder.

## Requirements

For framework-dependent builds:

- Windows
- .NET 8 Desktop Runtime

For building from source:

- .NET 8 SDK

## Build

From the project folder:

```powershell
cd TachionWinForms

dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
```

Output:

```text
TachionWinForms\bin\Release\net8.0-windows\win-x64\publish\tachion.exe
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

## Server protocol

The client uses the same simple JSON/WebSocket protocol as the original tachion/fsync prototype:

- `hello` with token, device name, and file manifest
- `put` for whole-file upload/download
- `request` to ask a peer/server for newer local files

The current design is intended for small personal files. It does not implement accounts, per-user permissions, block-level sync, or conflict copies.

## Repository hygiene

This repository should contain source code and generic defaults only.

Do not commit:

- real tokens
- `%APPDATA%\tachion\tachion.config.json`
- private VPS service files
- private nginx configs
- personal server names unless you want them public
