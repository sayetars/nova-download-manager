# Changelog

All notable changes to Nova Download Manager. Format follows Keep a Changelog; versioning is semantic (product pre-1.0 parity is intentional).

## [1.2.0] — 2026-10-24

**IDM Parity Polish (P2 complete)** — every remaining P2 gap from `docs/IDM-PARITY.md` is implemented. C# suite grew 81 → 108 tests; all Linux verification green (smoke, parity E2E 12/12, mini-window smoke + VLM QA, UI VLM QA, tarball clean-env byte-exact).

### Added — engine
- **Per-server connection caps** (IDM #14): default `scheduler.per_server_max` plus per-host exception overrides with wildcards (`scheduler.server_exceptions`), enforced in scheduler admission via `ActivePerHost()` + `PerServerPolicy` (tested; case-insensitive JSON, malformed-input safe).
- **Per-queue start/stop scheduling** (IDM #26): queue-level schedule windows — "run" windows (queue may only run inside) and "pause" windows (never run inside), day-of-week + overnight support; persisted on `download_queue.schedule_json`; `save_queue` accepts `schedule_json` (tested incl. pause-beats-run, disabled rules).
- **Periodic synchronization queues** (IDM #28): `sync_rule`/`sync_state` tables (migration v6), `SyncService` with 30 s due-check timer, HEAD probes (ranged-GET fallback on 405/501), ETag/Last-Modified change detection, interval gating, `list_sync_rules` / `save_sync_rule` / `delete_sync_rule` / `run_sync` IPC (tested: first-run adds, unchanged skip, changed re-add, 405 fallback, interval + disabled gating).
- **IDM import** (best-effort): `import_idm` IPC extracts URLs from .ief/.ef2-style exports or plain lists (regex, dedupe, punctuation-trim) and batch-adds as paused (tested).
- `per_server_overview` IPC (active per host + configured caps).

### Added — UI (Flutter)
- **Progress mini-window** (IDM #44): second `--mini` process — compact 340×460 always-on-top window (GTK `gtk_window_set_keep_above` / Win32 `HWND_TOPMOST` + `WS_EX_TOOLWINDOW`), global speed header, per-task rows with progress, pause/resume all; `nova/window` method channel; launched from the toolbar.
- **Sort menu** in the downloads list (IDM #36): date added / name / size / progress / state / speed with direction toggle.
- **Sync rules page** (new sidebar section): list / add / edit / enable / delete / run-now with result toast.
- **Queue schedule editor**: per-queue run/pause windows with day chips, shown as chips on queue cards.
- **Settings**: per-server caps section (default + exceptions editor), complete-dialog customization (`ui.complete_dialog` = always / media / never, honored by the shell dialog).
- **IDM import button** (settings → import/export): paste-dialog → `import_idm`.

### Changed — extensions (both browsers)
- **Special keys** (IDM #23): configurable force-download key (alt/ctrl/shift/none — default alt) and prevent key (suppresses overlay + interception while held); new **options page** (`options_ui`) with all settings incl. quality preference and allow/block lists; manifests bumped 1.2.0.

### Changed — platform
- SQLite migration v6 (`sync_rule`, `sync_state`, `download_queue.schedule_json`).
- Engine + IPC version strings → 1.2.0; `EngineStatusResponse.EngineVersion` 1.2.0.

### Verified
- `dotnet test` 108/108 (×2 consecutive runs) · `flutter analyze` 0 errors · `flutter test` 8/8 · smoke_test PASS · parity E2E 12/12 · mini-window smoke + VLM QA PASS · main UI VLM QA PASS · tarball clean-env byte-exact PASS.
- Windows runner mini-window code follows the v1.1.0 DnD pattern (structurally complete, compile-verification pending a Windows host — KNOWN-LIMITATIONS).

## [1.1.0] — 2026-10-24

**IDM Parity Core + Plus** — the P0 gap list from `docs/IDM-PARITY.md` is now implemented, with most P1 items included. C# suite grew 43 → 81 tests; parity E2E smoke 12/12 PASS on the shipped bits.

### Added — engine
- **Byte-quota windows** (IDM parity): rolling caps ("500MB per 60 min") with automatic pause of all running tasks and automatic resume when the window frees; restart-safe via `byte_quota_log` checkpoints; `quota_state` IPC + `engine_status.quota` payload; settings `quota.enabled` / `quota.max_bytes` / `quota.window_minutes` (accepts MB/GB/K/M/G suffixes). 10 new unit tests.
- **Live segment view**: per-segment progress published in-flight (500 ms throttle) and served over `task_segments` IPC; powers the new details-sheet segment bars.
- **FTP progress fix**: FluentFTP cumulative offsets converted to true byte deltas — the speed meter and quota tracker now see real FTP bytes (previously rate-only).
- Vault-auth retry, mirror failover, antivirus hook, dynamic segmentation, 32-connection cap, start-at times and per-task post actions — verified by the new ParityTests suite (vault, mirrors, schedule fields, FTP URLs).

### Added — UI (Flutter)
- **Task properties editor** (edit task → queue, category, priority, connections 1–32, speed cap, start-at HH:mm, post-completion action, mirrors) from the context menu and details sheet; engine enforces not-running.
- **Site logins vault editor** (settings): list / add / edit / delete stored credentials (host, user, password, Basic/Digest/NTLM/Negotiate); passwords never travel back to the UI.
- **Categories editor** (settings): custom categories with patterns and per-category destination directory.
- **Speed-limiter quick toggle** in the toolbar (unlimited / 128 KB/s … 10 MB/s presets) wired to `engine.global_speed_cap`.
- **Details sheet upgrade**: stacked per-segment progress bars (each slice sized by byte span) + live speed graph.
- **Drag & drop**: dropped links/text prefill the Add dialog (single URL) or the batch add dialog (multiple); implemented natively — Win32 `WM_DROPFILES` in the Windows runner, GTK drag-dest in the Linux runner, forwarded over method channel `nova/dnd`.
- **Byte-quota section** in settings + warning banner while the cap has paused downloads.
- **Check for updates** action (uses `update.url` + `check_update` IPC).

### Added — tooling / release
- `scripts/linux/package_release.sh`: assembles the versioned Linux tarball (bundle + self-contained engine + CLI + extensions + docs + SHA256SUMS).
- RUN-LINUX.sh hardened: sets `DOTNET_BUNDLE_EXTRACT_BASE_DIR` for single-file extraction; cleanup list covers `Nova.Grabber.dll`.
- Parity E2E smoke (`parity_smoke_test.sh`) 12/12 PASS: engine status via CLI, byte-exact 8 MiB download, `[01-99]` pattern expansion, remote ZIP preview via Range, Site Grabber (page + 3 resources, offline-rewritten), IDM `/a` switch, global speed cap, task export.
- Shipped-tarball clean-environment test PASS (self-contained engine boots with no .NET, raw-probe download byte-exact).
- UI smoke under Xvfb PASS; VLM visual QA PASS (window, connection, completed task, no glitches).

### Verified
- dotnet test **81/81** (was 69 pre-session, 43 at v1.0.1) · flutter test 8/8 · flutter analyze 0 errors.
- Built and packaged: `nova-dm-1.1.0-linux-x64.tar.gz` (41 MB, gzip; self-contained engine 70 MB single-file).

## [Unreleased]

### Fixed — test infrastructure (no user-facing behavior change; v1.0.1 artifacts remain valid)
- Deterministic parallel test isolation: `IpcServer`/`IpcClient` accept an optional explicit pipe name (default unchanged). Root cause of intermittent `BrowserPathTests` failures was two concurrently-running test classes binding the same well-known pipe `nova-engine-v1`; browser-path fixtures now run their engine on an isolated `NOVA_PIPE_PATH` socket, and IPC tests use unique per-class names.
- Browser-path test fixture now polls engine readiness (token + socket file, 30 s) instead of a fixed 1500 ms sleep, and reports engine stderr on boot failure.

### Added — verification tooling
- `scripts/linux/ipc_probe.py`: dependency-free (stdlib) nipc/1 client — handshake → add_download → poll → byte-exact verification. Proves the shipped package works without any dev tooling installed.
- `scripts/linux/tarball_cleanenv_test.sh`: extracts the published tarball, applies the RUN-LINUX.sh self-contained swap, boots the engine under `env -i`, and drives the raw probe end-to-end (PASS on v1.0.1).

## [1.0.1] — 2026-10-23

### Added — Linux support
- First Linux x64 release build: `flutter build linux --release` + `dotnet publish -r linux-x64` (framework-dependent **and** self-contained engine variants in one package).
- `RUN-LINUX.sh` smart launcher: detects .NET 8 runtime; when absent, automatically swaps in the self-contained engine (verified boot with zero dotnet present: socket, IPC token and SQLite database created).
- `register-browser-host.sh`: registers the native-messaging host for Chrome/Chromium/Brave/Vivaldi/Edge (`~/.config/<browser>/NativeMessagingHosts/`) and Firefox (`~/.mozilla/native-messaging-hosts/`).
- Bilingual (EN/FA) Linux quickstart guide; docs set shipped inside the package.

### Added — Persistence infrastructure (per user mandate)
- UNIVERSAL-RULES.md adopted as permanent agent constitution; project memory files (STATE, RESUME-POINT, VERSIONS, DECISIONS, RECOVERY, RULES-READ-LEDGER, PROJECT-RULES) established.
- Dropbox persistence layer (`tools/dropbox/`): source checkpoints with SHA-256 manifests, state-doc mirroring, safe version-gated restore; build artifacts published to `/Nova-Download-Manager/builds/`.
- Toolchain rebuild procedure for this sandbox documented in RECOVERY.md (user-space, no root: .NET 8 SDK, Flutter 3.47.7, clang 19, GTK3 dev, cmake/ninja, GL/EGL software stack).

### Verified on Linux (Debian 13, x86-64)
- 43/43 C# engine tests, 8/8 Flutter tests, 0 analyze errors (parity with 1.0.0).
- End-to-end smoke on the release bundle: real IPC handshake → download → byte check; GUI under Xvfb auto-spawns engine, shows "Engine connected" and "Completed: 1" after a live download (visual QA via VLM confirmed).

## [1.0.0] — 2026-10-23

### Added — Engine
- nipc/1 IPC protocol: 4-byte LE length-prefixed JSON, versioned handshake with per-user token, Windows pipe ACL, 4 MiB frame cap, ping/bye.
- SQLite persistence (WAL): tasks, segments, queues, scheduler rules, settings, events; migrations v1–v4.
- Multi-part HTTP(S) engine: range-aware planning, concurrent segment workers, single-handle offset writes, crash-safe `.novadl` temp, unique finalize naming, fsync.
- Resume & recovery: ETag/Last-Modified validators, restart-on-change, crash/restart recovery pass, interrupted requeue.
- Retries: exponential backoff + jitter, Retry-After, permanent-vs-transient classification, per-task budget.
- Integrity: expected-SHA-256 verification with pass/fail states.
- Scheduler: concurrency caps (global/per-queue), token-bucket rate limits (global/per-task), time windows incl. overnight, battery policy hook, anti-starvation aging.
- Media: HLS master/media parsing with variant selection; DASH MPD parsing (template, timeline, list); DRM/live refusal; segment assembly; ffmpeg-when-available muxing.
- Safe redirects (auth strip cross-origin), disk-space gate, structured error catalog.
- EngineHost process: single instance, IPC server, auth token, scheduler wiring.
- BrowserHost: native messaging stdio bridge, origin allow-list, engine auto-start (user level), stdout purity.

### Added — Flutter app
- Full desktop UI: shell (collapsible rail + toolbar + search + connection chip), downloads with filters/context menus/details/add-dialog, overview, queues, scheduler, history, browser integration, diagnostics, settings.
- Riverpod 2 state management; custom M3 design system (light/dark/system).
- nipc/1 client in Dart with auto-reconnect; Windows named-pipe bridge plugin (C++ runner).
- Complete English + Persian localization with RTL and Persian digits.

### Added — Extensions
- Chromium (MV3) and Firefox packages from a shared TS core.
- On-player overlay button + verified quality panel (shadow DOM, fullscreen/RTL/zoom aware).
- Background media observation (webRequest), context menus, optional download interception, allow/blocklists.

### Added — Infrastructure
- Inno Setup installer script + release/dev build scripts.
- 43 automated tests (engine matrix, media, IPC, persistence, scheduler, end-to-end browser path with real processes) — all passing.
- Documentation set (architecture, protocol, engine, video, browser, security, testing, build, limitations).

### Notes
- See docs/KNOWN-LIMITATIONS.md for environment-bound verification status.
