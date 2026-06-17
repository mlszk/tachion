# tachion Windows releases

## v0.1.3

Folder copy reliability update and optional auto-sync on app launch.

Changes:

- Added separate checkbox: `Start sync when opened`.
- Window title now shows the executable version.
- `Run at Windows startup` still only launches the app; auto-sync is controlled separately.

- Copying or moving a whole folder into the sync root now queues all files inside it for upload.
- Directory create/rename events are now watched.
- FileSystemWatcher internal buffer increased to 64 KB.
- If Windows reports missed watcher events, tachion performs a full recursive rescan.
- Keeps v0.1.2 delete propagation.

## v0.1.2

Delete propagation update.

Changes:

- Local file deletion sends a delete message to the server.
- Remote delete messages delete the local file.
- Pending uploads are cancelled when a file is deleted.
- Renames are treated as delete old path + upload new path.

## v0.1.1

Locked-file reliability update.

Changes:

- Retries files that are still locked by CAD/editors.
- Ignores common lock/temp files such as `.dwl`, `.dwl2`, `.swp`, `~$...`, and `.tmp.*`.
