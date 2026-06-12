# tachion Windows v0.1.2

## Changes

- Added delete propagation from Windows desktop client.
- Local file delete sends a `delete` message to the server.
- Incoming remote delete removes the local file.
- Rename is handled as delete old path + upload new path.
- Pending upload is cancelled when the file is deleted.

## Requires

Server must also be updated to the delete-capable `syncd_server.py`.
