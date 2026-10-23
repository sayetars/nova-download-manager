#!/usr/bin/env bash
# Nova — development build script (Linux/macOS/Windows+WSL)
# Builds the C# solution, runs tests, builds extensions and Flutter analyze.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== .NET solution =="
dotnet build Nova.sln -c Debug

echo "== Tests =="
dotnet test Nova.sln -c Debug

echo "== Extensions =="
cd extensions && npm install && npm run package && cd ..

echo "== Flutter analyze + tests =="
cd apps/nova_desktop && flutter pub get && flutter analyze && flutter test && cd ../..

echo "All good. (Windows release: run scripts/build-release.ps1 on a Windows host, then iscc installer/nova-installer.iss)"
