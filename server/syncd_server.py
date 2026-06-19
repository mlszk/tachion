#!/usr/bin/env python3
"""
syncd_server.py - minimal instant-sync hub for the VPS.

Holds the canonical copy of one folder and pushes every change to all
connected clients over persistent WebSockets.

Scope / assumptions:
  * Small files (DXF, configs, etc.). Transfers are whole-file, base64 over JSON.
  * Last-write-wins by file mtime.
  * Delete propagation is supported. Folder deletes create a tombstone so
    offline clients do not resurrect deleted files during reconciliation.
"""

import asyncio
import base64
import hashlib
import json
import os
import shutil
import time
from pathlib import Path

import uvicorn
from fastapi import FastAPI, WebSocket, WebSocketDisconnect

ROOT = Path(os.environ.get("SYNC_ROOT", "/opt/fsync")).resolve()
TOKEN = os.environ.get("SYNC_TOKEN", "change-me")
HOST = os.environ.get("SYNC_HOST", "127.0.0.1")   # behind nginx TLS; never bind public
PORT = int(os.environ.get("SYNC_PORT", "8765"))
TOMBSTONES_PATH = ROOT / ".tachion-tombstones.json"
TOMBSTONE_RETENTION_NS = 30 * 24 * 60 * 60 * 1_000_000_000  # 30 days

ROOT.mkdir(parents=True, exist_ok=True)
app = FastAPI()

clients: dict[WebSocket, str] = {}
clients_lock = asyncio.Lock()
tombstones: dict[str, int] = {}


def now_ns() -> int:
    return time.time_ns()


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def clean_rel(rel: str) -> str:
    return rel.replace("\\", "/").strip("/")


def is_same_or_child(rel: str, parent: str) -> bool:
    rel = clean_rel(rel)
    parent = clean_rel(parent)
    return rel == parent or rel.startswith(parent + "/")


def is_ignored_rel(rel: str) -> bool:
    rel = clean_rel(rel)
    if not rel:
        return True
    name = rel.rsplit("/", 1)[-1]
    return name == TOMBSTONES_PATH.name or ".tmp." in name


def safe_path(rel: str) -> Path:
    """Resolve a client-supplied relative path, refusing to escape ROOT."""
    rel = clean_rel(rel)
    p = (ROOT / rel).resolve()
    if p == ROOT:
        raise ValueError("refusing to operate on sync root itself")
    if ROOT not in p.parents:
        raise ValueError(f"path escapes root: {rel!r}")
    return p


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


def load_tombstones() -> None:
    global tombstones
    try:
        if TOMBSTONES_PATH.is_file():
            raw = json.loads(TOMBSTONES_PATH.read_text(encoding="utf-8"))
            tombstones = {clean_rel(k): int(v) for k, v in raw.items() if clean_rel(k)}
    except Exception as e:
        print(f"[server] could not load tombstones: {e}")
        tombstones = {}
    prune_tombstones(save=False)


def save_tombstones() -> None:
    try:
        tmp = TOMBSTONES_PATH.with_name(TOMBSTONES_PATH.name + f".tmp.{os.getpid()}")
        tmp.write_text(json.dumps(tombstones, indent=2, sort_keys=True), encoding="utf-8")
        os.replace(tmp, TOMBSTONES_PATH)
    except Exception as e:
        print(f"[server] could not save tombstones: {e}")


def prune_tombstones(save: bool = True) -> None:
    cutoff = now_ns() - TOMBSTONE_RETENTION_NS
    old = [rel for rel, ts in tombstones.items() if ts < cutoff]
    for rel in old:
        tombstones.pop(rel, None)
    if old and save:
        save_tombstones()


def newest_tombstone_for(rel: str, mtime_ns: int) -> tuple[str, int] | None:
    rel = clean_rel(rel)
    best: tuple[str, int] | None = None
    for t_rel, t_ns in tombstones.items():
        if t_ns >= mtime_ns and is_same_or_child(rel, t_rel):
            if best is None or t_ns > best[1]:
                best = (t_rel, t_ns)
    return best


def clear_tombstones_for_put(rel: str) -> None:
    """A newer put intentionally recreates this file/tree, so remove covering tombstones."""
    rel = clean_rel(rel)
    removed = False
    for t_rel in list(tombstones):
        if is_same_or_child(rel, t_rel) or is_same_or_child(t_rel, rel):
            tombstones.pop(t_rel, None)
            removed = True
    if removed:
        save_tombstones()


def add_tombstone(rel: str, delete_ns: int) -> None:
    rel = clean_rel(rel)
    if not rel:
        return
    tombstones[rel] = max(tombstones.get(rel, 0), delete_ns)
    # A folder tombstone covers children; remove redundant child tombstones.
    for t_rel in list(tombstones):
        if t_rel != rel and is_same_or_child(t_rel, rel):
            tombstones.pop(t_rel, None)
    prune_tombstones(save=False)
    save_tombstones()


def build_manifest() -> dict[str, int]:
    m: dict[str, int] = {}
    for p in ROOT.rglob("*"):
        if not p.is_file():
            continue
        rel = p.relative_to(ROOT).as_posix()
        if is_ignored_rel(rel):
            continue
        mt = p.stat().st_mtime_ns
        if newest_tombstone_for(rel, mt) is not None:
            continue
        m[rel] = mt
    return m


def read_file(rel: str):
    p = safe_path(rel)
    return p.read_bytes(), p.stat().st_mtime_ns


def msg_put(rel: str, data: bytes, mtime_ns: int) -> str:
    return json.dumps({
        "type": "put",
        "path": clean_rel(rel),
        "mtime_ns": mtime_ns,
        "hash": sha256(data),
        "data": base64.b64encode(data).decode("ascii"),
    })


def msg_delete(rel: str, delete_ns: int) -> str:
    return json.dumps({
        "type": "delete",
        "path": clean_rel(rel),
        "delete_ns": delete_ns,
        "mtime_ns": delete_ns,
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
        await asyncio.gather(*(safe_send(ws, message) for ws in targets), return_exceptions=True)


def cleanup_empty_parents(parent: Path) -> None:
    root = str(ROOT).rstrip("/\\")
    while parent != ROOT and str(parent).rstrip("/\\") != root:
        try:
            if any(parent.iterdir()):
                break
            parent.rmdir()
            parent = parent.parent
        except Exception:
            break


async def apply_put(m: dict, sender: WebSocket) -> None:
    rel = clean_rel(m["path"])
    if is_ignored_rel(rel):
        return
    mtime_ns = int(m["mtime_ns"])
    data = base64.b64decode(m["data"])
    if sha256(data) != m.get("hash"):
        return  # integrity check failed - drop it
    if newest_tombstone_for(rel, mtime_ns) is not None:
        return  # an equal/newer delete wins; do not resurrect
    path = safe_path(rel)
    if path.is_file() and path.stat().st_mtime_ns >= mtime_ns:
        return  # we already have this or something newer
    atomic_write(path, data, mtime_ns)
    clear_tombstones_for_put(rel)
    print(f"[server] stored {rel} from {clients.get(sender, '?')}")
    await broadcast(msg_put(rel, data, mtime_ns), exclude=sender)


async def apply_delete(m: dict, sender: WebSocket) -> None:
    rel = clean_rel(m.get("path", ""))
    if is_ignored_rel(rel):
        return
    delete_ns = int(m.get("delete_ns") or m.get("mtime_ns") or now_ns())

    try:
        path = safe_path(rel)
    except Exception as e:
        print(f"[server] rejected delete {rel!r} from {clients.get(sender, '?')}: {e}")
        return

    try:
        if path.is_dir():
            shutil.rmtree(path)
            print(f"[server] deleted folder {rel} from {clients.get(sender, '?')}")
        elif path.is_file():
            path.unlink()
            print(f"[server] deleted {rel} from {clients.get(sender, '?')}")
        else:
            print(f"[server] tombstoned already-missing {rel} from {clients.get(sender, '?')}")
        cleanup_empty_parents(path.parent)
    except Exception as e:
        # Never let one failed delete kill the WebSocket. Log it and keep the
        # connection alive. Do not tombstone/broadcast if the server copy could
        # not be removed, otherwise it may resurrect later from the VPS copy.
        print(f"[server] delete failed {rel} from {clients.get(sender, '?')}: {e}")
        return

    add_tombstone(rel, delete_ns)
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
        sent_deletes: set[str] = set()
        for rel, c_mtime in client_manifest.items():
            if is_ignored_rel(rel):
                continue
            tombstone = newest_tombstone_for(rel, int(c_mtime))
            if tombstone is not None:
                t_rel, t_ns = tombstone
                if t_rel not in sent_deletes:
                    await ws.send_text(msg_delete(t_rel, t_ns))
                    sent_deletes.add(t_rel)

        server_manifest = build_manifest()
        for rel, s_mtime in server_manifest.items():          # server newer/unique -> push
            c_mtime = client_manifest.get(rel)
            if c_mtime is None or s_mtime > int(c_mtime):
                data, mt = read_file(rel)
                await ws.send_text(msg_put(rel, data, mt))

        wanted = []
        for rel, c_mtime in client_manifest.items():          # client newer/unique -> ask
            if is_ignored_rel(rel):
                continue
            c_mtime = int(c_mtime)
            if newest_tombstone_for(rel, c_mtime) is not None:
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
                try:
                    await apply_delete(m, sender=ws)
                except Exception as e:
                    print(f"[server] delete handler error from {name}: {e}")
    except WebSocketDisconnect:
        pass
    except Exception as e:
        print(f"[server] {name} error: {e}")
    finally:
        async with clients_lock:
            clients.pop(ws, None)
        print(f"[server] {name} disconnected ({len(clients)} left)")


if __name__ == "__main__":
    load_tombstones()
    print(f"[server] serving {ROOT} on ws://{HOST}:{PORT}/ws")
    uvicorn.run(app, host=HOST, port=PORT, log_level="warning", ws_max_size=64 * 1024 * 1024)
