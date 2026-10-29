# Nova Download Manager

**نوا — مدیریت دانلود | Free forever. IDM-class, bilingual (EN + فارسی RTL), zero payments.**

Nova is a Windows download manager built to match and exceed Internet Download
Manager's feature set — with **no paid tiers, no trials, no feature locks, no ads,
ever** (see `LICENSE` and `LICENSE-THIRD-PARTY.md`).

## Components

| Part | Stack | Role |
|---|---|---|
| `engine/` | C# / .NET 8 LTS | Download engine: multi-segment dynamic splitting, scheduler, IPC, storage |
| `app/` | Flutter / Dart | Bilingual UI (English + Persian RTL), task list, video quality panel |
| `extensions/chrome` | MV3 | Browser takeover, floating video button, context menus |
| `extensions/firefox` | WebExtension | Same feature set via standard APIs |
| `scripts/` | — | Version injection, native-host installers, tests tooling |

## IDM-class features implemented

- **Dynamic multi-segment downloads** (up to 32 connections/file, dynamic splitting & rebalancing)
- **Resume** everywhere: HTTP ranges, FTP REST, SFTP seek, HLS segment cache
- **Mirrors + Metalink**: multiple URLs per task, failover, checksums (MD5/SHA-1/SHA-256/CRC32), PGP-aware
- **Protocols**: HTTP/1.1, HTTP/2, best-effort HTTP/3, FTP, FTPS, SFTP, HLS (AES-128 decrypt), video via yt-dlp
- **Site Grabber**: crawl with depth, include/exclude wildcard filters, mirroring site structure
- **Scheduler & queues**: per-queue concurrency, time windows (incl. overnight), priority, retry with backoff, auto-resume after network loss, shut down PC when done
- **Speed limiter**: global + per-queue token buckets
- **Video pipeline**: yt-dlp inspect → quality panel → download → FFmpeg merge (bestvideo+bestaudio)
- **Proxy**: system, manual HTTP, SOCKS4/4a/5 (hand-rolled wire protocol), PAC scripts (Jint), TLS client certs, Basic + Windows integrated auth
- **Integration**: clipboard URL watcher, `nova://` URL handling, ZIP preview list, antivirus hook, CLI (`add/list/pause/resume/cancel/remove/grab/video/status/stop`)
- **Robustness**: single-instance guard, versioned IPC handshake (proto 1), SQLite schema versioning (`user_version`), part-file format version (`fmt`), crash-safe segment resume
- **Distributions**: self-contained single-file engine, portable mode (`NOVA_PORTABLE=1`), CI-built installers on tags

## Quick start (dev, Linux self-test)

```bash
# engine
cd engine && dotnet run --project Nova.Engine        # serve mode (UDS + TCP 47613)
dotnet run --project Nova.Engine -- add https://example.com/file.zip
dotnet run --project Nova.Engine -- list

# tests (12 unit tests: framing, state machine, checksums, HLS parsing, DB)
dotnet test
```

Windows (CI): push a `v*` tag → GitHub Actions builds engine + Flutter app +
extension zips → GitHub Release. Local Windows build:

```powershell
dotnet publish engine/Nova.Engine -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
cd app; flutter build windows --release
```

Browser integration: run `scripts/install-native-host.ps1` with the engine
path + your Chrome/Edge extension IDs (or `-Firefox`).

## Free-forever guarantee

- Project license: **MIT** (`LICENSE`).
- Every dependency is free & redistributable: .NET (MIT), SQLite (public domain),
  FluentFTP / SSH.NET / HtmlAgilityPack / Jint (MIT/BSD), Flutter (BSD-3),
  yt-dlp (Unlicense), FFmpeg (LGPL build; user-supplied).
- No activation keys, no license servers, no paid tiers, no ads — anywhere in the code.

## Documentation

- `docs/KNOWN-LIMITATIONS.md` — honest platform limitations
- `CHANGELOG.md` — version history
- `scripts/` — tooling and test harnesses
