#!/usr/bin/env bash
# Nova DM — shared toolchain environment (source this in every build/smoke call).
# Rebuilt per RECOVERY.md after any environment reset.
export NOVA_ROOT="/home/z/my-project"
export DOTNET_ROOT="$NOVA_ROOT/sdk/dotnet"
export FLUTTER_ROOT="$NOVA_ROOT/sdk/flutter"
export PATH="$FLUTTER_ROOT/bin:$DOTNET_ROOT:$HOME/tools/llvm-19/usr/bin:$HOME/.venv/bin:$HOME/tools/gtk3-dev/usr/bin:$PATH"
export LD_LIBRARY_PATH="$HOME/tools/llvm-19/usr/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}"
export PKG_CONFIG_PATH="$HOME/tools/gtk3-dev/usr/lib/x86_64-linux-gnu/pkgconfig:$HOME/tools/gtk3-dev/usr/share/pkgconfig${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
