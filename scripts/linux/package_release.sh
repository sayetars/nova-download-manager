#!/usr/bin/env bash
# Nova — assemble the Linux x64 release tarball (v1.2.0).
# Mirrors the v1.0.1 layout: bundle/ (flutter + FDD engine + browserhost),
# engine-self-contained/, extensions/, docs/, launcher scripts, SHA256SUMS.
set -euo pipefail
ROOT=/home/z/my-project
VER=1.2.0
NAME="nova-dm-${VER}-linux-x64"
STAGE="$ROOT/download/stage-$NAME"
OUT="$ROOT/download/$NAME.tar.gz"

rm -rf "$STAGE" "$OUT"
PKG="$STAGE/$NAME"
mkdir -p "$PKG/bundle" "$PKG/engine-self-contained" "$PKG/extensions" "$PKG/docs"

# 1) Flutter bundle
cp -r "$ROOT/apps/nova_desktop/build/linux/x64/release/bundle/." "$PKG/bundle/"

# 2) FDD engine files → bundle root (UI auto-spawns ./NovaEngine)
#    (files only: the runtimes/ RID tree is skipped — top-level libe_sqlite3.so resolves)
find "$ROOT/publish/engine" -maxdepth 1 -type f -exec cp {} "$PKG/bundle/" \;

# 3) FDD browser host files → bundle root (native-messaging manifest points here)
cp "$ROOT"/publish/browserhost/* "$PKG/bundle/"

# 4) self-contained single-file engine
cp "$ROOT/publish/engine-self-contained/NovaEngine" "$PKG/engine-self-contained/"
rm -f "$PKG/engine-self-contained/"*.pdb

# 5) CLI + extension zips
mkdir -p "$PKG/cli"
cp "$ROOT"/publish/cli/* "$PKG/cli/"
cp "$ROOT/extensions/chromium/nova-chromium-${VER}.zip" "$PKG/extensions/"
cp "$ROOT/extensions/firefox/nova-firefox-${VER}.zip" "$PKG/extensions/"

# 6) docs + license + launcher scripts
cp "$ROOT"/docs/{ARCHITECTURE,BROWSER-INTEGRATION,DOWNLOAD-ENGINE,IPC-PROTOCOL,KNOWN-LIMITATIONS,SECURITY,VIDEO-DETECTION}.md "$PKG/docs/" 2>/dev/null || true
cp "$ROOT/LICENSE.txt" "$PKG/" 2>/dev/null || true
cp "$ROOT/installer/linux/RUN-LINUX.sh" "$PKG/"
cp "$ROOT/installer/linux/register-browser-host.sh" "$PKG/"
cp "$ROOT/installer/linux/README-QUICKSTART-LINUX.md" "$PKG/"

# 7) SHA256SUMS
cd "$PKG"
find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS

# 8) tar.gz
cd "$STAGE"
tar czf "$OUT" "$NAME"
sha256sum "$OUT"
du -sh "$OUT"
echo "PACKAGE READY: $OUT"
