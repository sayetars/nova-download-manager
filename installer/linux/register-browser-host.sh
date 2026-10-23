#!/usr/bin/env bash
# Register the Nova native-messaging host for Chromium/Brave/Chrome + Firefox.
# Run AFTER extracting the package; re-runnable (idempotent).
# The UI and engine do NOT require this — it only wires the browser
# extensions to the bundled NovaBrowserHost via native messaging.
set -eu
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_BIN="$DIR/engine-self-contained/NovaBrowserHost"
[ -x "$HOST_BIN" ] || HOST_BIN="$DIR/bundle/NovaBrowserHost"
[ -x "$HOST_BIN" ] || { echo "NovaBrowserHost not found" >&2; exit 1; }

CHROME_DIRS=(
  "$HOME/.config/google-chrome/NativeMessagingHosts"
  "$HOME/.config/chromium/NativeMessagingHosts"
  "$HOME/.config/BraveSoftware/Brave-Browser/NativeMessagingHosts"
  "$HOME/.config/vivaldi/NativeMessagingHosts"
  "$HOME/.config/microsoft-edge/NativeMessagingHosts"
)
FF_DIR="$HOME/.mozilla/native-messaging-hosts"

NM_NAME="com.nova.downloadmanager"

cat > "$DIR/.nm-chromium.json" <<EOF
{
  "name": "$NM_NAME",
  "description": "Nova Download Manager — native messaging bridge",
  "path": "$HOST_BIN",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://REPLACE_WITH_CHROME_EXTENSION_ID/",
    "chrome-extension://REPLACE_WITH_BRAVE_EXTENSION_ID/"
  ]
}
EOF
cat > "$DIR/.nm-firefox.json" <<EOF
{
  "name": "$NM_NAME",
  "description": "Nova Download Manager — native messaging bridge",
  "path": "$HOST_BIN",
  "type": "stdio",
  "allowed_extensions": ["nova@novadm.dev"]
}
EOF

for d in "${CHROME_DIRS[@]}"; do
  mkdir -p "$d"
  cp -f "$DIR/.nm-chromium.json" "$d/$NM_NAME.json"
  echo "registered: $d/$NM_NAME.json"
done
mkdir -p "$FF_DIR"
cp -f "$DIR/.nm-firefox.json" "$FF_DIR/$NM_NAME.json"
echo "registered: $FF_DIR/$NM_NAME.json"

echo
echo "NOTE: open each manifest and replace REPLACE_WITH_*_EXTENSION_ID with the"
echo "real extension ID after loading the unpacked extension from extensions/."
echo "Firefox: also set 'extensions.webextensions.background-delayed-startup' ok,"
echo "and allow the extension via about:debugging → Load Temporary Add-on."
