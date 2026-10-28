#!/usr/bin/env python3
"""End-to-end IPC flow test against a running 'NovaEngine serve' process:
ping -> add-download -> pause -> resume -> completed -> sha256 verification.

Requires: test_range_server.py running in slow mode on port 8799,
          and 'NovaEngine serve' already listening on the Unix socket.
"""
import hashlib
import json
import os
import socket
import sys
import time

SOCK = "/tmp/nova-engine-v1.sock"
OUT = "/tmp/nova_flow.bin"


def call(sock, obj):
    sock.sendall((json.dumps(obj) + "\n").encode())
    data = b""
    while not data.endswith(b"\n"):
        chunk = sock.recv(65536)
        if not chunk:
            break
        data += chunk
    return json.loads(data.decode())


def wait_state(sock, tid, states, timeout=60):
    deadline = time.time() + timeout
    while time.time() < deadline:
        r = call(sock, {"v": 1, "type": "list-downloads", "payload": {}})
        t = next(x for x in r["data"] if x["id"] == tid)
        print(f"    state={t['state']:<12} received={t['received']}/{t['total']} speed={t['speedBps']:.0f}B/s")
        if t["state"] in states:
            return t
        time.sleep(0.4)
    raise TimeoutError(f"timed out waiting for {states}")


def main():
    if os.path.exists(OUT):
        os.remove(OUT)

    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.settimeout(15)
    s.connect(SOCK)

    r = call(s, {"v": 1, "type": "ping", "payload": {}})
    print("ping ->", r)
    assert r["ok"] and r["data"]["message"] == "pong", "ping failed"

    r = call(s, {"v": 1, "type": "add-download", "payload": {
        "url": "http://127.0.0.1:8799/test.bin",
        "filePath": OUT,
        "segments": 4}})
    print("add-download ->", r["data"]["state"], r["data"]["total"])
    assert r["ok"], "add-download failed"
    tid = r["data"]["id"]

    t = wait_state(s, tid, {"Downloading", "Completed"}, timeout=15)
    if t["state"] == "Completed":
        print("NOTE: finished too fast to test pause — still OK")

    if t["state"] == "Downloading":
        print("pausing ...")
        r = call(s, {"v": 1, "type": "pause-download", "payload": {"id": tid}})
        print("pause-download ->", r["data"]["state"], r["data"]["received"])
        assert r["ok"] and r["data"]["state"] == "Paused", f"pause failed: {r}"

        r = call(s, {"v": 1, "type": "resume-download", "payload": {"id": tid}})
        print("resume-download ->", r["data"]["state"])
        assert r["ok"] and r["data"]["state"] in ("Queued", "Downloading"), f"resume failed: {r}"

    t = wait_state(s, tid, {"Completed", "Failed"}, timeout=120)
    assert t["state"] == "Completed", f"download failed: {t.get('error')}"

    src = hashlib.sha256(open("/tmp/nova_src.bin", "rb").read()).hexdigest()
    dst = hashlib.sha256(open(OUT, "rb").read()).hexdigest()
    print("sha_src =", src)
    print("sha_out =", dst)
    print("RESULT:", "PASS — byte-identical" if src == dst else "FAIL — corruption detected")
    sys.exit(0 if src == dst else 1)


if __name__ == "__main__":
    main()
