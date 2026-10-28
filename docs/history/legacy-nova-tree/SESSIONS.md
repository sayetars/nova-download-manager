# Nova Build Sessions

## Session 2026-10-28 — "Deep gap research round 4 + first real implementation"

### Research (round 4) — new gaps found beyond rounds 1–3
| # | Gap | Source |
|---|-----|--------|
| 1 | SFTP protocol | aria2 |
| 2 | DLC/RSDF/CCF encrypted link containers | JDownloader |
| 3 | Debrid services (Real-Debrid/AllDebrid/Premiumize) | RD ecosystem |
| 4 | Cloud host resolvers (Google Drive, Dropbox, Mega) | Motrix weakness |
| 5 | Live HLS recording | FetchV |
| 6 | Jalali (Persian) calendar scheduling | Persepolis |
| 7 | DASH video+audio track merge | XDM |

All were implemented in code during this session.

### Implementation (the first session with real files — previous sessions
### produced plans only, zero code; this was the user's core complaint)

**Engine (C# .NET 8 LTS) — 6 projects, all compile, 0 warnings:**
- `Nova.Core` — DownloadTask aggregate, 12-state machine, versioned JSON
  envelopes, 60+ IPC message types, event bus, Jalali calendar math
- `Nova.Ipc` — length-prefixed framing, Named Pipe (Win) + UDS (Linux)
  transports, dispatcher, sessions, broadcast, client with pending-call map
- `Nova.Storage` — SQLite with versioned migrations, task repo, settings repo
- `Nova.Engine` — segmented HTTP downloader (dynamic split + work stealing,
  sparse .novapart preallocation, resume validation via ETag), probe,
  speed governor (token buckets), FTP/FTPS + SFTP (SSH.NET), HLS parser +
  AES-128 + live handling, DASH parser (SegmentTemplate/Timeline/List),
  yt-dlp bridge (JSON import), debrid (RD/AD/PM), container files (DLC/RSDF/CCF),
  cloud resolvers (GDrive/Dropbox), server profiler + mirror picker, checksums
  (MD5/SHA1/SHA256/SHA512/CRC32), orchestrator with concurrency gate + retry
  with exponential backoff
- `Nova.Scheduler` — queue rules (start/pause/limit/shutdown), Jalali + Gregorian
  matching, per-task scheduled start, shutdown-when-complete; Site Grabber
  (recursive, filters, polite 2 req/s)
- `Nova.Host` — daemon + native messaging host (4-byte LE stdio, auto engine
  connect) + CLI (add/list/pause/resume/cancel/speed/grab) + host installer

**Extensions:**
- Chrome/Brave/Edge MV3: download interception (onDeterminingFilename),
  video sniffer (DOM + performance API + fetch/XHR hook for m3u8/mpd),
  floating download bubble, popup with quality selector, context menus,
  keyboard shortcut, badge speed
- Firefox: same, adapted to gecko MV3 event page
- Generated PNG icons (16/48/128) programmatically

**Flutter UI (source complete, CI builds):**
- pubspec (window_manager, tray_manager, media_kit, msix…)
- IPC client with framing protocol identical to C# side
- App model with event stream + live progress
- Main page: nav rail categories, speed graph painter, engine status pill,
  speed limiter menu
- Task cards: progress, segments view, actions, preview button
- Add URL dialog, scheduler page (Jalali default), settings page
- Video preview player (media_kit on .novapart)
- l10n ARB files EN + FA

**CI:** windows-release.yml — 4 jobs: engine test+publish (win-x64
self-contained), Flutter build, package (Inno Setup + zip), Linux self-test.
Inno Setup script with HKCU native-host registration + novadl:// protocol.

### Test results
- 23/23 unit tests pass
- Integration: hello -> addUrl -> analyzing -> downloading -> completed,
  102400-byte file verified on disk
- Cross-compile: nova-host.exe = PE32+ console x86-64, 73MB self-contained

### Bugs found and fixed by tests (why tests matter)
1. Frame.Read treated mid-length EOF as success
2. SQLite Migrate() ran a command without the open transaction
3. AES round-trip used wrong PKCS7 pad in test
4. CCF parser rejected non-deflate fallback
5. Many compile-level API mismatches (SSH.NET package id, ProtocolType.Unix,
   Path.GetExtension, PropertyNameCaseInsensitive namespace, Scheduler class/
   namespace clash, lambda discard assignment)

### Decisions
- dotnet 8 (LTS), single-file self-contained publish for Windows
- yt-dlp is an optional sidecar binary (auto-located); no bundling
- BitTorrent deliberately out of v1.0 engine (recorded in limitations)
