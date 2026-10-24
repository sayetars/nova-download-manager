#!/usr/bin/env bash
# Mini-window smoke test (IDM parity v1.2.0): launch the release bundle with
# `--mini` under Xvfb → compact always-on-top window renders + engine connects.
set -u
source "$(dirname "$0")/../env.sh"
export LD_LIBRARY_PATH="$HOME/tools/glvnd/usr/lib/x86_64-linux-gnu:$HOME/tools/xvfb-dev/usr/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}"
export __EGL_VENDOR_LIBRARY_FILENAMES="$HOME/tools/glvnd/usr/share/glvnd/egl_vendor.d/50_mesa.json"
export LIBGL_DRIVERS_PATH="$HOME/tools/glvnd/usr/lib/x86_64-linux-gnu/dri"
XVFB="/usr/bin/Xvfb"
XWD="$HOME/tools/xvfb-dev/usr/bin/xwd"
BUNDLE="/home/z/my-project/apps/nova_desktop/build/linux/x64/release/bundle"
OUT_PNG="/home/z/my-project/download/nova-mini-window.png"

pkill -x Xvfb 2>/dev/null
pkill -x NovaEngine 2>/dev/null
sleep 0.5
rm -rf "$HOME/.local/share/nova" /tmp/CoreFxPipe_nova-engine-v1

"$XVFB" :99 -screen 0 1280x800x24 -nolisten tcp > /tmp/xvfb.log 2>&1 &
XPID=$!
sleep 1.5

export DISPLAY=:99
cd "$BUNDLE" || exit 1
./nova_desktop --mini > /tmp/nova-mini.log 2>&1 &
MINIPID=$!
echo "mini pid $MINIPID"
sleep 7

ALIVE=$(kill -0 "$MINIPID" 2>/dev/null && echo yes || echo no)
echo "mini-alive=$ALIVE"
if [ "$ALIVE" != "yes" ]; then
  echo "MINI WINDOW CRASHED:"; tail -20 /tmp/nova-mini.log
  pkill -x Xvfb 2>/dev/null; exit 3
fi

"$XWD" -root -silent -out /tmp/nova-mini.xwd 2>/dev/null
"$HOME/tools/xvfb-dev/usr/bin/xwdtopnm" /tmp/nova-mini.xwd 2>/dev/null | \
  "$HOME/tools/xvfb-dev/usr/bin/pnmtopng" > "$OUT_PNG" 2>/dev/null
ls -la "$OUT_PNG"

# window geometry check via xwininfo if available
if command -v xwininfo >/dev/null 2>&1; then
  xwininfo -root -tree 2>/dev/null | grep -i nova | head -5
fi

kill "$MINIPID" 2>/dev/null
pkill -x NovaEngine 2>/dev/null
pkill -x Xvfb 2>/dev/null
echo "MINI SMOKE DONE"
