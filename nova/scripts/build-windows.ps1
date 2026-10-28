# Nova Download Manager - local Windows build (run from repo root)
# Requires: Windows 11 + .NET 8 SDK + Flutter stable + VS 2022 "Desktop development with C++"
$ErrorActionPreference = 'Stop'

$out = 'dist/Nova-Windows-x64'
New-Item -ItemType Directory -Force -Path "$out/engine" | Out-Null

Write-Host '== Building engine (win-x64, self-contained) ==' -ForegroundColor Cyan
dotnet publish engine/Nova.DownloadEngine -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$out/engine"
if ($LASTEXITCODE -ne 0) { throw 'engine build failed' }

Write-Host '== Building Flutter UI ==' -ForegroundColor Cyan
Push-Location app
if (-not (Test-Path windows)) { flutter create --platforms=windows --org ai.nova . }
flutter pub get
flutter build windows --release
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'flutter build failed' }
Copy-Item -Recurse -Force build/windows/x64/runner/Release/* ../$out
Pop-Location

Copy-Item -Force installer/README-ENGINE.txt "$out/engine/README.txt"

Write-Host "Done -> $out (UI: $out\nova.exe  |  engine CLI: $out\engine\NovaEngine.exe)" -ForegroundColor Green
