# Install updated tachion server v0.1.4

This update is required for delete-everywhere behavior. Updating only the Windows client is not enough.

## Replace server file

Copy:

```text
Server/syncd_server.py
```

to your VPS app folder, usually:

```bash
sudo cp syncd_server.py /opt/fsync-app/syncd_server.py
```

## Restart service

```bash
sudo systemctl restart fsync-server
sudo journalctl -u fsync-server -f
```

## Tombstones

The server stores delete tombstones here:

```text
/opt/fsync/.tachion-tombstones.json
```

This file prevents deleted files/folders from coming back when an offline desktop reconnects with old copies.

Do not manually delete it unless you intentionally want to forget recent deletions.

## If delete still reconnects

Check the server log immediately after trying a delete:

```bash
sudo journalctl -u fsync-server -n 80 --no-pager
```

The updated server logs rejected or failed deletes without killing the WebSocket.
