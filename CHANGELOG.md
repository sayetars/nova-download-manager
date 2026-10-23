# Changelog

All notable changes to Nova Download Manager. Format follows Keep a Changelog; versioning is semantic (product pre-1.0 parity is intentional).

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
