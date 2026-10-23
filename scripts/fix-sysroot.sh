#!/bin/bash
# One-shot idempotent sysroot fixup: patch .pc prefixes, fix dangling .so links, clang wrappers
set -u
SYSROOT="$HOME/sysroot"

# 1) Patch all .pc files: force prefix to sysroot/usr
find "$SYSROOT" -name '*.pc' | while read -r pc; do
  sed -i "s|^prefix=/usr$|prefix=$SYSROOT/usr|" "$pc"
  sed -i "s|^prefix=\${pcfiledir}/../\.\.\.$|prefix=$SYSROOT/usr|" "$pc"
done

# 2) Fix dangling dev .so symlinks by copying real system libs (with remove-destination)
LIBDIR="$SYSROOT/usr/lib/x86_64-linux-gnu"
if [ -d "$LIBDIR" ]; then
  cd "$LIBDIR" || exit 1
  for l in $(find . -maxdepth 1 -xtype l -name '*.so' 2>/dev/null | sed 's|^\./||'); do
    [ -z "$l" ] && continue
    tgt=$(readlink "$l")
    if [ -e "/usr/lib/x86_64-linux-gnu/$tgt" ]; then
      cp -L --remove-destination "/usr/lib/x86_64-linux-gnu/$tgt" "$l" 2>/dev/null
    elif [ -e "/usr/lib/x86_64-linux-gnu/$l" ]; then
      cp -L --remove-destination "/usr/lib/x86_64-linux-gnu/$l" "$l" 2>/dev/null
    fi
  done
fi

# 3) clang wrappers
mkdir -p "$SYSROOT/wrap"
cat > "$SYSROOT/wrap/clang" <<EOF
#!/bin/bash
export LD_LIBRARY_PATH="$SYSROOT/usr/lib/x86_64-linux-gnu:\$LD_LIBRARY_PATH"
exec "$SYSROOT/usr/bin/clang-19" "\$@"
EOF
cat > "$SYSROOT/wrap/clang++" <<EOF
#!/bin/bash
export LD_LIBRARY_PATH="$SYSROOT/usr/lib/x86_64-linux-gnu:\$LD_LIBRARY_PATH"
exec "$SYSROOT/usr/bin/clang++-19" "\$@"
EOF
chmod +x "$SYSROOT/wrap/clang" "$SYSROOT/wrap/clang++"

# 4) Verify
export PKG_CONFIG_PATH="$SYSROOT/usr/lib/x86_64-linux-gnu/pkgconfig:$SYSROOT/usr/share/pkgconfig"
printf '#include <gtk/gtk.h>\nint main(){ gtk_init_check(0,0); return 0; }\n' > /tmp/t.c
"$SYSROOT/wrap/clang" /tmp/t.c $(pkg-config --cflags --libs gtk+-3.0) -o /tmp/t.out \
  && /tmp/t.out \
  && echo "=== GTK COMPILE+LINK+RUN OK ==="
