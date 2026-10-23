#!/usr/bin/env python3
"""Raw nipc/1 probe — speaks the engine wire protocol directly over the Unix
socket with zero dev tooling (pure stdlib). Used to prove the SHIPPED tarball
works end-to-end: handshake -> add_download (real HTTP origin) -> byte-exact
file on disk. Events arriving mid-request are skipped by matching reply ids.

Usage: ipc_probe.py <socket-path> <engine-data-dir>
Exit 0 = full success; prints each step.
"""
import hashlib
import http.server
import json
import os
import socket
import struct
import sys
import threading
import time

SOCKET_PATH = sys.argv[1]
DATA_DIR = sys.argv[2]

MAX_FRAME = 4 * 1024 * 1024


def _recv_exact(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise IOError("socket closed")
        buf += chunk
    return buf


def send_env(sock, obj):
    payload = json.dumps(obj).encode("utf-8")
    sock.sendall(struct.pack("<i", len(payload)) + payload)


def read_env(sock):
    (n,) = struct.unpack("<i", _recv_exact(sock, 4))
    if not (0 < n < MAX_FRAME):
        raise IOError(f"bad frame length: {n}")
    return json.loads(_recv_exact(sock, n))


def call(sock, env, expect_id, timeout=30.0):
    """Send one request envelope, read frames until the matching reply id."""
    send_env(sock, env)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        resp = read_env(sock)
        if resp.get("id") == expect_id:
            return resp
        # broadcast event (id == null) — skip, do not let it eat replies
    raise TimeoutError(f"no reply with id {expect_id}")


def main():
    token = open(os.path.join(DATA_DIR, "ipc.token")).read().strip()
    assert len(token) >= 32, "ipc.token missing/short"

    # deterministic 2 MiB origin payload
    payload = bytes((i * 7 + 13) & 0xFF for i in range(2 * 1024 * 1024))
    sha_payload = hashlib.sha256(payload).hexdigest()

    class Origin(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path == "/file.bin":
                self.send_response(200)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
            else:
                self.send_response(404)
                self.end_headers()

        def log_message(self, *a):
            pass

    origin = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Origin)
    threading.Thread(target=origin.serve_forever, daemon=True).start()
    url = f"http://127.0.0.1:{origin.server_address[1]}/file.bin"

    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.settimeout(45)
    s.connect(SOCKET_PATH)

    # 1) handshake with the engine's token
    send_env(s, {
        "v": 1, "id": "h1", "type": "hello", "token": token,
        "payload": {"client": "python-clean-probe", "protocol_version": 1},
    })
    resp = read_env(s)
    assert resp.get("type") == "ok", f"handshake rejected: {resp}"
    print("[probe] handshake: ok")

    # 2) engine_status sanity
    resp = call(s, {"v": 1, "id": "s1", "type": "engine_status"}, "s1")
    assert resp.get("type") == "ok", f"engine_status rejected: {resp}"
    print("[probe] engine_status: ok")

    # 3) real download
    resp = call(s, {"v": 1, "id": "a1", "type": "add_download",
                    "payload": {"url": url, "auto_start": True}}, "a1")
    assert resp.get("type") == "ok", f"add_download rejected: {resp}"
    task_key = resp["payload"]["task_key"]
    print(f"[probe] add_download: ok ({task_key})")

    # 4) poll until completed
    state, final_path = None, None
    for _ in range(120):
        resp = call(s, {"v": 1, "id": "l1", "type": "list_tasks",
                        "payload": {"filter": "all", "limit": 50}}, "l1")
        tasks = resp.get("payload", {}).get("tasks", [])
        me = [t for t in tasks if t.get("task_key") == task_key]
        if me:
            state = me[0].get("state")
            final_path = me[0].get("final_path")
        if state in ("completed", "failed", "cancelled"):
            break
        time.sleep(0.5)
    assert state == "completed", f"task did not complete; state={state}"
    assert final_path and os.path.exists(final_path), "final_path missing on disk"

    # 5) byte-exact verification
    data = open(final_path, "rb").read()
    sha_file = hashlib.sha256(data).hexdigest()
    assert data == payload, "downloaded bytes differ from origin"
    print(f"[probe] file: {final_path}")
    print(f"[probe] sha256 origin={sha_payload}")
    print(f"[probe] sha256 file  ={sha_file}")
    print("[probe] PASS: shipped-engine end-to-end (handshake, download, byte-exact)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
