#!/usr/bin/env bash
# Nova Download Manager — Linux smart launcher.
# Picks the right engine variant (framework-dependent if a .NET 8 runtime is
# available, otherwise swaps in the bundled self-contained engine) and starts
# the UI, which auto-spawns the engine and connects over the local nipc/1 socket.
set -u
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUNDLE="$DIR/bundle"
NE="$BUNDLE/NovaEngine"

have_dotnet8() {
  if command -v dotnet >/dev/null 2>&1; then
    dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft.NETCore.App 8"
    return
  fi
  if [ -d "${DOTNET_ROOT:-/nonexistent}" ] && [ -x "$DOTNET_ROOT/dotnet" ]; then
    "$DOTNET_ROOT/dotnet" --list-runtimes 2>/dev/null | grep -q "Microsoft.NETCore.App 8"
    return
  fi
  false
}

# single-file engine extracts to ~/.cache/nova (writable fallback: TMPDIR).
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/nova"
mkdir -p "$DOTNET_BUNDLE_EXTRACT_BASE_DIR" 2>/dev/null || export DOTNET_BUNDLE_EXTRACT_BASE_DIR="${TMPDIR:-/tmp}"

# FDD apphost is small (<1 MB); the self-contained binary is ~67 MB.
if [ -f "$NE" ] && [ "$(stat -c%s "$NE")" -lt 1000000 ]; then
  if have_dotnet8; then
    echo "[nova] using framework-dependent engine (.NET 8 runtime detected)"
  elif [ -x "$DIR/engine-self-contained/NovaEngine" ]; then
    echo "[nova] no .NET 8 runtime found — installing self-contained engine"
    cp -f "$DIR/engine-self-contained/NovaEngine" "$NE"
    cp -f "$DIR/engine-self-contained/libe_sqlite3.so" "$BUNDLE/" 2>/dev/null || true
    rm -f "$BUNDLE"/*.pdb "$BUNDLE/NovaEngine.deps.json" "$BUNDLE/NovaEngine.dll" \
          "$BUNDLE/NovaBrowserHost.deps.json" "$BUNDLE/NovaBrowserHost.dll" \
          "$BUNDLE/Nova.Domain.dll" "$BUNDLE/Nova.Engine.dll" "$BUNDLE/Nova.Ipc.dll" \
          "$BUNDLE/Nova.Media.dll" "$BUNDLE/Nova.Persistence.dll" \
          "$BUNDLE/Nova.Scheduler.dll" "$BUNDLE/NovaBrowserHost" \
          "$BUNDLE/Microsoft.Data.Sqlite.dll" "$BUNDLE/SQLitePCLRaw.batteries_v2.dll" \
          "$BUNDLE/SQLitePCLRaw.core.dll" "$BUNDLE/SQLitePCLRaw.provider.e_sqlite3.dll" \
          "$BUNDLE/NovaBrowserHost.runtimeconfig.json" 2>/dev/null || true
  else
    echo "[nova] ERROR: .NET 8 runtime not found and no self-contained engine is present." >&2
    echo "[nova]        Install .NET 8 runtime: https://dotnet.microsoft.com/download/dotnet/8.0" >&2
    exit 1
  fi
fi

echo "[nova] starting UI…"
exec "$BUNDLE/nova_desktop"
