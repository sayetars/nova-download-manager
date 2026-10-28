#!/usr/bin/env bash
# Nova — assemble the Windows x64 ENGINE bundle zip (v1.6.0).
# Contains the cross-compiled (win-x64, self-contained) engine + browser host +
# CLI + browser extensions + native-messaging registration script + docs.
# The Flutter UI is NOT included (needs a Windows host — see README-WINDOWS.md
# and .github/workflows/windows-build.yml).
set -euo pipefail
ROOT=/home/z/my-project
VER=1.6.0
NAME="nova-engine-${VER}-win-x64"
STAGE="$ROOT/download/stage-$NAME"
OUT="$ROOT/download/$NAME.zip"

rm -rf "$STAGE" "$OUT"
PKG="$STAGE/$NAME"
mkdir -p "$PKG/engine" "$PKG/browserhost" "$PKG/cli" "$PKG/extensions" "$PKG/docs"

# 1) cross-compiled win-x64 self-contained binaries
cp -r "$ROOT/publish/win-x64/engine/." "$PKG/engine/"
cp -r "$ROOT/publish/win-x64/browserhost/." "$PKG/browserhost/"
cp -r "$ROOT/publish/win-x64/cli/." "$PKG/cli/"
find "$PKG" -name "*.pdb" -delete

# 2) extensions + registration + docs
cp "$ROOT/extensions/chromium/nova-chromium-${VER}.zip" "$PKG/extensions/"
cp "$ROOT/extensions/firefox/nova-firefox-${VER}.zip" "$PKG/extensions/"
cp "$ROOT/installer/windows/register-nativehost.ps1" "$PKG/"
cp "$ROOT/installer/windows/README-WINDOWS.md" "$PKG/"
cp "$ROOT"/docs/{ARCHITECTURE,BROWSER-INTEGRATION,DOWNLOAD-ENGINE,IPC-PROTOCOL,KNOWN-LIMITATIONS,SECURITY,VIDEO-DETECTION}.md "$PKG/docs/" 2>/dev/null || true
cp "$ROOT/LICENSE.txt" "$PKG/" 2>/dev/null || true

# 3) SHA256SUMS
cd "$PKG"
find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS

# 4) zip (LFN-safe)
cd "$STAGE"
zip -qr "$OUT" "$NAME"
sha256sum "$OUT"
du -sh "$OUT"
echo "PACKAGE READY: $OUT"
