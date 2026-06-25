# tachion v0.1.5 — Bulk folder import reliability

This release is aimed at real production folders with many small files, for example:

```text
9 inserts × 12 layers × 4 files ≈ 432 files
2 KB – 900 KB each
```

## What changed

When a folder is copied or moved into the sync folder, tachion now treats it as a single bulk import job:

1. Detect the new folder.
2. Suppress noisy child file watcher events under that folder.
3. Rescan the folder tree until it becomes stable.
4. Count the final file list.
5. Queue uploads for all final files.
6. Wait for the upload queue to settle.
7. Ask the server REST API for `GET /files`.
8. Re-queue files that are missing or older on the server.

## VPS impact

No new mandatory server protocol was added for v0.1.5.

The verification step uses the existing REST endpoint from the v0.1.4 final server synthesis:

```text
GET /files
```

So the VPS should run `syncd_server.py` from v0.1.4 final server synthesis or newer.

If the REST check is unavailable, the Windows client still syncs normally, but logs that bulk verification was skipped.

## Expected log examples

```text
Bulk folder import detected Kit_001; waiting until folder tree is stable...
Bulk import still watching Kit_001: 186 file(s) visible so far...
Bulk import ready Kit_001: 432 file(s) found; queueing uploads...
Bulk import queued Kit_001: 50/432 file(s)...
Bulk import queued Kit_001: 432/432 file(s). Waiting for upload queue to settle...
Bulk import verified Kit_001: 432 file(s) present on server.
```
