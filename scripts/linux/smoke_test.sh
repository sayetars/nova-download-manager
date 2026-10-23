#!/usr/bin/env bash
# Linux smoke test: start the engine from the release bundle, run the gated
# flutter smoke test (real IPC + real HTTP download + byte check), stop engine.
set -u
BUNDLE="/home/z/my-project/apps/nova_desktop/build/linux/x64/release/bundle"
APP="/home/z/my-project/apps/nova_desktop"
export PATH="$HOME/flutter/bin:$HOME/.dotnet:$PATH"

rm -rf "$HOME/.local/share/nova" /tmp/CoreFxPipe_nova-engine-v1
cd "$BUNDLE" || exit 1
./NovaEngine > /tmp/nova-engine.log 2>&1 &
ENGINE_PID=$!

UP=0
for i in $(seq 1 30); do
  if [ -S /tmp/CoreFxPipe_nova-engine-v1 ] && [ -f "$HOME/.local/share/nova/ipc.token" ]; then
    UP=1; break
  fi
  sleep 0.5
done
if [ "$UP" != "1" ]; then
  echo "ENGINE FAILED TO BOOT"; cat /tmp/nova-engine.log; kill "$ENGINE_PID" 2>/dev/null; exit 3
fi
echo "engine up (pid $ENGINE_PID)"

cd "$APP" || exit 1
NOVA_SMOKE=1 flutter test test/smoke_ipc_test.dart > /tmp/nova-smoke.out 2>&1
RC=$?
tail -6 /tmp/nova-smoke.out

kill "$ENGINE_PID" 2>/dev/null
wait "$ENGINE_PID" 2>/dev/null
exit $RC
