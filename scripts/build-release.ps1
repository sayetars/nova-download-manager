# Nova — Release Build (run on a Windows 11 machine with VS Build Tools + .NET 8 SDK + Flutter)
# Produces installer\payload\* for Inno Setup:
#   scripts\build-release.ps1
#
# Steps performed:
#   1. dotnet publish engine projects (framework-dependent, win-x64)
#   2. flutter build windows --release
#   3. copies extension build outputs
#   4. prepares installer\payload tree + host manifests
# Then run:  iscc installer\nova-installer.iss

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host '== 1/4 Publishing engine (win-x64, framework-dependent) =='
dotnet publish "$root\src\Nova.EngineHost" -c Release -r win-x64 --self-contained false `
  -o "$root\installer\payload\engine" /p:PublishSingleFile=false
dotnet publish "$root\src\Nova.BrowserHost" -c Release -r win-x64 --self-contained false `
  -o "$root\installer\payload\engine" /p:PublishSingleFile=false

Write-Host '== 2/4 Flutter Windows release =='
Push-Location "$root\apps\nova_desktop"
flutter build windows --release
Pop-Location
New-Item -ItemType Directory -Force "$root\installer\payload\app" | Out-Null
Copy-Item -Recurse -Force "$root\apps\nova_desktop\build\windows\x64\runner\Release\*" "$root\installer\payload\app\"

Write-Host '== 3/4 Extensions =='
New-Item -ItemType Directory -Force "$root\installer\payload\ext" | Out-Null
Push-Location "$root\extensions"
npm install
npm run package
Pop-Location
Copy-Item "$root\extensions\chromium\nova-chromium-1.0.0.zip" "$root\installer\payload\ext\"
Copy-Item "$root\extensions\firefox\nova-firefox-1.0.0.zip" "$root\installer\payload\ext\"
Copy-Item -Recurse "$root\extensions\chromium\dist" "$root\installer\payload\ext\chromium"
Copy-Item -Recurse "$root\extensions\firefox\dist" "$root\installer\payload\ext\firefox"

Write-Host '== 4/4 Host manifests =='
New-Item -ItemType Directory -Force "$root\installer\payload\hosts" | Out-Null
Copy-Item "$root\extensions\com.nova.downloadmanager.chromium.template.json" `
          "$root\installer\payload\hosts\com.nova.downloadmanager.chromium.json"
Copy-Item "$root\extensions\com.nova.downloadmanager.firefox.template.json" `
          "$root\installer\payload\hosts\com.nova.downloadmanager.firefox.json"
# dev placeholder for allowed origins — replace with the real store IDs when published
Write-Host 'NOTE: replace allowed_origins/allowed_extensions in the host manifests with your published extension IDs.'

Write-Host 'Done. Now run:  iscc installer\nova-installer.iss'
