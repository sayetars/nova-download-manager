# Nova Download Manager — Engine bundle for Windows (x64)

**Version 1.6.0** — this bundle contains the *Windows engine side* of Nova,
cross-compiled from the official source tree (`dotnet publish -r win-x64
--self-contained` — no Windows machine was needed to build it).

## What is inside

| Folder | Contents |
|---|---|
| `engine\` | `NovaEngine.exe` — self-contained background engine (SQLite, scheduler, all download protocols, BitTorrent, HLS/DASH, yt-dlp bridge, partial preview server, JSON-RPC/WS remote control) |
| `browserhost\` | `NovaBrowserHost.exe` — native-messaging host bridging the browser extensions to the engine |
| `cli\` | `nova.exe` — command-line client (`nova add <url>`, `nova list`, `nova pause` …) |
| `extensions\` | `nova-chromium-1.6.0.zip` + `nova-firefox-1.6.0.zip` (unzip → load unpacked) |
| `register-nativehost.ps1` | Registers the native-messaging host (user-level, no admin) |
| `docs\` | Engine + IPC + browser-integration documentation |

## Quick start (engine + browser extension)

```powershell
# 1) start the engine (background process; it lives on \\.\pipe\nova-engine)
.\engine\NovaEngine.exe

# 2) unzip + load the extension
Expand-Archive extensions\nova-chromium-1.6.0.zip -DestinationPath extensions\chromium
# chrome://extensions → Developer mode → Load unpacked → extensions\chromium

# 3) wire the extension to the host (replace the id with the one chrome://extensions shows)
powershell -ExecutionPolicy Bypass -File .\register-nativehost.ps1 -ExtensionId <extension-id>
```

The floating download button on videos, quality panel, and "download with Nova"
context-menu entries are then live in the browser. `nova.exe` in `cli\` works
immediately (`cli\nova.exe list`).

## Where is the Windows GUI?

The Flutter desktop UI is **source-complete for Windows** (including the named-pipe
bridge and the `--mini` topmost window code in `apps/nova_desktop/windows/`), but a
Windows build of the UI requires a Windows host with the Visual Studio C++ toolchain —
Flutter cannot cross-compile Windows binaries on Linux.

Two supported paths:

1. **GitHub Actions (recommended).** The repository ships
   `.github/workflows/windows-build.yml`. Push the repo to any GitHub account →
   Actions → "Nova Windows Build" → it compiles engine + Flutter UI + extensions,
   builds the Inno Setup installer, and uploads the artifacts. No local Windows needed.
2. **Any Windows PC with VS 2022 + Flutter + .NET 8:**
   `flutter pub get && flutter build windows --release`, then `iscc installer\nova-installer.iss`.

Everything in this bundle (engine, browser host, extensions, CLI) already runs on
Windows today and is the exact set the installer puts beside the UI.

## Optional extras

- **yt-dlp**: drop `yt-dlp.exe` anywhere on PATH (or set its path in the UI/settings
  `ytdlp.path`) to enable the 1000+ site quality panel, including native video+audio
  merge downloads (v1.6.0).
- **ffmpeg** on PATH enables DASH audio+video muxing for non-DRM streams.

## Integrity

SHA256SUMS in this folder lists every file's digest. Binaries are unsigned —
SmartScreen may warn on first run ("More info" → "Run anyway").
