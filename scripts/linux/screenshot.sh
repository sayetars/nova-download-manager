#!/usr/bin/env bash
# Screenshot the running UI for visual verification.
set -u
source "$(dirname "$0")/../env.sh"
export LD_LIBRARY_PATH="$HOME/tools/glvnd/usr/lib/x86_64-linux-gnu:$HOME/tools/xvfb-dev/usr/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}"
export __EGL_VENDOR_LIBRARY_FILENAMES="$HOME/tools/glvnd/usr/share/glvnd/egl_vendor.d/50_mesa.json"
export LIBGL_DRIVERS_PATH="$HOME/tools/glvnd/usr/lib/x86_64-linux-gnu/dri"
XVFB="/usr/bin/Xvfb"
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
sleep 9

XWD="$HOME/tools/xvfb-dev/usr/bin/xwd"
"$XWD" -root -out /tmp/nova-ui.xwd; echo "xwd rc=$?"
ls -la /tmp/nova-ui.xwd
"$HOME/tools/xvfb-dev/usr/bin/xwdtopnm" /tmp/nova-ui.xwd > /tmp/nova-ui.pnm 2>/tmp/xwdtopnm.err; echo "xwdtopnm rc=$?"
head -2 /tmp/xwdtopnm.err
"$HOME/tools/xvfb-dev/usr/bin/pnmtopng" /tmp/nova-ui.pnm > /home/z/my-project/download/nova-linux-ui.png 2>/tmp/pnmtopng.err; echo "pnmtopng rc=$?"
head -2 /tmp/pnmtopng.err
ls -la /home/z/my-project/download/nova-linux-ui.png

kill "$UIPID" 2>/dev/null
pkill -x NovaEngine 2>/dev/null
kill "$XPID" 2>/dev/null
wait 2>/dev/null
true
