#!/usr/bin/env python3
"""
syncd_server.py - tachion instant-sync hub for the VPS.

Holds the canonical copy of one folder and pushes every change to all
connected clients over persistent WebSockets.

Scope / assumptions:
  * Small files (DXF, configs, etc.). Transfers are whole-file, base64 over JSON.
  * Last-write-wins by file mtime.
  * Delete propagation is supported. Folder deletes create a tombstone so
    offline clients do not resurrect deleted files during reconciliation.
  * REST file endpoints for iOS/manual clients:
      GET    /files               file list (default) or raw manifest (?format=manifest)
      GET    /files/{path}        download
      PUT    /files/{path}        upload (broadcasts to desktops)
      DELETE /files/{path}        delete (tombstones + broadcasts)
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
from fastapi import FastAPI, WebSocket, WebSocketDisconnect, Request, HTTPException
from fastapi.responses import FileResponse

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


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

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


def actor_name(sender: WebSocket | None = None, fallback: str = "REST") -> str:
    if sender is None:
        return fallback
    return clients.get(sender, "?")


# ---------------------------------------------------------------------------
# tombstones
# ---------------------------------------------------------------------------

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
    for t_rel in list(tombstones):
        if t_rel != rel and is_same_or_child(t_rel, rel):
            tombstones.pop(t_rel, None)
    prune_tombstones(save=False)
    save_tombstones()


# ---------------------------------------------------------------------------
# manifest
# ---------------------------------------------------------------------------

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


def build_file_list() -> list[dict]:
    files: list[dict] = []
    for rel, mtime_ns in sorted(build_manifest().items()):
        try:
            p = safe_path(rel)
            files.append({
                "path": rel,
                "mtime_ns": mtime_ns,
                "size": p.stat().st_size,
            })
        except Exception:
            pass
    return files


def read_file(rel: str):
    p = safe_path(rel)
    return p.read_bytes(), p.stat().st_mtime_ns


# ---------------------------------------------------------------------------
# wire messages
# ---------------------------------------------------------------------------

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


# ---------------------------------------------------------------------------
# apply operations (shared by WebSocket and REST paths)
# ---------------------------------------------------------------------------

async def apply_put(m: dict, sender: WebSocket | None = None) -> dict:
    """Apply a put; returns {"status": "ok"|"skipped"|"ignored", ...}."""
    rel = clean_rel(m["path"])
    if is_ignored_rel(rel):
        return {"status": "ignored", "reason": "ignored path", "path": rel}
    mtime_ns = int(m["mtime_ns"])
    data = base64.b64decode(m["data"])

    provided_hash = m.get("hash")
    actual_hash = sha256(data)
    if provided_hash and provided_hash.lower() != actual_hash.lower():
        return {"status": "ignored", "reason": "hash mismatch", "path": rel}

    if newest_tombstone_for(rel, mtime_ns) is not None:
        return {"status": "ignored", "reason": "newer delete tombstone", "path": rel}

    path = safe_path(rel)
    if path.is_file() and path.stat().st_mtime_ns >= mtime_ns:
        return {"status": "skipped", "reason": "server has same or newer", "path": rel}

    atomic_write(path, data, mtime_ns)
    clear_tombstones_for_put(rel)
    who = actor_name(sender)
    print(f"[server] stored {rel} from {who}")
    await broadcast(msg_put(rel, data, mtime_ns), exclude=sender)
    return {"status": "ok", "path": rel, "mtime_ns": mtime_ns, "hash": actual_hash}


async def apply_delete(m: dict, sender: WebSocket | None = None) -> dict:
    """Apply a delete; returns {"status": "ok"|"ignored", ...}."""
    rel = clean_rel(m.get("path", ""))
    if is_ignored_rel(rel):
        return {"status": "ignored", "reason": "ignored path", "path": rel}
    delete_ns = int(m.get("delete_ns") or m.get("mtime_ns") or now_ns())
    who = actor_name(sender)

    try:
        path = safe_path(rel)
    except Exception as e:
        print(f"[server] rejected delete {rel!r} from {who}: {e}")
        return {"status": "ignored", "reason": str(e), "path": rel}

    try:
        if path.is_dir():
            shutil.rmtree(path)
            print(f"[server] deleted folder {rel} from {who}")
        elif path.is_file():
            path.unlink()
            print(f"[server] deleted {rel} from {who}")
        else:
            print(f"[server] tombstoned already-missing {rel} from {who}")
        cleanup_empty_parents(path.parent)
    except Exception as e:
        print(f"[server] delete failed {rel} from {who}: {e}")
        return {"status": "error", "reason": str(e), "path": rel}

    add_tombstone(rel, delete_ns)
    await broadcast(msg_delete(rel, delete_ns), exclude=sender)
    return {"status": "ok", "path": rel, "delete_ns": delete_ns}


# ---------------------------------------------------------------------------
# WebSocket endpoint
# ---------------------------------------------------------------------------

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
        client_manifest = hello.get("manifest", {})
        async with clients_lock:
            clients[ws] = name
        print(f"[server] {name} connected ({len(clients)} total)")

        # ---- reconciliation (offline catch-up) ----
        # tell client about files it has that were deleted
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

        # push files that are newer on the server
        server_manifest = build_manifest()
        for rel, s_mtime in server_manifest.items():
            c_mtime = client_manifest.get(rel)
            if c_mtime is None or s_mtime > int(c_mtime):
                data, mt = read_file(rel)
                await ws.send_text(msg_put(rel, data, mt))

        # request files that are newer on the client
        wanted = []
        for rel, c_mtime in client_manifest.items():
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


# ---------------------------------------------------------------------------
# REST API — flexible auth, mtime, and hash verification
# ---------------------------------------------------------------------------

def rest_token_candidates(request: Request) -> list[str]:
    """Extract token from any reasonable header or query param."""
    candidates: list[str] = []
    auth = request.headers.get("authorization") or request.headers.get("Authorization")
    if auth:
        if auth.lower().startswith("bearer "):
            candidates.append(auth[7:].strip())
        candidates.append(auth.strip())
    for header in ("x-sync-token", "x-tachion-token", "x-fsync-token", "sync-token", "token"):
        val = request.headers.get(header)
        if val:
            candidates.append(val.strip())
    for qname in ("token", "sync_token", "SYNC_TOKEN"):
        val = request.query_params.get(qname)
        if val:
            candidates.append(val.strip())
    return [c for c in candidates if c]


def require_rest_auth(request: Request) -> None:
    if TOKEN not in rest_token_candidates(request):
        raise HTTPException(status_code=401, detail="invalid or missing sync token")


def normalize_mtime_ns(value: str | None) -> int | None:
    """Accept ns, ms, or seconds — detect by magnitude."""
    if value is None or value == "":
        return None
    try:
        f = float(value)
        if f > 10_000_000_000_000_000:
            return int(f)                   # already nanoseconds
        if f > 10_000_000_000:
            return int(f * 1_000_000)       # milliseconds
        return int(f * 1_000_000_000)       # seconds
    except Exception:
        return None


def request_mtime_ns(request: Request) -> int:
    """Pull mtime from any header or query param the caller provides."""
    for header in ("x-mtime-ns", "x-tachion-mtime-ns", "x-sync-mtime-ns",
                   "x-mtime", "last-modified-ns"):
        mt = normalize_mtime_ns(request.headers.get(header))
        if mt is not None:
            return mt
    for qname in ("mtime_ns", "mtime", "modified"):
        mt = normalize_mtime_ns(request.query_params.get(qname))
        if mt is not None:
            return mt
    return now_ns()


def rest_rel_or_400(rel: str) -> str:
    rel = clean_rel(rel)
    if is_ignored_rel(rel):
        raise HTTPException(status_code=400, detail="invalid path")
    try:
        safe_path(rel)
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e)) from e
    return rel


@app.get("/files")
async def rest_list_files(request: Request):
    """Default: {\"files\": [...]} for iOS. Add ?format=manifest for raw dict."""
    require_rest_auth(request)
    fmt = (request.query_params.get("format") or "").lower()
    if fmt == "manifest":
        return build_manifest()
    return {"files": build_file_list()}


@app.get("/files/{rel:path}")
async def rest_get_file(rel: str, request: Request):
    require_rest_auth(request)
    rel = rest_rel_or_400(rel)
    path = safe_path(rel)
    if not path.is_file():
        raise HTTPException(status_code=404, detail="file not found")
    mt = path.stat().st_mtime_ns
    if newest_tombstone_for(rel, mt) is not None:
        raise HTTPException(status_code=404, detail="file was deleted")
    return FileResponse(
        path,
        filename=path.name,
        headers={
            "X-Mtime-Ns": str(mt),
            "X-SHA256": sha256(path.read_bytes()),
        },
    )


@app.put("/files/{rel:path}")
async def rest_put_file(rel: str, request: Request):
    require_rest_auth(request)
    rel = rest_rel_or_400(rel)
    data = await request.body()
    mtime_ns = request_mtime_ns(request)
    provided_hash = (
        request.headers.get("x-sha256")
        or request.headers.get("x-file-sha256")
        or request.query_params.get("hash")
    )
    actual_hash = sha256(data)
    if provided_hash and provided_hash.lower() != actual_hash.lower():
        raise HTTPException(status_code=400, detail="sha256 mismatch")

    result = await apply_put({
        "path": rel,
        "mtime_ns": mtime_ns,
        "hash": actual_hash,
        "data": base64.b64encode(data).decode("ascii"),
    })
    result["hash"] = actual_hash
    result["size"] = len(data)
    return result


@app.delete("/files/{rel:path}")
async def rest_delete_file(rel: str, request: Request):
    require_rest_auth(request)
    rel = rest_rel_or_400(rel)
    delete_ns = request_mtime_ns(request)
    return await apply_delete({"type": "delete", "path": rel, "delete_ns": delete_ns})


if __name__ == "__main__":
    load_tombstones()
    print(f"[server] serving {ROOT} on ws://{HOST}:{PORT}/ws")
    uvicorn.run(app, host=HOST, port=PORT, log_level="warning", ws_max_size=64 * 1024 * 1024)
