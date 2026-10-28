# Nova Download Manager — Worklog (master index)

One file per round inside `worklog/`. Persian summary at the bottom of each round file.

| Round | Date | Focus | Result |
|---|---|---|---|
| 12 | 2026-10-29 | Windows-build research + Engine v0.1 vertical slice | Engine v0.1.0 built, self-tested (2× PASS), cross-compiled to win-x64, CI workflow added |

## Current status (end of round 12)

| Component | State |
|---|---|
| C# engine (`NovaEngine`) | **v0.1.0** — multi-segment Range, pause/resume, crash recovery, SQLite (WAL), versioned JSON IPC, CLI. Self-tests: multi-segment sha256 PASS, IPC pause/resume flow PASS |
| Flutter UI (`app/`) | scaffold only; full bilingual EN/FA (RTL) UI pending |
| Chrome/Brave extension | not started |
| Firefox extension | not started |
| Native Messaging host registration (Windows) | pending (engine IPC is ready for it) |
| IDM gap list (rounds 1–3: Site Grabber, FTP, SOCKS, scheduler, speed limiter, video detection, yt-dlp bridge, server profiling, mTLS, Metalink/PGP, …) | pending — engine architecture prepared for them |
