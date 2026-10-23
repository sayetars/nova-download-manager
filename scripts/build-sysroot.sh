#!/bin/bash
# Build user-space sysroot with gtk-3 dev headers + clang toolchain (no root needed)
set -u
SYSROOT="$HOME/sysroot"
DEB_DIR="$HOME/debs"
mkdir -p "$SYSROOT" "$DEB_DIR"
cd "$DEB_DIR"

# Dependency closure of libgtk-3-dev and clang-19 (deps only, no recommends)
apt-cache depends --recurse --no-recommends --no-suggests --no-conflicts --no-breaks --no-replaces --no-enhances \
  libgtk-3-dev clang-19 2>/dev/null | grep -E '^[a-zA-Z0-9]' | sed 's/[<|].*$//' | sort -u > /tmp/closure.txt

# Get installed package list
dpkg-query -W -f='${Package}\n' 2>/dev/null | sort -u > /tmp/installed.txt

# Download: everything in closure that is NOT installed, plus any -dev packages in closure
# (some -dev are partially present; overlaying sysroot copies is harmless)
> /tmp/download.txt
while read -r pkg; do
  [ -z "$pkg" ] && continue
  if ! grep -qx "$pkg" /tmp/installed.txt; then
    echo "$pkg" >> /tmp/download.txt
  elif echo "$pkg" | grep -qE -- '-dev$|-dev:|^clang|^llvm'; then
    echo "$pkg" >> /tmp/download.txt
  fi
done < /tmp/closure.txt

sort -u /tmp/download.txt -o /tmp/download.txt
echo "Packages to download: $(wc -l < /tmp/download.txt)"

FAIL=0
while read -r pkg; do
  # skip virtual/odd packages
  [ -z "$pkg" ] && continue
  if ! apt-get download "$pkg" > /dev/null 2>&1; then
    echo "SKIP (unresolvable): $pkg"
  fi
done < /tmp/download.txt

echo "Extracting debs..."
for f in *.deb; do
  [ -e "$f" ] || continue
  dpkg-deb -x "$f" "$SYSROOT" || echo "EXTRACT FAIL: $f"
done
echo "Sysroot ready at $SYSROOT"
ls "$SYSROOT/usr/include/gtk-3.0/gtk/gtk.h" 2>/dev/null && echo "GTK HEADERS OK"
ls "$SYSROOT/usr/bin/clang-19" 2>/dev/null && echo "CLANG-19 OK"
