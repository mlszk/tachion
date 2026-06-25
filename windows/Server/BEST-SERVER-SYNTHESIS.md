# tachion v0.1.4 server synthesis

This server file merges the safer WebSocket/delete/tombstone logic with the iOS-friendly REST API shape.

Included:

- WebSocket endpoint: `/ws`
- iOS/manual REST endpoints:
  - `GET /files`
  - `GET /files/{path}`
  - `PUT /files/{path}`
  - `DELETE /files/{path}`
- Default `GET /files` response:

```json
{
  "files": [
    { "path": "folder/file.txt", "size": 123, "mtime_ns": 123456789 }
  ]
}
```

- Optional manifest format:

```text
GET /files?format=manifest
```

returns:

```json
{
  "folder/file.txt": 123456789
}
```

- REST auth accepts:
  - `Authorization: Bearer <token>`
  - `X-Sync-Token: <token>`
  - `X-Tachion-Token: <token>`
  - `?token=<token>`
- REST PUT broadcasts uploaded files to connected desktop WebSocket clients.
- REST DELETE creates tombstones and broadcasts delete to connected desktop WebSocket clients.
- Delete is idempotent: deleting an already-missing path still creates/broadcasts a tombstone.
- WebSocket per-message errors are logged without killing the whole connection when possible.
