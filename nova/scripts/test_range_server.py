#!/usr/bin/env python3
"""Minimal HTTP server with Range support — used for Nova engine self-tests.

usage: test_range_server.py [port] [slow]
Serves an 8 MiB random file at /test.bin (written to /tmp/nova_src.bin).
'slow' adds ~10ms per 16 KiB chunk so pause/resume can be tested reliably.
"""
import hashlib
import http.server
import os
import sys
import time

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8799
SLOW = len(sys.argv) > 2 and sys.argv[2] == "slow"
SIZE = 8 * 1024 * 1024
SRC = "/tmp/nova_src.bin"

data = os.urandom(SIZE)
with open(SRC, "wb") as f:
    f.write(data)
print(f"serving {SRC} ({SIZE} bytes) sha256={hashlib.sha256(data).hexdigest()} slow={SLOW}", flush=True)


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _headers(self, code, length, extra=None):
        self.send_response(code)
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()

    def do_HEAD(self):
        self._headers(200, SIZE)

    def do_GET(self):
        rng = self.headers.get("Range")
        if rng:
            start, _, end = rng.replace("bytes=", "").partition("-")
            start = int(start)
            end = min(int(end), SIZE - 1) if end else SIZE - 1
            self._headers(206, end - start + 1,
                          {"Content-Range": f"bytes {start}-{end}/{SIZE}"})
            self._send(data[start:end + 1])
        else:
            self._headers(200, SIZE)
            self._send(data)

    def _send(self, blob):
        if SLOW:
            for i in range(0, len(blob), 16384):
                self.wfile.write(blob[i:i + 16384])
                time.sleep(0.01)
        else:
            self.wfile.write(blob)

    def log_message(self, *a):
        pass


http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
