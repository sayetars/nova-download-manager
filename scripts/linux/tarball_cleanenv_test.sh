#!/usr/bin/env bash
# Shipped-tarball clean-environment test (v1.0.1 package, as a real user gets it):
#  1. extract the published tar.gz
#  2. replicate RUN-LINUX.sh's runtime-less swap (self-contained engine)
#  3. boot the engine under `env -i` (no dotnet, no dev vars)
#  4. drive it with the raw stdlib IPC probe: handshake -> real download -> bytes
# Everything happens inside one tool call (sandbox kills background processes
# between calls — D-005).
set -u
TARBALL="/home/z/my-project/download/nova-dm-1.3.0-linux-x64.tar.gz"
ROOT="/tmp/nova-dm-test"
PKG="$ROOT/nova-dm-1.3.0-linux-x64"
DATA="$ROOT/data"
rm -rf "$ROOT"; mkdir -p "$ROOT" "$DATA" "$ROOT/home"

tar xzf "$TARBALL" -C "$ROOT" || { echo "EXTRACT FAIL"; exit 1; }

# --- runtime-less swap, exactly as RUN-LINUX.sh does it (no dotnet on PATH) ---
NE="$PKG/bundle/NovaEngine"
if [ -f "$NE" ] && [ "$(stat -c%s "$NE")" -lt 1000000 ]; then
  if command -v dotnet >/dev/null 2>&1 && dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft.NETCore.App 8"; then
    echo "[tarball-test] dotnet 8 present in THIS shell — forcing runtime-less path via env -i"
  fi
  cp -f "$PKG/engine-self-contained/NovaEngine" "$NE" || exit 1
  cp -f "$PKG/engine-self-contained/libe_sqlite3.so" "$PKG/bundle/" 2>/dev/null || true
  echo "[tarball-test] self-contained engine swapped in: $(stat -c%s "$NE") bytes"
else
  echo "[tarball-test] engine not FDD-size? stat: $(stat -c%s "$NE" 2>/dev/null || echo missing)"
fi

# --- clean-env boot: no DOTNET_ROOT, no sdk paths, minimal HOME ---
env -i HOME="$ROOT/home" PATH=/usr/bin:/bin TMPDIR=/tmp \
  "$NE" --data "$DATA" > "$ROOT/engine.log" 2>&1 &
EPID=$!

UP=0
for i in $(seq 1 60); do
  if [ -S /tmp/CoreFxPipe_nova-engine-v1 ] && [ -f "$DATA/ipc.token" ]; then UP=1; break; fi
  if ! kill -0 "$EPID" 2>/dev/null; then break; fi
  sleep 0.5
done
if [ "$UP" != "1" ]; then
  echo "ENGINE BOOT FAIL (clean env)"; tail -20 "$ROOT/engine.log"; kill "$EPID" 2>/dev/null; exit 3
fi
echo "[tarball-test] engine up in clean env (pid $EPID): socket + ipc.token present"

# --- raw protocol probe: handshake + real download + byte-exact check ---
python3 /home/z/my-project/scripts/linux/ipc_probe.py /tmp/CoreFxPipe_nova-engine-v1 "$DATA"
RC=$?

kill "$EPID" 2>/dev/null; wait "$EPID" 2>/dev/null
if [ "$RC" = "0" ]; then echo "TARBALL CLEAN-ENV TEST: PASS"; else echo "TARBALL CLEAN-ENV TEST: FAIL ($RC)"; fi
exit $RC
