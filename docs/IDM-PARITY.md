# IDM Feature-Parity Gap Analysis

> Date: 2026-10-24 · Baseline audited: **Nova v1.0.1** (Linux-verified release)
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
- 43 automated engine tests + 8 Flutter tests green on this repo.

## 2. IDM feature matrix vs Nova

### 2.1 Download engine

| # | IDM capability | Nova v1.0.1 | Notes |
|---|---|---|---|
| 1 | Dynamic file segmentation (re-splits largest remaining segment in flight) | **PARTIAL** | Static upfront plan (`min(max_conn, size/512KB)`), no in-flight re-split |
| 2 | Connection reuse without re-connect/login stages | **HAVE** | `SocketsHttpHandler` connection pooling |
| 3 | Up to 32 connections per file | **PARTIAL** | Capped at 16 in UI; engine default 4 |
| 4 | Resume broken/interrupted downloads | **HAVE** | Byte-exact, crash-safe, re-probed |
| 5 | Error recovery & auto-retry | **HAVE** | Backoff + jitter, per-task budget |
| 6 | Protocols: HTTP, HTTPS, FTP, MMS | **PARTIAL** | HTTP/HTTPS only (MMS excluded as dead tech) |
| 7 | Proxy / SOCKS with auth (per-protocol) | **MISSING** | Zero proxy code in `src/` (grep-verified) |
| 8 | Site logins vault (stored credentials, premium sites) | **MISSING** | 401/403 surfaces as error; no prompt, no storage (KNOWN-LIMITATIONS #6) |
| 9 | Download quotas (MB per hour / per N hours, auto-resume later) | **MISSING** | Speed caps exist; no byte-quota windows |
| 10 | Custom temporary directory | **MISSING** | `.novadl` fixed beside final file |
| 11 | One-click browser takeover | **PARTIAL** | Interception exists but opt-in (see §8) |
| 12 | Drag & drop link/file onto window | **MISSING** | — |
| 13 | Per-download properties editor (URL, referer, UA, auth, path, speed limit, priority, start time) | **MISSING** | Add dialog covers URL/filename/connections only; no edit-after-add |
| 14 | Per-server connection-count exceptions | **MISSING** | — |
| 15 | Download later / scheduled single-task start | **PARTIAL** | Queues + windows exist; no per-task start time in Add dialog |

### 2.2 Browser integration

| # | IDM capability | Nova v1.0.1 | Notes |
|---|---|---|---|
| 16 | Extensions for Chrome/Firefox/Edge/Opera (+Brave via Chromium) | **HAVE** | Chrome/Brave + Firefox (MV3, native messaging) |
| 17 | Video download panel on web players | **HAVE** | Overlay + quality panel (better: verified sizes) |
| 18 | "Download with IDM" context menu | **HAVE** | Link + video + page-videos |
| 19 | "Download ALL links" from page (selection dialog) | **MISSING** | Only "page videos"; no generic link grabber dialog |
| 20 | Download panel for selected links (multi-select) | **MISSING** | — |
| 21 | File-types takeover editor | **MISSING** | No user-editable extension list |
| 22 | Site exceptions with wildcards (`*.tonec.com`) | **PARTIAL** | Allow/blocklist per site exists; wildcard matching + file-type scoping missing |
| 23 | Special keys to force/prevent interception | **MISSING** | — |
| 24 | URL clipboard monitoring (+ auto-add copied links) | **MISSING** | Copy-link exists in task menu only |
| 25 | Advanced integration into any HTTP/FTP app | **EXCLUDED** | Modern equivalent is the extension route (§8) |

### 2.3 Scheduler & queue

| # | IDM capability | Nova v1.0.1 | Notes |
|---|---|---|---|
| 26 | Scheduler (start at set time, one-time/recurring) | **PARTIAL** | Time-window rules exist; no start/stop scheduling of queues |
| 27 | Multiple named queues | **HAVE** | QueueName + per-queue caps |
| 28 | Periodic synchronization queues (re-download) | **MISSING** | — |
| 29 | Hang up / shut down PC when done | **MISSING** | — |
| 30 | "Download later" → queue | **PARTIAL** | Queue assignment not exposed in Add dialog |
| 31 | Speed limiter (on-the-fly global throttle) | **PARTIAL** | Engine caps exist; no quick toggle UI |
| 32 | Max simultaneous downloads per queue | **HAVE** | `max_concurrent` per queue |
| 33 | Move task between queues | **HAVE** | `queueName` mutable (verify UI action exists) |

### 2.4 UI / organization

| # | IDM capability | Nova v1.0.1 | Notes |
|---|---|---|---|
| 34 | Download categories (built-in + custom) | **PARTIAL** | Auto-assign + icons + filter; no custom categories |
| 35 | Per-category save dirs + "change on last selected" | **MISSING** | — |
| 36 | Sortable columns + find in list | **PARTIAL** | Search by name/URL; no column sorting |
| 37 | Toolbar/skins customization | **EXCLUDED** | M3 design system is the deliberate replacement |
| 38 | Dark theme | **HAVE** | light/dark/system |
| 39 | Multilingual (40+ incl. Farsi) | **PARTIAL** | EN/FA by design; more languages later |
| 40 | Sounds on events | **MISSING** | — |
| 41 | Progress dialog with speed graph + segment view | **PARTIAL** | Details sheet has counts; no graph/segment bars |
| 42 | Download-complete dialog (open/run/etc.) | **PARTIAL** | SnackBar + task actions; no complete dialog |
| 43 | Tray icon / minimize to tray | **PARTIAL** | Setting exists; Linux tray not shipped (KNOWN-LIMITATIONS #8) |
| 44 | Progress mini-window (always-on-top) | **MISSING** | — |

### 2.5 Special tools & post-download

| # | IDM capability | Nova v1.0.1 | Notes |
|---|---|---|---|
| 45 | Site Grabber (spider: depth, filters, projects, scheduling, folder replication) | **MISSING** | Largest single gap |
| 46 | Batch add (multiple URLs, from clipboard/text) | **MISSING** | Single-URL add dialog |
| 47 | ZIP preview + selective download | **MISSING** | — |
| 48 | Command line (`/d /p /f /q /h /n /a /s`) | **MISSING** | Only dev tools (ipc_probe) |
| 49 | Import/export download list | **MISSING** | — |
| 50 | Quick update check | **MISSING** | — |
| 51 | Automatic antivirus scan on completion | **MISSING** | — |
| 52 | Open/run file after completion | **HAVE** | `open_file` / `open_folder` |

## 3. Gaps — P0 (v1.1.0 "IDM Parity Core")

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

## 4. Gaps — P1 (v1.2.0 "IDM Parity Plus")

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
