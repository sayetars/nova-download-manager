#!/usr/bin/env bash
# UI smoke test: run the real Flutter Linux bundle under Xvfb, let it
# auto-spawn the engine, screenshot the window, then tear everything down.
set -u
export LD_LIBRARY_PATH="$HOME/tools/glvnd/usr/lib/x86_64-linux-gnu:$HOME/tools/xvfb-dev/usr/lib/x86_64-linux-gnu:$HOME/tools/clang-dev/usr/lib/x86_64-linux-gnu"
export __EGL_VENDOR_LIBRARY_FILENAMES="$HOME/tools/glvnd/usr/share/glvnd/egl_vendor.d/50_mesa.json"
XVFB="$HOME/tools/xvfb-dev/usr/bin/Xvfb"
XWD="$HOME/tools/xvfb-dev/usr/bin/xwd"
BUNDLE="/home/z/my-project/apps/nova_desktop/build/linux/x64/release/bundle"

pkill -x Xvfb 2>/dev/null
pkill -x NovaEngine 2>/dev/null
sleep 0.5
rm -rf "$HOME/.local/share/nova" /tmp/CoreFxPipe_nova-engine-v1

"$XVFB" :99 -screen 0 1280x800x24 -nolisten tcp > /tmp/xvfb.log 2>&1 &
XPID=$!
sleep 1.5

export DISPLAY=:99
cd "$BUNDLE" || exit 1
./nova_desktop > /tmp/nova-ui.log 2>&1 &
UIPID=$!
echo "ui pid $UIPID"

# give the UI time to boot, spawn the engine and connect
sleep 8

ALIVE=$(kill -0 "$UIPID" 2>/dev/null && echo yes || echo no)
ENGINE_UP=0
[ -S /tmp/CoreFxPipe_nova-engine-v1 ] && ENGINE_UP=1
echo "ui-alive=$ALIVE engine-socket=$ENGINE_UP"

# window tree + screenshot
"$XWD" -root -silent -out /tmp/nova-ui.xwd 2>/dev/null
"$HOME/tools/xvfb-dev/usr/bin/xwdtopnm" /tmp/nova-ui.xwd 2>/dev/null | \
  "$HOME/tools/xvfb-dev/usr/bin/pnmtopng" > /home/z/my-project/download/nova-linux-ui.png 2>/dev/null
ls -la /home/z/my-project/download/nova-linux-ui.png 2>/dev/null

echo "--- engine log (tail) ---"
tail -5 /tmp/nova-engine.log 2>/dev/null
echo "--- ui log (head) ---"
head -15 /tmp/nova-ui.log

kill "$UIPID" 2>/dev/null
pkill -x NovaEngine 2>/dev/null
kill "$XPID" 2>/dev/null
wait 2>/dev/null
if [ "$ALIVE" = "yes" ] && [ "$ENGINE_UP" = "1" ]; then echo "UI SMOKE: PASS"; exit 0; fi
echo "UI SMOKE: FAIL"; exit 1
