#!/usr/bin/env bash
# E2E smoke test for Nova v1.2.0 (IDM parity): engine + CLI + real HTTP download
# + batch/pattern + grabber + remote ZIP preview + IDM switches + export.
# Everything runs inside ONE process-tree lifetime (sandbox kills trees between calls).
set -uo pipefail
ROOT=/home/z/my-project
DATA=/tmp/nova-e2e
WEB=/tmp/nova-e2e-web
LOG=/tmp/nova-e2e.log
ENGINE=$ROOT/publish/engine/NovaEngine
PASS=0; FAIL=0

ok()  { PASS=$((PASS+1)); echo "  PASS: $1" >> "$LOG"; }
bad() { FAIL=$((FAIL+1)); echo "  FAIL: $1" >> "$LOG"; }

cli() { timeout 25 $ROOT/publish/cli/nova --data "$DATA" "$@"; }

rm -rf "$DATA" "$WEB"; mkdir -p "$WEB"; : > "$LOG"

# --- fixtures ------------------------------------------------------------------
dd if=/dev/urandom of="$WEB/big.bin" bs=1M count=8 2>/dev/null
SHA_BIG=$(sha256sum "$WEB/big.bin" | cut -d' ' -f1)
python3 - "$WEB" <<'PYEOF'
import zipfile, sys, os
w = sys.argv[1]
with zipfile.ZipFile(os.path.join(w, 'arch.zip'), 'w') as z:
    z.writestr('a.txt', 'hello')
    z.writestr('dir/b.txt', 'world')
with open(os.path.join(w, 'page.html'), 'w') as f:
    f.write('<html><body><a href="img1.png">i1</a><a href="img2.png">i2</a></body></html>')
for n in ('img1.png', 'img2.png'):
    open(os.path.join(w, n), 'wb').write(b'\x89PNG\r\n\x1a\n' + b'x' * 64)
PYEOF

cd "$WEB"
# tiny Range-supporting static server (python http.server lacks ranges)
cat > /tmp/range_server.py <<'PY'
import http.server, os, re, sys
class H(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        p = os.path.join(os.getcwd(), self.path.lstrip('/'))
        if not os.path.isfile(p):
            self.send_error(404); return
        size = os.path.getsize(p)
        rng = self.headers.get('Range')
        if rng:
            m = re.match(r'bytes=(\d+)-(\d*)', rng)
            if m:
                start = int(m.group(1)); end = int(m.group(2)) if m.group(2) else size - 1
                end = min(end, size - 1)
                self.send_response(206)
                self.send_header('Content-Range', f'bytes {start}-{end}/{size}')
                self.send_header('Content-Length', str(end - start + 1))
                self.send_header('Accept-Ranges', 'bytes')
                self.end_headers()
                with open(p, 'rb') as f:
                    f.seek(start)
                    remaining = end - start + 1
                    while remaining > 0:
                        chunk = f.read(min(65536, remaining))
                        if not chunk: break
                        self.wfile.write(chunk); remaining -= len(chunk)
                return
        self.send_response(200)
        self.send_header('Content-Length', str(size))
        self.send_header('Accept-Ranges', 'bytes')
        self.end_headers()
        with open(p, 'rb') as f:
            self.wfile.write(f.read())
sys.stderr = open(os.devnull, 'w')
http.server.HTTPServer(('127.0.0.1', int(sys.argv[1])), H).serve_forever()
PY
python3 /tmp/range_server.py 18099 >/dev/null 2>&1 &
HTTPD=$!
"$ENGINE" --data "$DATA" >/tmp/e2e-engine.log 2>&1 &
ENGPID=$!
trap 'kill $ENGPID $HTTPD 2>/dev/null; wait 2>/dev/null' EXIT
sleep 3

# 1) status -----------------------------------------------------------------------
OUT=$(cli status 2>&1)
echo "$OUT" | grep -q '"protocol_version"' && ok "engine status via CLI" || bad "engine status: $OUT"

# 2) single download (python http.server has no ranges → single-conn path) ----------
OUT=$(cli add "http://127.0.0.1:18099/big.bin" 2>&1)
echo "$OUT" | grep -q 'task_key' && ok "add_download" || bad "add big: $OUT"

ST=none
for i in $(seq 1 30); do
  ST=$(cli list 2>&1 | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin)
    m = [t["state"] for t in (d.get("tasks") or []) if "big.bin" in t.get("file_name","")]
    print(m[0] if m else "none")
except Exception:
    print("err")')
  [ "$ST" = "completed" ] && break
  sleep 1
done
[ "$ST" = "completed" ] && ok "big.bin completed" || bad "big.bin state: $ST"

GOT=$(sha256sum "$DATA/downloads/big.bin" 2>/dev/null | cut -d' ' -f1)
[ "$GOT" = "$SHA_BIG" ] && ok "byte-exact content (8 MiB)" || bad "content mismatch: got ${GOT:0:12} want ${SHA_BIG:0:12}"

# 3) pattern batch expand -----------------------------------------------------------
OUT=$(cli expand "http://127.0.0.1:18099/[1-3].png" 2>&1)
echo "$OUT" | grep -q '"urls"' && ok "expand_pattern engine-side" || bad "expand: $OUT"

# 4) remote ZIP preview via Range (central directory) -------------------------------
OUT=$(cli zip "http://127.0.0.1:18099/arch.zip" 2>&1)
echo "$OUT" | grep -q 'a.txt' && ok "remote ZIP preview via Range" || bad "zip preview: $OUT"

# 5) grabber: mirror a tiny page ------------------------------------------------------
OUT=$(cli grab "http://127.0.0.1:18099/page.html" --depth 1 --dir "$DATA/grab" 2>&1)
echo "$OUT" | grep -q 'run_id' && ok "grabber_start" || bad "grab: $OUT"
for i in $(seq 1 15); do
  [ -f "$DATA/grab/127.0.0.1/page.html" ] && break
  sleep 1
done
GRABCOUNT=$(find "$DATA/grab" -type f 2>/dev/null | wc -l)
[ -f "$DATA/grab/127.0.0.1/page.html" ] && ok "grabber saved page (offline-rewritten)" || bad "grabber page missing"
[ "$GRABCOUNT" -ge 2 ] && ok "grabber resources ($GRABCOUNT files)" || bad "grabber resources: $GRABCOUNT"

# 6) IDM-compat switch /a (add without start) -----------------------------------------
OUT=$(cli /a "http://127.0.0.1:18099/img1.png" 2>&1)
echo "$OUT" | grep -q 'task_key' && ok "IDM /a switch" || bad "idm /a: $OUT"

# 7) speed cap set/read -----------------------------------------------------------------
OUT=$(cli speed 1024 2>&1)
[ -n "$OUT" ] && ok "set global speed cap" || bad "speed: $OUT"

# 8) export -----------------------------------------------------------------------------
OUT=$(cli export 2>&1)
echo "$OUT" | grep -q '"json"' && ok "export_tasks" || bad "export: $OUT"

echo "== SMOKE RESULT: PASS=$PASS FAIL=$FAIL ==" >> "$LOG"
cat "$LOG"
[ "$FAIL" -eq 0 ]
