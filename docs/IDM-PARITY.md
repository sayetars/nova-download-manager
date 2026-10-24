# IDM Feature-Parity Gap Analysis

> Date: 2026-10-24 · Baseline audited: **Nova v1.0.1** (Linux-verified release) — **updated for v1.1.0 (P0 1–12 + P1 13–19) and v1.2.0 (P2 complete: per-server caps #14, special keys #23, queue scheduling #26, sync queues #28, sortable columns #36, mini-window #44). Remaining PARTIAL rows are deliberate design trade-offs (§8) or platform-scoped.**
> Purpose: complete, verified inventory of what Internet Download Manager (IDM) offers vs. what Nova has today, and the roadmap to full parity. This document is the working backlog for the parity phases.

Research sources: official `internetdownloadmanager.com` pages (features2.html, support/options.html, support/idm-grabber.html, support/command_line.html, video FAQ), askvg.com IDM panel guide, dealarious.com "10+ bonus features" guide, softonic/sourceforge reviews — cross-checked against a direct code audit of this repository (engine sources, Flutter pages, extension sources, docs/, STATE.md).

Status legend: **HAVE** (implemented & verified) · **PARTIAL** (exists but incomplete vs. IDM) · **MISSING** (not present) · **EXCLUDED** (deliberately not implemented — see §8).

---

## 1. Where Nova stands today (verified in code, not claimed)

- Real multipart engine: range-probed plans, parallel segment workers, shared-filestream writes at absolute offsets, crash-safe `.novadl` resume (ETag/Last-Modified validated), retry with exponential backoff + `Retry-After`, optional SHA-256 verification, disk-space gates, unique final names, path-traversal-safe names.
- Scheduler: global/per-queue concurrency caps, time-window rules (overnight supported) with per-window speed caps, battery policy, anti-starvation priority aging.
- Rate limiting: token bucket global + per-task + per-queue; honest ETA (`-1` when unknown).
- Media: HLS/DASH parsing, variant/representation selection, DRM + live-stream refusal, segment progress, ffmpeg mux when available (documented fallback).
- Browser integration: on-player overlay + verified quality panel, link/video/page-videos context menus, optional interception (off by default), allow/blocklists, auto-hand-off toggle.
- UI: EN/FA with true RTL + Persian digits, light/dark/system themes, search, state filters, category icons with auto-assign (`Disk.GuessCategory` from filename + content type), add-URL dialog (URL, filename, connections 1–16), task details sheet, history with retention, open file/folder.
- Persistence: SQLite WAL, migrations v1–v4, engine as single source of truth.
- 43 automated engine tests at v1.0.1 → **81/81 engine tests + 8/8 Flutter tests green at v1.1.0** on this repo.

## 2. IDM feature matrix vs Nova

### 2.1 Download engine

| # | IDM capability | Nova v1.0.1 | Notes |
|---|---|---|---|
| 1 | Dynamic file segmentation (re-splits largest remaining segment in flight) | **HAVE** (v1.1.0) | `TryDynamicSplit` in TaskRunner |
| 2 | Connection reuse without re-connect/login stages | **HAVE** | `SocketsHttpHandler` connection pooling |
| 3 | Up to 32 connections per file | **HAVE** (v1.1.0) | Engine clamps 1–32; UI chips 1/2/4/8/16/24/32 in Add + Properties dialogs |
| 4 | Resume broken/interrupted downloads | **HAVE** | Byte-exact, crash-safe, re-probed |
| 5 | Error recovery & auto-retry | **HAVE** | Backoff + jitter, per-task budget |
| 6 | Protocols: HTTP, HTTPS, FTP, MMS | **PARTIAL** (v1.1.0: FTP/FTPS real) | HTTP/HTTPS + FTP/FTPS (FluentFTP, resume via REST, FTPS explicit TLS, ftp_list browser); MMS excluded as dead tech |
| 7 | Proxy / SOCKS with auth (per-protocol) | **HAVE** (v1.1.0) | `ProxyConfig` (http/https/socks4/4a/5, auth, bypass) applied to engine HttpClient + proxy-aware factory for FTP/grabber/ZIP; settings UI |
| 8 | Site logins vault (stored credentials, premium sites) | **HAVE** (v1.1.0) | `LoginVault` encrypted-at-rest, 401 retry with vault creds, FTP creds; settings UI editor (Basic/Digest/NTLM/Negotiate) |
| 9 | Download quotas (MB per hour / per N hours, auto-resume later) | **HAVE** (v1.1.0) | `QuotaTracker`: rolling windows, auto-pause + auto-resume, restart-safe, `quota_state` IPC, UI banner + settings |
| 10 | Custom temporary directory | **HAVE** (v1.1.0) | `engine.temp_dir` setting (settings UI; default: beside final file) |
| 11 | One-click browser takeover | **PARTIAL** | Interception exists but opt-in (see §8) |
| 12 | Drag & drop link/file onto window | **HAVE** (v1.1.0) | Win32 WM_DROPFILES + GTK drag-dest → method channel `nova/dnd` → Add dialog prefill (single or batch) |
| 13 | Per-download properties editor (URL, referer, UA, auth, path, speed limit, priority, start time) | **HAVE** (v1.1.0) | Task Properties dialog: queue, category, priority, connections, speed cap, start-at, post action, mirrors (`update_task` IPC) |
| 14 | Per-server connection-count exceptions | **HAVE** (v1.2.0) | `scheduler.per_server_max` default + `scheduler.server_exceptions` JSON overrides (wildcards), enforced in scheduler admission (`PerServerPolicy`, tested) |
| 15 | Download later / scheduled single-task start | **HAVE** (v1.1.0) | Add dialog start-at + task properties editor start-at; scheduler honors `StartAtMs` (tested) |

### 2.2 Browser integration

| # | IDM capability | Nova v1.0.1 | Notes |
|---|---|---|---|
| 16 | Extensions for Chrome/Firefox/Edge/Opera (+Brave via Chromium) | **HAVE** | Chrome/Brave + Firefox (MV3, native messaging) |
| 17 | Video download panel on web players | **HAVE** | Overlay + quality panel (better: verified sizes) |
| 18 | "Download with IDM" context menu | **HAVE** | Link + video + page-videos |
| 19 | "Download ALL links" from page (selection dialog) | **HAVE** (v1.1.0) | Extension context menu "all links" + `collectLinks()` grouping (documents/media/archives/other, 500 cap) → bulk add |
| 20 | Download panel for selected links (multi-select) | **HAVE** (v1.1.0) | Multi-select link dialog in extension → batch `add_downloads` |
| 21 | File-types takeover editor | **HAVE** (v1.1.0) | `intercept.file_types` setting + settings UI field (drives extension interception) |
| 22 | Site exceptions with wildcards (`*.tonec.com`) | **HAVE** (v1.1.0) | `UrlPatterns.WildcardMatch` (tested) + allow/blocklist + file-type scoping via `intercept.file_types` |
| 23 | Special keys to force/prevent interception | **HAVE** (v1.2.0) | Extension options: interceptKey (alt/ctrl/shift/none) forces "download with Nova" on click; preventKey suppresses overlay + interception while held (both browsers, options page included) |
| 24 | URL clipboard monitoring (+ auto-add copied links) | **HAVE** (v1.1.0) | 2 s watcher prefills the Add dialog (never silent) + ignore-list of seen clips |
| 25 | Advanced integration into any HTTP/FTP app | **EXCLUDED** | Modern equivalent is the extension route (§8) |

### 2.3 Scheduler & queue

| # | IDM capability | Nova v1.0.1 | Notes |
|---|---|---|---|
| 26 | Scheduler (start at set time, one-time/recurring) | **HAVE** (v1.2.0) | Global time-window rules + per-queue start/stop schedule windows ("run"/"pause" entries, overnight windows, `QueueSchedule`, tested) |
| 27 | Multiple named queues | **HAVE** | QueueName + per-queue caps |
| 28 | Periodic synchronization queues (re-download) | **HAVE** (v1.2.0) | `sync_rule` table + `SyncService`: HEAD (ranged-GET fallback) ETag/Last-Modified comparison, interval gating, run-now IPC, dedicated UI page (tested incl. interval/disabled/405 paths) |
| 29 | Hang up / shut down PC when done | **HAVE** (v1.1.0) | `PostActionRunner`: exit / shutdown / hibernate / sleep / logoff with cancel window; per-task + engine-wide settings |
| 30 | "Download later" → queue | **HAVE** (v1.1.0) | Queue picker in task properties editor; queue assignment on add via IPC `queue_name` |
| 31 | Speed limiter (on-the-fly global throttle) | **HAVE** (v1.1.0) | Toolbar quick toggle (presets → `engine.global_speed_cap`, applied live) |
| 32 | Max simultaneous downloads per queue | **HAVE** | `max_concurrent` per queue |
| 33 | Move task between queues | **HAVE** | `queueName` mutable (verify UI action exists) |

### 2.4 UI / organization

| # | IDM capability | Nova v1.0.1 | Notes |
|---|---|---|---|
| 34 | Download categories (built-in + custom) | **HAVE** (v1.1.0) | Auto-assign + icons + filter + custom categories with patterns (settings editor, `category_def` table) |
| 35 | Per-category save dirs + "change on last selected" | **HAVE** (v1.1.0) | `destination_dir` per category def (change-on-last-selected remains a P2 nicety) |
| 36 | Sortable columns + find in list | **HAVE** (v1.2.0) | Search by name/URL + sort menu (date added/name/size/progress/state/speed, asc/desc toggle) in the downloads list |
| 37 | Toolbar/skins customization | **EXCLUDED** | M3 design system is the deliberate replacement |
| 38 | Dark theme | **HAVE** | light/dark/system |
| 39 | Multilingual (40+ incl. Farsi) | **PARTIAL** | EN/FA by design; more languages later |
| 40 | Sounds on events | **HAVE** (v1.1.0) | Completion sound + notification dialog |
| 41 | Progress dialog with speed graph + segment view | **HAVE** (v1.1.0) | Details sheet: stacked per-segment bars (`task_segments` IPC, 500 ms live publish) + speed graph painter |
| 42 | Download-complete dialog (open/run/etc.) | **HAVE** (v1.1.0) | Complete dialog with open-file action + sound |
| 43 | Tray icon / minimize to tray | **PARTIAL** | Setting exists; Linux tray not shipped (KNOWN-LIMITATIONS #8) |
| 44 | Progress mini-window (always-on-top) | **HAVE** (v1.2.0) | Second `--mini` instance: compact always-on-top window (GTK keep-above / Win32 HWND_TOPMOST), global speed + per-task rows + pause/resume all; launched from the toolbar (VLM-verified screenshot) |

### 2.5 Special tools & post-download

| # | IDM capability | Nova v1.0.1 | Notes |
|---|---|---|---|
| 45 | Site Grabber (spider: depth, filters, projects, scheduling, folder replication) | **HAVE** (v1.1.0) | `Nova.Grabber`: recursive HTML spider (depth, include/exclude wildcards, image/css/script/video, offline rewrite, folder structure), saved projects, Grabber UI page; parity E2E verified |
| 46 | Batch add (multiple URLs, from clipboard/text) | **HAVE** (v1.1.0) | Multi-line Add dialog + `[01-99]` pattern expansion + batch drop + `add_downloads` IPC |
| 47 | ZIP preview + selective download | **HAVE** (v1.1.0) | Range-based remote central-directory read + selective entry download (details sheet); parity E2E verified |
| 48 | Command line (`/d /p /f /q /h /n /a /s`) | **HAVE** (v1.1.0) | `nova` CLI with idman-compatible switches + subcommands (add/list/pause/resume/…); shipped in package `cli/` |
| 49 | Import/export download list | **HAVE** (v1.1.0) | `export_tasks` / `import_tasks` IPC + settings buttons (JSON) |
| 50 | Quick update check | **HAVE** (v1.1.0) | `check_update` IPC + settings action (endpoint via `update.url`) |
| 51 | Automatic antivirus scan on completion | **HAVE** (v1.1.0) | `Antivirus` command hook (default MpCmdRun pattern), verdict surfaced on task; settings section |
| 52 | Open/run file after completion | **HAVE** | `open_file` / `open_folder` |

## 3. Gaps — P0 (v1.1.0 "IDM Parity Core") — ✅ COMPLETE (2026-10-24)

1. **Proxy & SOCKS support** — engine: proxy config (HTTP/HTTPS/SOCKS4/5, auth, per-protocol), settings UI, per-task override; `proxy.*` settings keys; test with local proxy.
2. **Site logins vault + auth flow** — encrypted credential store (DPAPI on Windows, libsecret on Linux), 401/407 retry with prompt, realm matching, per-task credentials, premium-site support.
3. **Dynamic segmentation** — in-flight re-split: when a worker finishes, split the largest remaining segment; connection count ramps up to configured max without re-probe.
4. **Download-all-links grabber** — extension collects page links (a[href] + resources), filter dialog (by type/domain), bulk `add_tasks`; plus multi-URL add dialog (textarea, dedupe, per-line validation).
5. **Clipboard URL monitoring** — opt-in watcher (desktop UI), auto-add dialog on copied http(s) links, ignore-list.
6. **Speed limiter toggle + quotas** — global cap quick control in toolbar; byte-quota windows (MB per hour / per N hours, auto-pause + resume later).
7. **Categories upgrade** — custom categories (name, patterns, save dir), per-category default dir, "change folder on last selected" behavior, category editor page.
8. **Post-completion actions** — shutdown / sleep / hibernate / exit engine / close app when queue completes (with grace + cancel window).
9. **File-types takeover editor + per-server connection caps** — editable extension list driving interception; per-server max-connection exceptions table.
10. **Task properties editor** — edit URL/referer/UA/auth/dir/queue/priority/speed cap after add; "Download later at HH:MM".
11. **Connections up to 32** — extend UI chips (1/2/4/8/16/24/32) + engine guardrails.
12. **Drag & drop** — drop URL/text/file-list onto main window → Add dialog prefilled.

## 4. Gaps — P1 (v1.2.0 "IDM Parity Plus") — ✅ items 13–19 COMPLETE in v1.1.0 (engine + E2E verified); UI polish continues

13. **FTP + FTPS** — protocol handler behind the same segment engine (active/passive, ASCII/binary, list parsing, resume via REST).
14. **Site Grabber module** — new `Nova.Grabber`: HTML spider with configurable depth, domain filters, file-type filters, "don't explore parents", folder-structure replication, saved projects (SQLite), scheduled re-sync, checkbox selection UI, add-checked-to-main-list.
15. **Antivirus integration** — configurable scanner command (default Windows Defender `MpCmdRun.exe`), run on completion, verdict surfaced on task.
16. **ZIP preview + selective download** — Range-based fetch of central directory, list contents, selective entry download (store/deflate), integrate into task details.
17. **Command-line interface** — `nova.exe` CLI: `add`, `pause`, `resume`, `list`, `start-queue`; idman-compatible `/d /p /f /n /a /q /s` wrappers where mapping is honest.
18. **Import/export** — JSON export/import of task list (+ .txt/clipboard URL import).
19. **Scheduler synchronization mode** — periodic re-download of a URL/project (conditional on ETag/Last-Modified change).
20. **Sounds + notifications** — per-event sounds; native notifications on completion.
21. **Sortable columns + progress mini-window** — column sorting; floating compact progress window with per-segment bars + speed graph.
22. **Special keys** — extension modifiers (e.g. hold Alt to force/prevent takeover).
23. **Mirror URLs** — fallback URL list per task, tried in order on permanent failure.
24. **Temp directory setting** — configurable temp root (default: beside final).
25. **Quick update check** — opt-in version check against a version endpoint.

P2 (v1.3.0 polish): complete-dialog customization, additional languages, tray parity on Windows, IDM import wizard (best-effort from exported lists).

## 5. Excluded (legacy) — documented, intentional

- **MMS / RTSP protocols** — no modern servers; dead technology.
- **Dial-up hang-up / modem session control** — obsolete hardware class.
- **IEMonitor.exe / ActiveX click monitoring** — pre-extension-era technique; modern browsers block injection. Nova's equivalent is the standards-based MV3 extension + native messaging.
- **"Add any browser .exe" integration** — same reason as above (Chromium/Gecko engines cover Chrome/Brave/Edge/Vivaldi/Opera via our Chromium package).
- **AOL / MSN / Avant / Maxthon legacy browser list** — covered by the above.

## 6. Deliberate divergences (keep, document loudly)

- **Interception opt-in, off by default** — security-first; IDM takes over by default. Expose a prominent one-click "Take over my browser downloads" setting so parity is one toggle away.
- **DRM refusal, always** — legal red line; not a gap.
- **Persian-first RTL** — our advantage, not a gap.

## 7. Where Nova already exceeds IDM

HLS/DASH manifest handling with a **verified** quality picker (IDM cannot parse manifests — it only sees direct media URLs); explicit DRM/live refusal policy; SQLite crash-safe authoritative state (vs. registry/`.idm` files); MV3 extensions via native messaging for Chrome/Brave/Firefox; true RTL Persian with Persian digits; Material 3 light/dark design; security model (pipe token, path-traversal guards, cross-origin credential stripping on redirects); cross-platform engine (Linux-verified today, Windows target); honest metadata (no invented sizes/quality); ffmpeg muxing with documented fallback; anti-starvation aging + battery policy; 43-test automated suite.

## 8. Roadmap & verification

| Phase | Version | Scope | Components touched |
|---|---|---|---|
| 1 — IDM Parity Core | v1.1.0 | P0 items 1–12 | Engine, Scheduler, Persistence (migrations v5+), Flutter settings/downloads, extension, IPC additions (`set_settings` keys, `add_tasks`, `update_task`, `quota.*` events) |
| 2 — IDM Parity Plus | v1.2.0 | P1 items 13–19 | New `Nova.Grabber` + `Nova.Ftp`, CLI project, persistence (grabber projects), extension bulk-add |
| 3 — Polish | v1.3.0 | P1 20–25 + P2 | Flutter UI, notifications, tray (Windows runner) |
| 4 — Windows Release | v2.0.0 | Windows build, installer, store publishing, full manual test matrix | Windows host required |

Acceptance: every matrix row above moves to **HAVE** (or stays **EXCLUDED** with a doc'd reason); new engine features land with tests in `tests/Nova.Engine.Tests`; extension features get manual checklists in `docs/TESTING.md`; `docs/KNOWN-LIMITATIONS.md` shrinks accordingly each phase.
