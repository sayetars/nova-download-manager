# Nova Download Manager

A professional Windows 11 download manager: **Flutter** desktop UI, **C#/.NET 8** download engine, and smart **Chrome / Brave / Firefox** extensions with an on-player download button and a verified quality picker.

```
Flutter UI ──Named Pipes──▶ Nova Engine (C#) ──HTTP/HTTPS──▶ Real files
Browser ──▶ Native Messaging ──▶ Nova Engine ──▶ Real download
```

## Highlights

- **Real multi-part downloading** — range-aware segment workers, parallel connections, crash-safe `.novadl` temp files, byte-exact resume after pause / process restart / machine reboot.
- **One source of truth** — the C# engine is the ONLY component that downloads, writes files, and owns the SQLite database. Flutter and the extensions are pure IPC clients.
- **Verified quality picker** — extension candidates are probed by the engine (HLS master/media playlists, DASH manifests). No fabricated sizes or qualities; DRM and live streams are detected and refused.
- **Browser integration** — on-player overlay button, context menus, optional download interception, allow/blocklists.
- **Scheduler** — concurrency caps, global/per-task/per-queue speed limits, time windows (incl. overnight), battery policy, anti-starvation aging.
- **Bilingual UI** — full English + فارسی with true RTL and Persian digits.

## Repository layout

| Path | What |
|---|---|
| `apps/nova_desktop` | Flutter Windows app (Riverpod, custom M3 design, EN/FA) |
| `src/Nova.Domain` | Entities, state machine, filename policy (no dependencies) |
| `src/Nova.Persistence` | SQLite (WAL, migrations v1–v4), repositories |
| `src/Nova.Engine` | Multipart engine, probe, rate limiting, retries, media dispatch |
| `src/Nova.Scheduler` | Admission control, windows, battery policy |
| `src/Nova.Media` | HLS/DASH parsers, media pipeline, candidates service |
| `src/Nova.Ipc` | nipc/1 protocol + named-pipe server/client |
| `src/Nova.EngineHost` | Background engine process (owns everything) |
| `src/Nova.BrowserHost` | Native messaging host (stdio ↔ pipe) |
| `src/Nova.Windows` | Windows-only helpers (build-gated to Windows) |
| `extensions/` | Shared TS core + Chromium & Firefox packages |
| `tests/Nova.Engine.Tests` | 43 automated tests incl. end-to-end browser path |
| `installer/` | Inno Setup script |
| `docs/` | The documentation set |

## Quick start (development)

Requirements: .NET 8 SDK, Flutter (stable), Node.js 18+.

```bash
# 1) build + test the engine
dotnet test Nova.sln

# 2) build extensions
cd extensions && npm install && npm run package

# 3) Flutter app
cd ../apps/nova_desktop && flutter pub get && flutter test
```

Run the engine manually (dev): `src/Nova.EngineHost/bin/Debug/net8.0/NovaEngine`

Load extensions in dev mode: `chrome://extensions` → *Load unpacked* → `extensions/chromium/dist` (Brave: same). Firefox: `about:debugging` → *Load Temporary Add-on* → `extensions/firefox/dist/manifest.json`.

## Release (Windows)

On a Windows 11 machine with Visual Studio Build Tools + .NET 8 SDK + Flutter + Inno Setup 6:

```powershell
scripts\build-release.ps1     # engine + app + extensions → installer\payload
iscc installer\nova-installer.iss   # → dist\nova-setup-1.0.0.exe
```

## Documentation

- [Architecture](docs/ARCHITECTURE.md) — components, layers, data flow
- [IPC protocol](docs/IPC-PROTOCOL.md) — nipc/1 wire format and message catalog
- [Download engine](docs/DOWNLOAD-ENGINE.md) — multipart, resume, recovery internals
- [Video detection](docs/VIDEO-DETECTION.md) — how the on-player button works
- [Browser integration](docs/BROWSER-INTEGRATION.md) — extension setup and host registration
- [Security](docs/SECURITY.md) — threat model
- [Testing](docs/TESTING.md) — what is tested and how to run it
- [Build & release](docs/BUILD-AND-RELEASE.md)
- [Known limitations](docs/KNOWN-LIMITATIONS.md) — read this before using
