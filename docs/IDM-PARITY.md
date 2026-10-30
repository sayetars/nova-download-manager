# IDM Feature-Parity Gap Analysis

> Date: 2026-10-24 · Baseline audited: **Nova v1.0.1** (Linux-verified release) — **updated for v1.1.0 (P0 1–12 + P1 13–19), v1.2.0 (P2 complete: per-server caps #14, special keys #23, queue scheduling #26, sync queues #28, sortable columns #36, mini-window #44), and v1.8.0 (full audit refresh 2026-10-30: every row re-verified against fresh primary-source research + the completion wave below). Remaining PARTIAL rows are deliberate design trade-offs (§8) or platform-scoped.**

Research sources (2026-10-30 refresh): official `internetdownloadmanager.com` pages — features2.html (21 enumerated features), support/options.html (9-tab Options dialog), support/starting.html (automatic mode + special keys), support/command_line.html (CLI switches), support/idm-scheduler/idm_scheduler.html (queue + retry semantics), support/idm-grabber/wheretosearch.html (wizard step 3) — cross-checked against a direct code audit of this repository.

Status legend: **HAVE** (implemented & verified) · **PARTIAL** (exists but incomplete vs. IDM) · **MISSING** (not present) · **EXCLUDED** (deliberately not implemented — see §8).

---

## 1. Where Nova stands today (v1.8.0, verified in code + tests)

- Real multipart engine: range-probed plans, parallel segment workers, in-flight dynamic re-split, shared-filestream writes at absolute offsets, crash-safe `.novadl` resume (ETag/Last-Modified validated, with an opt-out), retry with exponential backoff + `Retry-After`, optional SHA-256 verification, disk-space gates, unique final names, path-traversal-safe names, mirror failover, Metalink v3/v4.
- Protocols: HTTP/HTTPS (HTTP/2 offered on https since v1.8.0), FTP/FTPS, SFTP, WebDAV, HLS (incl. AES-128), DASH, magnet/BitTorrent, RSS/Atom, yt-dlp bridge.
- Proxy: static HTTP/SOCKS4/4a/5 + auth + bypass; **PAC (proxy auto-config) scripts via Jint** (v1.8.0) with `Get System` OS detection (Windows registry / gsettings / macOS scutil).
- Scheduler: global/per-queue concurrency caps, time-window rules with per-window speed caps, battery policy, anti-starvation priority aging, **per-queue retry defaults + lone-task infinite 30 s retry (v1.8.0)**, sync queues, per-queue post actions incl. **open file / run program (v1.8.0)**.
- Auth: login vault (encrypted, host **and URL-prefix entries** — v1.8.0), **interactive 401/407 login dialog with re-prompt on rejection (v1.8.0)**, TLS client certificates.
- Browser integration: Chrome/Brave (MV3, any Chromium) + Firefox; video overlay + verified quality panel; context menus (link/video/page/all-links/multi-select); allow/blocklists; interception opt-in; special keys incl. **Insert (IDM's default) — v1.8.0**.
- UI: EN/FA with true RTL + Persian digits, light/dark/system themes, search, state filters, category icons with auto-assign + per-category default dirs + **"change folder on last selected" (v1.8.0)**, sortable list + **shown-fields chooser (v1.8.0)**, four-mode add dialog with Save As, task details sheet with segments + speed graph, history, mini window, **tray (Windows, v1.8.0)**, **per-event sounds (v1.8.0)**, **downloaded-file drag-out to Explorer (Windows, v1.8.0)**.
- Engine host: autostart at login (**v1.8.0**: Windows HKCU Run key / Linux XDG autostart), `idman.exe`-compatible CLI, ZIP preview, Site Grabber, antivirus hook, quotas, quick update with **weekly auto-check (v1.8.0)**.
- **320/320 automated engine tests + 13/13 Flutter tests + 0 analyze errors at v1.8.0** on this repo.

## 2. IDM feature matrix vs Nova (v1.8.0)

### 2.1 Download engine

| # | IDM capability | Nova v1.8.0 | Notes |
|---|---|---|---|
| 1 | Dynamic file segmentation (re-splits largest remaining segment in flight) | **HAVE** | `TryDynamicSplit` in TaskRunner |
| 2 | Connection reuse without re-connect/login stages | **HAVE** | `SocketsHttpHandler` pooling + HTTP/2 multiplexing (v1.8.0) |
| 3 | Up to 32 connections per file | **HAVE** | Engine clamps 1–32; UI chips 1–32 |
| 4 | Resume broken/interrupted downloads | **HAVE** | Byte-exact, crash-safe, re-probed; v1.8.0 fixed the multi-segment planMatches bug (silent restarts since v1.1.0) |
| 5 | Error recovery & auto-retry | **HAVE** | Backoff + jitter, per-task budget, per-queue defaults (v1.8.0), lone-task infinite 30 s retry (v1.8.0) |
| 6 | Protocols: HTTP, HTTPS, FTP, MMS | **HAVE** | MMS/RTSP excluded as dead tech (§8); SFTP/WebDAV/HLS/DASH/Torrent/Metalink beyond IDM |
| 7 | Proxy / SOCKS with auth (per-protocol) | **HAVE** | Static + **PAC scripts (v1.8.0)** + system detection; FTP proxy via CONNECT |
| 8 | Site logins vault (premium sites) | **HAVE** | Encrypted at rest; host + URL-prefix entries; 401/407 retry; **interactive login dialog (v1.8.0)** |
| 9 | Download quotas (FAP) | **HAVE** | `QuotaTracker` rolling windows, auto-pause + resume |
| 10 | Custom temporary directory | **HAVE** | `engine.temp_dir` |
| 11 | One-click browser takeover | **PARTIAL** | Interception exists but opt-in (§8) |
| 12 | Drag & drop link/file onto window | **HAVE** | Win32 WM_DROPFILES + GTK drag-dest → Add dialog |
| 13 | Per-download properties editor | **HAVE** | Queue, category, priority, connections, speed cap, start-at, post action, mirrors, Save As dir |
| 14 | Per-server connection-count exceptions | **HAVE** | `scheduler.per_server_max` + wildcard overrides |
| 15 | Download later / scheduled start | **HAVE** | Add-dialog start-at + properties editor |
| 16 | **Drag & drop files OUT of the app** | **HAVE** (v1.8.0, Windows) | OLE `DoDragDrop` + CF_HDROP on the "nova/dragout" channel — press the tile's category icon and move; Linux GTK drag-out not shipped |

### 2.2 Browser integration

| # | IDM capability | Nova v1.8.0 | Notes |
|---|---|---|---|
| 17 | Extensions for Chrome/Firefox/Edge/Opera (+Brave via Chromium) | **HAVE** | Chrome/Brave/Edge/Vivaldi/Opera (one Chromium MV3 package) + Firefox |
| 18 | Video download panel on web players | **HAVE** | Overlay + quality panel (better: verified sizes) |
| 19 | "Download with IDM" context menu | **HAVE** | Link + video + page-videos |
| 20 | "Download ALL links" from page | **HAVE** | Extension dialog with grouping + bulk add |
| 21 | Multi-select link panel | **HAVE** | Batch `add_downloads` |
| 22 | File-types takeover editor | **HAVE** | `intercept.file_types` |
| 23 | Site exceptions with wildcards | **HAVE** | `UrlPatterns.WildcardMatch` + allow/blocklists |
| 24 | Special keys to force/prevent interception | **HAVE** | alt/ctrl/shift **+ Insert (IDM's default) via key-state tracking (v1.8.0)** |
| 25 | URL clipboard monitoring | **HAVE** | 2 s watcher prefills the Add dialog (never silent) |
| 26 | Advanced integration into any HTTP/FTP app | **EXCLUDED** | Modern equivalent is the extension route (§8) |

### 2.3 Scheduler & queue

| # | IDM capability | Nova v1.8.0 | Notes |
|---|---|---|---|
| 27 | Scheduler (start at set time, one-time/recurring) | **HAVE** | Global windows + per-queue schedule windows |
| 28 | Multiple named queues | **HAVE** | Per-queue caps, retry defaults, post actions |
| 29 | Periodic synchronization queues | **HAVE** | `sync_rule` + `SyncService` (ETag/Last-Modified) |
| 30 | Hang up / shut down PC when done | **HAVE** | exit / shutdown / hibernate / sleep / logoff |
| 31 | "Download later" → queue | **HAVE** | Queue picker in add + properties |
| 32 | Speed limiter (on-the-fly global throttle) | **HAVE** | Toolbar quick toggle → live `engine.global_speed_cap` |
| 33 | Max simultaneous downloads per queue | **HAVE** | `max_concurrent` per queue |
| 34 | Move task between queues | **HAVE** | `queueName` mutable (properties editor) |
| 35 | **Failed file moves to end of queue; infinite 30 s retry when alone** | **HAVE** (v1.8.0) | Retryable failures of the lone remaining task in a queue retry every 30 s indefinitely (`engine.lone_infinite_retry`, default on, IDM behaviour); others proceed meanwhile via backoff scheduling |
| 36 | **Per-queue "Number of retries for each file"** | **HAVE** (v1.8.0) | `queue.max_retries.<name>` seeds new tasks; per-task value still wins |
| 37 | **"Open the following file when done" (per queue)** | **HAVE** (v1.8.0) | `queue.post_action.<name>` = `open:<path>` (documents, .exe, .bat — IDM's documented multi-program trick) or any power action; fires on the queue's active→idle transition |
| 38 | Queue file reorder | **HAVE** | Priority aging + `StartAtMs`; drag-order UI equivalent via priority (§8 note) |

### 2.4 UI / organization

| # | IDM capability | Nova v1.8.0 | Notes |
|---|---|---|---|
| 39 | Download categories (built-in + custom) | **HAVE** | Auto-assign + icons + custom patterns |
| 40 | Per-category save dirs + **"change folder on last selected"** | **HAVE** (v1.8.0) | `CategoryDirStore`; a custom Save As dir becomes the category default when the option is on |
| 41 | Sortable columns + find in list | **HAVE** | Search + sort menu + **shown-fields chooser (queue/category/date) (v1.8.0)** — card-UI equivalent of IDM's column list |
| 42 | Toolbar/skins customization | **EXCLUDED** | M3 design system is the deliberate replacement |
| 43 | Dark theme | **HAVE** | light/dark/system |
| 44 | Multilingual (40+ incl. Farsi) | **PARTIAL** | EN/FA by design (§8) |
| 45 | Sounds on events | **HAVE** (v1.8.0) | Per-event (complete / error / all-done) custom sound files + platform default cues |
| 46 | Progress dialog with speed graph + segments | **HAVE** | Details sheet: segment bars + speed painter; **progress-view mode: always / in-list / never (v1.8.0)** |
| 47 | Download-complete dialog | **HAVE** | Complete dialog + open-file action + sound |
| 48 | Tray icon / minimize to tray | **HAVE** (v1.8.0, Windows) | `Shell_NotifyIconW` + menu (Open/Exit) + close-to-tray; Linux tray remains a documented limitation |
| 49 | Progress mini-window (always-on-top) | **HAVE** | `--mini` window (GTK keep-above / Win32 TOPMOST) |
| 50 | **Automatic startup with the OS** | **HAVE** (v1.8.0) | `get/set_autostart` IPC: Windows HKCU Run key; Linux XDG autostart .desktop; Settings toggle |

### 2.5 Special tools & post-download

| # | IDM capability | Nova v1.8.0 | Notes |
|---|---|---|---|
| 51 | Site Grabber (depth, filters, projects, scheduling) | **HAVE** | Depth, include/exclude, types, offline rewrite, saved projects; **v1.8.0: "don't explore parent directories" + "explore all sites within the main domain" (subdomains) + separate levels for other sites + min/max file-size filters**; fixed the counters + folder-replication bugs |
| 52 | Batch add (multiple URLs, clipboard/text) | **HAVE** | Multi-line + `[01-99]` patterns + batch drop |
| 53 | ZIP preview + selective download | **HAVE** | Range-based central directory + selective entries |
| 54 | Command line (`/d /p /f /q /h /n /a /s`) | **HAVE** | `nova` CLI with idman-compatible switches + subcommands |
| 55 | Import/export download list | **HAVE** | JSON export/import + IDM list import |
| 56 | Quick update check | **HAVE** | Manual check + **weekly auto-check honouring `update.auto_check_hours` (v1.8.0)** |
| 57 | Automatic antivirus scan on completion | **HAVE** | Configurable scanner command (default Defender) |
| 58 | Open/run file after completion | **HAVE** | `open_file` / `open_folder` + queue post actions |
| 59 | "Ignore file modification time changes when resuming" | **HAVE** (v1.8.0) | `engine.resume_ignore_validator_change` — validator-only changes resume old bytes |
| 60 | "Get System" proxy detection button | **HAVE** (v1.8.0) | OS proxy detection incl. PAC URL pickup |
| 61 | PAC "automatic configuration script" | **HAVE** (v1.8.0) | Jint (MIT) evaluation, full PAC helper env, bypass list, per-URL caching, Settings test button |
| 62 | HTTP/2 downloading | **HAVE** (v1.8.0) | Offered on https via ALPN with 1.1 fallback (beyond IDM 6.42) |
| 63 | Dial-up / VPN phone-book control | **EXCLUDED** | Obsolete hardware class (§8) |

## 3. Completion history

- **v1.1.0 (P0 1–12)** and **v1.2.0 (P1 13–19 + P2)** — see git history.
- **v1.8.0 completion wave (2026-10-30)**: PAC + system proxy detection; HTTP/2; resume-ignore + multi-segment resume bug fix; category change-on-last-selected; interactive 401/407 login dialog + vault URL-prefix entries; per-queue retry defaults + lone-task infinite retry; per-queue open/run post actions; Grabber wizard step-3/4 options + two bug fixes; Insert special key; Windows tray + drag-out; per-event sounds; progress-view modes; shown-fields chooser; autostart; weekly auto-update; Save As in add dialog.

## 4. Excluded (legacy) — documented, intentional

- **MMS / RTSP protocols** — no modern servers; dead technology.
- **Dial-up hang-up / modem + VPN phone-book session control** — obsolete hardware class.
- **IEMonitor.exe / ActiveX click monitoring / "add any browser .exe"** — pre-extension-era technique; modern browsers block injection. Nova's equivalent is the standards-based MV3 extension + native messaging (covers every Chromium browser + Firefox).
- **AOL / MSN / Avant / Maxthon legacy browser list** — covered by the above.
- **Skins/toolbar customization** — M3 design system replaces it.

## 5. Deliberate divergences (keep, document loudly)

- **Interception opt-in, off by default** — security-first; IDM takes over by default.
- **DRM refusal, always** — legal red line; not a gap.
- **Persian-first RTL (EN/FA)** — 40-language breadth is rejected in favour of two fully-correct translations (RTL + Persian digits); more languages via community ARB contributions welcome.
- **Queue reorder via drag in a list** — our card UI reorders through priority + aging (anti-starvation), a scheduler-honest equivalent.

## 6. Where Nova already exceeds IDM

HTTP/2 with ALPN; HLS/DASH manifest handling with a **verified** quality picker; explicit DRM/live refusal policy; SQLite crash-safe authoritative state; MV3 extensions via native messaging; true RTL Persian with Persian digits; Material 3 design; security model (pipe token, path-traversal guards, cross-origin credential stripping); cross-platform engine; honest metadata; ffmpeg muxing with documented fallback; anti-starvation aging + battery policy; SFTP/WebDAV/Metalink/BitTorrent/RSS/yt-dlp support; PAC with a live test button; 320-test automated suite.

## 7. Verification

Acceptance: every matrix row above is **HAVE** (or **EXCLUDED** with a doc'd reason); new engine features land with tests in `tests/Nova.Engine.Tests` (v1.8.0 completion suite: `V180Tests.cs` + `V180CompletionTests.cs`); extension features get manual checklists in `docs/TESTING.md`; `docs/KNOWN-LIMITATIONS.md` shrinks accordingly each phase.

## 8. Known residuals (tracked in KNOWN-LIMITATIONS)

- Linux tray + Linux drag-out (Windows ships both; GTK runner work pending).
- Extension special keys cover modifiers + Insert (IDM allows arbitrary keys).
- PAC: no WPAD/DHCP auto-discovery (explicit URL only, by choice).
- Registrable-domain heuristic for subdomain grabbing is label-based (no Public Suffix List).
