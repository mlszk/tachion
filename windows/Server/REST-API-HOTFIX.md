# Server REST API hotfix for tachion v0.1.4

This hotfix restores the REST endpoints required by the iOS/manual clients while keeping the v0.1.4 WebSocket delete/tombstone logic.

Restored endpoints:

```text
GET    /files
GET    /files/{path}
PUT    /files/{path}
DELETE /files/{path}
```

Authentication accepts the shared sync token using any of these styles:

```text
Authorization: Bearer <token>
X-Sync-Token: <token>
X-Tachion-Token: <token>
?token=<token>
```

Notes:

- `GET /files` returns the original lightweight manifest: `path -> mtime_ns`.
- `GET /files?format=list` returns a richer list with path, mtime_ns, and size.
- `PUT /files/{path}` stores a file, clears relevant tombstones, and broadcasts it to WebSocket desktops.
- `DELETE /files/{path}` tombstones/deletes the file or folder and broadcasts delete to WebSocket desktops.
