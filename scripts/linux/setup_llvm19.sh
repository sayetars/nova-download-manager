#!/usr/bin/env bash
# User-space clang-19 (no root): download debs + dpkg -x into ~/tools/llvm-19
set -uo pipefail
PREFIX="$HOME/tools/llvm-19"; WORK="$HOME/tools/llvm19-debs"
mkdir -p "$PREFIX" "$WORK"; cd "$WORK"
if [ ! -x "$PREFIX/usr/bin/clang" ]; then
  apt-cache depends --recurse --no-recommends --no-suggests --no-conflicts --no-breaks --no-replaces --no-enhances \
    clang-19 libclang-common-19-dev 2>/dev/null | grep -E '^[a-zA-Z0-9]' | sed 's/[<|].*$//' | sort -u > /tmp/lclosure.txt
  dpkg-query -W -f='${Package}\n' 2>/dev/null | sort -u > /tmp/linstalled.txt
  PKGS=""
  while read -r p; do
    [ -z "$p" ] && continue
    grep -qx "$p" /tmp/linstalled.txt && continue
    case "$p" in
      clang*|llvm*|libclang*|libllvm*|libz3*|libtinfo*|libzstd*|libedit*|libxml2*|libicu*|libmd*|libbsd*) PKGS="$PKGS $p";;
    esac
  done < /tmp/lclosure.txt
  echo "Downloading:$PKGS"
  apt-get download $PKGS 2>&1 | tail -2
  for d in *.deb; do dpkg -x "$d" "$PREFIX"; done
fi
export PATH="$PREFIX/usr/bin:$PATH"
export LD_LIBRARY_PATH="$PREFIX/usr/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}"
clang --version | head -1
ls "$PREFIX/usr/lib/llvm-19/lib/clang/19" 2>/dev/null | head -3 || ls "$PREFIX/usr/lib/clang/19" 2>/dev/null | head -3
