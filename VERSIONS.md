# VERSIONS — Nova Download Manager

| Version | Date | Status | Notes |
|---------|------|--------|-------|
| v1.0.0 | 2026-10-23 | RELEASED (Windows path) | Engine + Flutter UI + extensions + installer + docs. 43/43 C# tests ×2, 8/8 Flutter tests, 0 analyze errors. Commits up to b06e865 + checkpoint c0dfa89. |
| v1.0.1 | 2026-10-23 | RELEASED (Linux x64) | First Linux build. 43/43 + 8/8 + 0 errors re-verified on Linux; release-bundle smoke suite PASS; self-contained no-runtime boot PASS; tar.gz + sha256 on Dropbox (round-trip verified). |
| v1.1.0 | 2026-10-24 | RELEASED (Linux x64) | IDM Parity Core + most of Plus: byte quotas, task properties editor, logins vault UI, categories editor, speed-limiter toggle, segment bars + speed graph, drag & drop (Win32+GTK), check-update, FTP byte-delta fix, live segment IPC. 81/81 C# + 8/8 Flutter + 0 analyze errors; parity E2E 12/12; clean-env tarball PASS; VLM QA PASS. |
| v1.2.0 | 2026-10-24 | RELEASED (Linux x64) | IDM Parity Polish — P2 complete: per-server caps, special keys (extension options page), per-queue schedules, periodic sync queues (ETag/Last-Modified), sortable downloads list, always-on-top mini-window (--mini), complete-dialog customization, IDM import. 108/108 C# (+27) + 8/8 Flutter + 0 analyze; parity E2E 12/12; mini smoke + VLM PASS; UI VLM PASS; clean-env tarball PASS. |
| v1.3.0 | 2026-10-24 | RELEASED (Linux x64) | Beyond IDM — Market Parity (docs/FEATURE-GAP-2026.md): Metalink v4/v3 + RFC 6249 + sidecars + piece repair, multi-source parallel, cookies/headers, SFTP, WebDAV, RSS/Atom feeds, auto-extract, ffmpeg convert, filename tokens, sequential, JSON-RPC remote (HTTP+WS), Feeds page + Advanced add-dialog (EN/FA), extension cookie pass-through 1.3.0. 127/127 C# (+19) + 8/8 Flutter + 0 analyze; smoke + parity 12/12 + clean-env PASS. |
| v1.4.0 | 2026-10-24 | RELEASED (Linux x64) | BitTorrent core: bencode, .torrent+magnet, HTTP/UDP trackers (BEP-3/15), DHT (BEP-5), peer wire + BEP-10/9 ut_metadata, rarest-first/sequential pickers, resume bitfield, seeding, multi-file split; magnet UI + extension support 1.4.0. 160/160 C# (+33) + 8/8 Flutter + 0 analyze; loopback swarm E2E (own seeder + local tracker) byte-exact. |
| v1.0.2 | — | NOT STARTED | Reserved for user-test feedback fixes. |

## Version floor logic
- Trusted sources: this file + git HEAD + Dropbox /Nova-Download-Manager/state/VERSIONS.md + CHANGELOG.md.
- Floor = highest trusted version present across sources. Working below floor is forbidden.
- After any reset: compare local VERSIONS.md vs the Dropbox mirror; converge to the newest, then continue.

## Builds published to Dropbox (/Nova-Download-Manager/builds/)
| Artifact | Version | Status |
|----------|---------|--------|
| nova-dm-1.0.1-linux-x64.tar.gz (70,586,472 B, sha256 e0199b86…c603e) | v1.0.1 | PUBLISHED 2026-10-23, round-trip verified |
| nova-dm-1.1.0-linux-x64.tar.gz (sha256 748289f6…1d2ad3) | v1.1.0 | PUBLISHED 2026-10-24 |
| nova-dm-1.2.0-linux-x64.tar.gz | v1.2.0 | PUBLISHED 2026-10-24 |
| nova-dm-1.3.0-linux-x64.tar.gz (sha256 73ca0059…) | v1.3.0 | PUBLISHED 2026-10-24 |
| nova-dm-1.4.0-linux-x64.tar.gz (48,077,125 B, sha256 e61360c39a1aeb1e698ae55e283eaee9a67f9cffca7fe1a74860130c6f8cf772) | v1.4.0 | PUBLISHED 2026-10-24, round-trip verified |
| nova-chromium-1.4.0.zip / nova-firefox-1.4.0.zip | v1.4.0 | PUBLISHED 2026-10-24 (magnet links in ALL-links dialog) |
| nova-dm-1.2.0-linux-x64.tar.gz (41 MB, sha256 55432af8…e9d5cf5) | v1.2.0 | PUBLISHED 2026-10-24 |
| nova-chromium-1.2.0.zip / nova-firefox-1.2.0.zip | v1.2.0 | PUBLISHED 2026-10-24 (options page + special keys) |
| evidence-nova-linux-ui.png / evidence-nova-linux-ui-download.png | v1.0.1 | PUBLISHED (visual QA evidence) |
| nova-linux-ui-download.png + evidence-v1.1.0-vlm.json | v1.1.0 | PUBLISHED (visual QA evidence) |
