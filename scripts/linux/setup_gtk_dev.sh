#!/usr/bin/env bash
# Extract GTK3 + X11/Wayland dev packages into a user-space prefix (no root needed).
# Usage: setup_gtk_dev.sh  → prefix at ~/tools/gtk3-dev
set -euo pipefail

PREFIX="$HOME/tools/gtk3-dev"
WORK="$HOME/tools/gtk3-debs"
mkdir -p "$PREFIX" "$WORK"
cd "$WORK"

PKGS=(
  libgtk-3-dev
  libatk1.0-dev
  libatk-bridge2.0-dev
  libatspi2.0-dev
  libepoxy-dev
  libxrandr-dev
  libxinerama-dev
  libxi-dev
  libxcursor-dev
  libxfixes-dev
  libxdamage-dev
  libxcomposite-dev
  libxkbcommon-dev
  libwayland-dev
  libegl-dev
  libgl-dev
  libglx-dev
  libopengl-dev
  libgles-dev
  libdbus-1-dev
  libsystemd-dev
  libxtst-dev
  libcolord-dev
  libcloudproviders-dev
)

echo "== downloading =="
for p in "${PKGS[@]}"; do
  if [ ! -f ".done-$p" ]; then
    apt-get download "$p" 2>&1 | tail -1 || { echo "FAILED to download $p"; continue; }
    touch ".done-$p"
  fi
done

echo "== extracting =="
for deb in ./*.deb; do
  dpkg -x "$deb" "$PREFIX"
done

# Keep /usr/... layout; pkg-config prefix rewrite below.
echo "== rewriting pc prefixes =="
for pc in "$PREFIX"/usr/lib/x86_64-linux-gnu/pkgconfig/*.pc \
          "$PREFIX"/usr/share/pkgconfig/*.pc; do
  [ -f "$pc" ] || continue
  # Only rewrite files we extracted (they say prefix=/usr)
  if grep -q '^prefix=/usr' "$pc"; then
    sed -i "s|^prefix=/usr$|prefix=$PREFIX/usr|" "$pc"
  fi
done

echo "== fixing dangling .so symlinks (point at system runtime libs) =="
fix_symlinks() {
  local dir="$1"
  while IFS= read -r -d '' link; do
    target="$(readlink "$link")"
    case "$target" in
      /*) ;;  # absolute, leave
      *)
        base="$(basename "$target")"
        if [ ! -e "$link" ]; then
          if [ -e "/usr/lib/x86_64-linux-gnu/$base" ]; then
            ln -sf "/usr/lib/x86_64-linux-gnu/$base" "$link"
          elif [ -e "/usr/lib/$base" ]; then
            ln -sf "/usr/lib/$base" "$link"
          fi
        fi
        ;;
    esac
  done < <(find "$dir" -type l -print0)
}
fix_symlinks "$PREFIX/usr/lib"

export PKG_CONFIG_PATH="$PREFIX/usr/lib/x86_64-linux-gnu/pkgconfig:$PREFIX/usr/share/pkgconfig"

echo "== verification: pkg-config gtk+-3.0 =="
if pkg-config --cflags --libs gtk+-3.0 >/dev/null 2>&1; then
  echo "GTK3 PC OK"
  pkg-config --modversion gtk+-3.0
else
  echo "GTK3 PC STILL FAILING — missing modules:"
  for m in gtk+-3.0 gdk-3.0 atk epoxy x11 xext xinerama xi xrandr xcursor xfixes xdamage xcomposite xkbcommon wayland-client wayland-cursor wayland-egl colord cloudproviders cairo-gobject harfbuzz; do
    pkg-config --exists "$m" || echo "MISSING: $m"
  done
  exit 1
fi

echo "== header compile smoke test =="
cat > /tmp/gtk_smoke.c <<'EOF'
#include <gtk/gtk.h>
int main(void) { gtk_init_check(NULL, NULL); return 0; }
EOF
gcc /tmp/gtk_smoke.c -o /tmp/gtk_smoke $(pkg-config --cflags --libs gtk+-3.0) && echo "GTK3 COMPILE+LINK OK" || { echo "COMPILE FAILED"; exit 1; }
