#!/usr/bin/env bash
# Visual demo: UI up (auto-spawns engine) → smoke test pushes a real download
# through the SAME engine → screenshot shows the completed task in the UI.
set -u
export LD_LIBRARY_PATH="$HOME/tools/glvnd/usr/lib/x86_64-linux-gnu:$HOME/tools/xvfb-dev/usr/lib/x86_64-linux-gnu:$HOME/tools/clang-dev/usr/lib/x86_64-linux-gnu"
export __EGL_VENDOR_LIBRARY_FILENAMES="$HOME/tools/glvnd/usr/share/glvnd/egl_vendor.d/50_mesa.json"
export PATH="$HOME/flutter/bin:$HOME/.dotnet:$PATH"
XVFB="$HOME/tools/xvfb-dev/usr/bin/Xvfb"
BUNDLE="/home/z/my-project/apps/nova_desktop/build/linux/x64/release/bundle"

pkill -x Xvfb 2>/dev/null; pkill -x NovaEngine 2>/dev/null
sleep 0.5
rm -rf "$HOME/.local/share/nova" /tmp/CoreFxPipe_nova-engine-v1

"$XVFB" :99 -screen 0 1280x800x24 -nolisten tcp > /tmp/xvfb.log 2>&1 &
XPID=$!
sleep 1.5
export DISPLAY=:99
cd "$BUNDLE" || exit 1
./nova_desktop > /tmp/nova-ui.log 2>&1 &
UIPID=$!
sleep 6

# push a real download through the running engine (same one the UI talks to)
cd /home/z/my-project/apps/nova_desktop || exit 1
NOVA_SMOKE=1 flutter test test/smoke_ipc_test.dart > /tmp/nova-smoke2.out 2>&1
grep -E "All tests|Some tests" /tmp/nova-smoke2.out | tail -1

sleep 3
"$HOME/tools/xvfb-dev/usr/bin/xwd" -root -silent -out /tmp/nova-ui2.xwd
"$HOME/tools/xvfb-dev/usr/bin/xwdtopnm" /tmp/nova-ui2.xwd 2>/dev/null | \
  "$HOME/tools/xvfb-dev/usr/bin/pnmtopng" 2>/dev/null > /home/z/my-project/download/nova-linux-ui-download.png
ls -la /home/z/my-project/download/nova-linux-ui-download.png

kill "$UIPID" 2>/dev/null
pkill -x NovaEngine 2>/dev/null
kill "$XPID" 2>/dev/null
wait 2>/dev/null
true
