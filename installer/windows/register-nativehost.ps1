# Nova Windows native-messaging registration (user-level, no admin required).
# Run from an elevated-optional PowerShell in the extracted package folder:
#   powershell -ExecutionPolicy Bypass -File .\register-nativehost.ps1 [-ExtensionId <id>] [-AllUsers]
#
# Registers com.nova.downloadmanager for Chrome, Brave, Edge (registry) and
# Firefox (profile-dir JSON). Idempotent — re-running refreshes the paths.
param(
    [string]$ExtensionId = "REPLACE_WITH_CHROME_EXTENSION_ID",
    [switch]$AllUsers
)

$ErrorActionPreference = "Stop"
$Dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$HostBin = Join-Path $Dir "browserhost\NovaBrowserHost.exe"
if (-not (Test-Path $HostBin)) { Write-Error "NovaBrowserHost.exe not found at $HostBin"; exit 1 }

$NmName = "com.nova.downloadmanager"
$origins = @(
    "chrome-extension://$ExtensionId/",
    "chrome-extension://REPLACE_WITH_BRAVE_EXTENSION_ID/"
) | Where-Object { $_ -notmatch "REPLACE_WITH" }

$manifestChrome = Join-Path $Dir "nm-chromium.json"
@{
    name        = $NmName
    description = "Nova Download Manager - native messaging bridge"
    path        = $HostBin
    type        = "stdio"
    allowed_origins = $origins
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $manifestChrome

# --- Chromium-family browsers: registry key -> manifest path
$roots = @("HKCU:\Software")
if ($AllUsers) { $roots = @("HKLM:\Software", "HKLM:\Software\WOW6432Node") }
foreach ($root in $roots) {
    foreach ($b in "Google\Chrome", "Chromium", "BraveSoftware\Brave-Browser", "Microsoft\Edge") {
        $key = "$root\$b\NativeMessagingHosts\$NmName"
        try {
            New-Item -Path $key -Force | Out-Null
            Set-ItemProperty -Path $key -Name "(default)" -Value $manifestChrome
            Write-Host "registered: $key"
        } catch { Write-Warning "could not register $key : $_" }
    }
}

# --- Firefox: JSON manifest in the profile-wide native-messaging dir
$ffDir = Join-Path $env:APPDATA "Mozilla\NativeMessagingHosts"
New-Item -ItemType Directory -Force -Path $ffDir | Out-Null
@{
    name        = $NmName
    description = "Nova Download Manager - native messaging bridge"
    path        = $HostBin
    type        = "stdio"
    allowed_extensions = @("nova@novadm.dev")
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $ffDir "$NmName.json")
Write-Host "registered: $ffDir\$NmName.json"

Write-Host ""
Write-Host "NOTE: load the extensions in dev mode (chrome://extensions -> Load unpacked"
Write-Host "      -> select the extracted extensions\nova-chromium folder), then re-run"
Write-Host "      this script passing the real extension id:"
Write-Host "      .\register-nativehost.ps1 -ExtensionId <32-letter-id>"
Write-Host "Brave accepts the same id (its store id differs) — edit nm-chromium.json if needed."
