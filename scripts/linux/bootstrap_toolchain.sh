#!/usr/bin/env bash
# Toolchain bootstrap stage 1: dotnet SDK + flutter tarball download (resumable)
set -uo pipefail
R=/home/z/my-project
mkdir -p $R/sdk
# dotnet
if [ ! -x $R/sdk/dotnet/dotnet ]; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir $R/sdk/dotnet
else echo "dotnet OK"; fi
$R/sdk/dotnet/dotnet --version || true
# flutter 3.47.7 (hash-pinned in .metadata)
if [ ! -d $R/sdk/flutter/bin ]; then
  curl -C - -L -o /tmp/flutter.tar.xz \
    https://storage.googleapis.com/flutter_infra_release/releases/stable/linux/flutter_linux_3.47.7-stable.tar.xz \
    && echo FLUTTER_DL_DONE || echo FLUTTER_DL_PARTIAL
else echo "flutter OK"; fi
ls -la /tmp/flutter.tar.xz 2>/dev/null
