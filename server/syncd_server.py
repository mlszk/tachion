#!/usr/bin/env python3
"""
syncd_server.py - minimal instant-sync hub for the VPS.

Holds the canonical copy of one folder and pushes every change to all
connected clients over persistent WebSockets. This is the "warm central
push" trick that makes Dropbox feel instant, stripped to its bones.

Scope / assumptions:
  * Small files (DXF, configs, etc.). Transfers are whole-file, base64 over JSON.
  * Runs behind nginx TLS; bind to localhost or WireGuard IP, not public IP.
  * A shared token is the only auth. Fine for a private tool; not a bank vault.
  * Last-write-wins by file mtime.
  * Delete support exists through REST and WebSocket message type "delete".
    Tombstones prevent old desktop clients from resurrecting deleted files.

Run (normally via systemd; manual start for testing):
  pip install "fastapi" "uvicorn[standard]"
  SYNC_ROOT=/opt/tachion SYNC_HOST=127.0.0.1 SYNC_TOKEN=yoursecret python3 syncd_server.py
"""

import asyncio
import base64
import hashlib
import json
import os
import time
from pathlib import Path

import uvicorn
from fastapi import (FastAPI, WebSocket, WebSocketDisconnect,
                     Request, Header, HTTPException, Depends)
from fastapi.responses import FileResponse

ROOT = Path(os.environ.get("SYNC_ROOT", "/opt/fsync")).resolve()
TOKEN = os.environ.get("SYNC_TOKEN", "change-me")
HOST = os.environ.get("SYNC_HOST", "127.0.0.1")   # behind nginx TLS; never bind public
PORT = int(os.environ.get("SYNC_PORT", "8765"))

META_DIR = ROOT / ".tachion_meta"
TOMBSTONES_FILE = META_DIR / "deleted.json"
TOMBSTONE_KEEP_NS = int(os.environ.get("TOMBSTONE_KEEP_DAYS", "30")) * 24 * 60 * 60 * 1_000_000_000

ROOT.mkdir(parents=True, exist_ok=True)
META_DIR.mkdir(parents=True, exist_ok=True)
app = FastAPI()

clients: dict[WebSocket, str] = {}
clients_lock = asyncio.Lock()
tombstones_lock = asyncio.Lock()


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def safe_path(rel: str) -> Path:
    """Resolve a client-supplied relative path, refusing to escape ROOT."""
    rel = rel.replace("\\", "/").lstrip("/")
    p = (ROOT / rel).resolve()
    if p != ROOT and ROOT not in p.parents:
        raise ValueError(f"path escapes root: {rel!r}")
    return p


def rel_path(path: Path) -> str:
    return path.relative_to(ROOT).as_posix()


def is_internal_path(path: Path) -> bool:
    """Ignore tachion's own metadata files."""
    try:
        path.relative_to(META_DIR)
        return True
    except ValueError:
        return False


def should_list_file(path: Path) -> bool:
    if is_internal_path(path):
        return False
    if ".tmp." in path.name:
        return False
    return path.is_file()


def atomic_write(path: Path, data: bytes, mtime_ns: int) -> None:
    """Write to a temp file then rename - so a reader never sees a half file."""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + f".tmp.{os.getpid()}")
    with open(tmp, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)
    os.utime(path, ns=(mtime_ns, mtime_ns))


def load_tombstones_sync() -> dict[str, int]:
    try:
        with open(TOMBSTONES_FILE, "r", encoding="utf-8") as f:
            raw = json.load(f)
        return {str(k): int(v) for k, v in raw.items()}
    except FileNotFoundError:
        return {}
    except Exception as e:
        print(f"[server] tombstone load error: {e}")
        return {}


def save_tombstones_sync(tombstones: dict[str, int]) -> None:
    META_DIR.mkdir(parents=True, exist_ok=True)
    data = json.dumps(tombstones, indent=2, sort_keys=True).encode("utf-8")
    atomic_write(TOMBSTONES_FILE, data, time.time_ns())


def prune_tombstones_sync(tombstones: dict[str, int]) -> dict[str, int]:
    now = time.time_ns()
    return {rel: ts for rel, ts in tombstones.items() if now - int(ts) <= TOMBSTONE_KEEP_NS}


async def get_tombstones() -> dict[str, int]:
    async with tombstones_lock:
        tombstones = prune_tombstones_sync(load_tombstones_sync())
        save_tombstones_sync(tombstones)
        return tombstones


async def remember_delete(rel: str, delete_ns: int) -> None:
    async with tombstones_lock:
        tombstones = load_tombstones_sync()
        old = int(tombstones.get(rel, 0))
        if delete_ns > old:
            tombstones[rel] = delete_ns
        tombstones = prune_tombstones_sync(tombstones)
        save_tombstones_sync(tombstones)


async def forget_tombstone_if_newer_put(rel: str, mtime_ns: int) -> None:
    async with tombstones_lock:
        tombstones = load_tombstones_sync()
        deleted_ns = int(tombstones.get(rel, 0))
        if deleted_ns and mtime_ns > deleted_ns:
            tombstones.pop(rel, None)
            save_tombstones_sync(prune_tombstones_sync(tombstones))


def build_manifest() -> dict:
    m = {}
    for p in ROOT.rglob("*"):
        if should_list_file(p):
            m[rel_path(p)] = p.stat().st_mtime_ns
    return m


def read_file(rel: str):
    p = safe_path(rel)
    return p.read_bytes(), p.stat().st_mtime_ns


def msg_put(rel: str, data: bytes, mtime_ns: int) -> str:
    return json.dumps({
        "type": "put",
        "path": rel,
        "mtime_ns": mtime_ns,
        "hash": sha256(data),
        "data": base64.b64encode(data).decode("ascii"),
    })


def msg_delete(rel: str, delete_ns: int) -> str:
    return json.dumps({
        "type": "delete",
        "path": rel,
        "delete_ns": delete_ns,
        "mtime_ns": delete_ns,  # compatibility/name symmetry for clients
    })


async def safe_send(ws: WebSocket, message: str) -> None:
    try:
        await ws.send_text(message)
    except Exception:
        pass


async def broadcast(message: str, exclude: WebSocket | None = None) -> None:
    async with clients_lock:
        targets = [ws for ws in clients if ws is not exclude]
    if targets:
        await asyncio.gather(*(safe_send(ws, message) for ws in targets),
                             return_exceptions=True)


def cleanup_empty_parents(path: Path) -> None:
    parent = path.parent
    while parent != ROOT:
        if is_internal_path(parent):
            break
        try:
            parent.rmdir()
            parent = parent.parent
        except OSError:
            break


async def delete_one(rel: str, delete_ns: int | None = None) -> tuple[str, int]:
    """Delete one file if it exists, remember tombstone, return rel/delete time."""
    path = safe_path(rel)
    rel = rel_path(path)
    delete_ns = delete_ns or time.time_ns()

    if path.is_dir():
        raise IsADirectoryError(rel)

    if path.is_file():
        path.unlink()
        cleanup_empty_parents(path)

    await remember_delete(rel, delete_ns)
    return rel, delete_ns


async def apply_put(m: dict, sender: WebSocket) -> None:
    rel = m["path"]
    mtime_ns = int(m["mtime_ns"])
    data = base64.b64decode(m["data"])
    if sha256(data) != m.get("hash"):
        return  # integrity check failed - drop it

    tombstones = await get_tombstones()
    deleted_ns = int(tombstones.get(rel, 0))
    if deleted_ns and mtime_ns <= deleted_ns:
        print(f"[server] ignored old put for deleted {rel} from {clients.get(sender, '?')}")
        return

    path = safe_path(rel)
    if path.is_file() and path.stat().st_mtime_ns >= mtime_ns:
        return  # we already have this or something newer

    atomic_write(path, data, mtime_ns)
    await forget_tombstone_if_newer_put(rel, mtime_ns)
    print(f"[server] stored {rel} from {clients.get(sender, '?')}")
    await broadcast(msg_put(rel, data, mtime_ns), exclude=sender)


async def apply_delete(m: dict, sender: WebSocket) -> None:
    rel = m["path"]
    delete_ns = int(m.get("delete_ns") or m.get("mtime_ns") or time.time_ns())
    rel, delete_ns = await delete_one(rel, delete_ns)
    print(f"[server] deleted {rel} from {clients.get(sender, '?')}")
    await broadcast(msg_delete(rel, delete_ns), exclude=sender)


@app.websocket("/ws")
async def ws_endpoint(ws: WebSocket):
    await ws.accept()
    name = "unknown"
    try:
        hello = json.loads(await ws.receive_text())
        if hello.get("type") != "hello" or hello.get("token") != TOKEN:
            await ws.close(code=4001)
            return
        name = hello.get("name", "unknown")
        client_manifest = hello.get("manifest", {})  # rel -> mtime_ns
        async with clients_lock:
            clients[ws] = name
        print(f"[server] {name} connected ({len(clients)} total)")

        # ---- reconciliation (offline catch-up) ----
        server_manifest = build_manifest()
        tombstones = await get_tombstones()

        for rel, s_mtime in server_manifest.items():          # server newer/unique -> push
            c_mtime = client_manifest.get(rel)
            if c_mtime is None or s_mtime > int(c_mtime):
                data, mt = read_file(rel)
                await ws.send_text(msg_put(rel, data, mt))

        # Server-side deletions -> tell clients to delete local copies.
        for rel, deleted_ns in tombstones.items():
            c_mtime = client_manifest.get(rel)
            if c_mtime is not None and int(c_mtime) <= int(deleted_ns):
                await ws.send_text(msg_delete(rel, int(deleted_ns)))

        # Client newer/unique -> ask, except when server has a newer tombstone.
        wanted = []
        for rel, c_mtime in client_manifest.items():
            c_mtime = int(c_mtime)
            if rel in tombstones and c_mtime <= int(tombstones[rel]):
                continue
            if rel not in server_manifest or c_mtime > server_manifest[rel]:
                wanted.append(rel)
        if wanted:
            await ws.send_text(json.dumps({"type": "request", "paths": wanted}))

        # ---- steady state ----
        while True:
            m = json.loads(await ws.receive_text())
            if m.get("type") == "put":
                await apply_put(m, sender=ws)
            elif m.get("type") == "delete":
                await apply_delete(m, sender=ws)
    except WebSocketDisconnect:
        pass
    except Exception as e:
        print(f"[server] {name} error: {e}")
    finally:
        async with clients_lock:
            clients.pop(ws, None)
        print(f"[server] {name} disconnected ({len(clients)} left)")


# --- REST API for lightweight clients (iOS, web) ---------------------------
# Same token, same nginx TLS. Uploads/deletes here also broadcast to connected
# desktop clients, so a phone action lands on desktops immediately.
def require_token(authorization: str = Header(default="")):
    if authorization != f"Bearer {TOKEN}":
        raise HTTPException(status_code=401, detail="unauthorized")


@app.get("/files", dependencies=[Depends(require_token)])
def list_files():
    out = []
    for p in ROOT.rglob("*"):
        if should_list_file(p):
            st = p.stat()
            out.append({"path": rel_path(p),
                        "size": st.st_size, "mtime_ns": st.st_mtime_ns})
    return {"files": out}


@app.get("/files/{path:path}", dependencies=[Depends(require_token)])
def download_file(path: str):
    try:
        fp = safe_path(path)
    except ValueError:
        raise HTTPException(status_code=404, detail="not found")
    if not fp.is_file() or is_internal_path(fp):
        raise HTTPException(status_code=404, detail="not found")
    return FileResponse(str(fp), filename=fp.name,
                        media_type="application/octet-stream")


@app.put("/files/{path:path}", dependencies=[Depends(require_token)])
async def upload_file(path: str, request: Request):
    try:
        fp = safe_path(path)
    except ValueError:
        raise HTTPException(status_code=400, detail="bad path")
    if is_internal_path(fp):
        raise HTTPException(status_code=400, detail="bad path")

    data = await request.body()
    mtime_ns = time.time_ns()
    atomic_write(fp, data, mtime_ns)
    rel = rel_path(fp)
    await forget_tombstone_if_newer_put(rel, mtime_ns)
    print(f"[server] stored {rel} via REST upload")
    await broadcast(msg_put(rel, data, mtime_ns))   # push to connected desktops
    return {"path": rel, "size": len(data),
            "hash": sha256(data), "mtime_ns": mtime_ns}


@app.delete("/files/{path:path}", dependencies=[Depends(require_token)])
async def delete_file(path: str):
    try:
        fp = safe_path(path)
    except ValueError:
        raise HTTPException(status_code=404, detail="not found")
    if is_internal_path(fp):
        raise HTTPException(status_code=404, detail="not found")
    if not fp.is_file():
        raise HTTPException(status_code=404, detail="not found")

    rel, delete_ns = await delete_one(path)
    print(f"[server] deleted {rel} via REST")
    await broadcast(msg_delete(rel, delete_ns))
    return {"deleted": rel, "delete_ns": delete_ns}


if __name__ == "__main__":
    print(f"[server] serving {ROOT} on ws://{HOST}:{PORT}/ws")
    uvicorn.run(app, host=HOST, port=PORT, log_level="warning",
                ws_max_size=64 * 1024 * 1024)
