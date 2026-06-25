# tachion v0.1.4 - all current fixes summary

This package is a clean full source snapshot containing the Windows client and VPS server needed for the current v0.1.4 behavior.

## Included fixes / features

### Windows client

- Whole-folder copy/move reliability fix
  - Folder trees are rescanned until stable.
  - Files still changing during copy are re-queued.
  - Prevents random partial folder uploads.

- 30-minute connection watchdog
  - If `Unable to connect to the remote server` continues for 30 minutes, sync stops automatically.
  - This prevents reconnect attempts from keeping Windows awake indefinitely.

- Global start/stop hotkey
  - Default: `Ctrl+Alt+T`.
  - Starts sync if stopped.
  - Stops sync if running/reconnecting.
  - Configurable in the UI.

- Always on top
  - Saved checkbox for keeping the main window above other windows.

- Delete-everywhere client support
  - Remote folder delete now deletes the full local folder recursively.
  - Pending child uploads are cancelled when a folder delete happens.

### VPS server

- Server-side delete support
  - Handles `delete` messages.
  - Deletes files or folders from VPS storage.
  - Broadcasts delete messages to connected desktops.

- Tombstones
  - Stores delete tombstones in `.tachion-tombstones.json` under the sync root.
  - Prevents offline desktops from resurrecting files/folders after reconnect.

## Version

Windows app version metadata is set to `0.1.4`, so the window title should show:

```text
tachion v0.1.4
```

## Delete server hotfix

The server delete handler is hardened so a failed delete is logged instead of closing the WebSocket connection. If the server cannot remove a path, it will not tombstone/broadcast that delete, preventing inconsistent resurrection behavior.
