# tachion Windows v0.1.5

## Added

- Added bulk folder import mode for production folders with hundreds of small nested files.
- When a folder is copied or moved into the sync folder, tachion now treats it as one import job.
- Child watcher noise is suppressed while the folder is still being copied.
- tachion waits until the folder tree becomes stable, then queues the final complete file list.
- Added bulk import progress logs, including visible file counts while scanning and queueing.
- Added post-import verification using the VPS REST file list when available.
- If verification finds missing or older files on the server, tachion re-queues them automatically.

## Server impact

- No mandatory VPS protocol change from v0.1.4 final server synthesis.
- v0.1.5 uses the existing REST endpoint `GET /files` for verification.
- Keep the v0.1.4 final server synthesis or newer on the VPS, because older servers without REST `/files` cannot provide verification.

---

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

## Server REST API hotfix

- Restored REST endpoints required by iOS/manual clients:
  - `GET /files`
  - `GET /files/{path}`
  - `PUT /files/{path}`
  - `DELETE /files/{path}`
- Kept WebSocket `/ws` route, delete-everywhere support, and tombstones.
- REST uploads/deletes now also broadcast updates to connected desktop WebSocket clients.

## Final server synthesis hotfix

- Combined strict WebSocket delete/tombstone handling with iOS REST endpoints.
- `GET /files` now returns the iOS-friendly `{ "files": [...] }` shape by default.
- `GET /files?format=manifest` remains available for manifest-style clients.
- REST auth accepts Bearer, `X-Sync-Token`, `X-Tachion-Token`, and `?token=`.
- REST PUT/DELETE broadcasts updates to connected desktop clients.
- Delete is idempotent and keeps tombstones to prevent resurrection.

