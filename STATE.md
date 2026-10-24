# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.3.0
CURRENT TASK: T-015 (v1.3.0 Beyond IDM — Market Parity) COMPLETE — research matrix docs/FEATURE-GAP-2026.md; shipped: Metalink v4/v3 + RFC 6249 + Digest, checksum sidecars (.sha256/.md5/.sfv + CRC32), piece-hash verify+repair, multi-source parallel download, per-task cookies+headers, Netscape cookies.txt import, RSS/Atom FeedService, SFTP (SSH.NET 2026.0.0), WebDAV (dav/davs + PROPFIND), auto-extract (zip in-proc; 7z/rar external), ffmpeg convert (mp3/remux), filename tokens, sequential mode, JSON-RPC 2.0 remote (HTTP+WS, token, port-walk), Feeds page + settings sections + Advanced add-dialog (EN/FA), extension opt-in cookie pass-through 1.3.0.
CURRENT SUBSTEP: —
DONE: v1.3.0 tarball download/nova-dm-1.3.0-linux-x64.tar.gz (sha256 73ca0059478f2818d5e629cbb71b9055b7b2b62bdc717938c639cd5015089eb8); SQLite migration v7 (feed_rule/feed_seen + 6 task columns); TaskSnapshot wire additions (mirrors/expected_hash/has_cookies/sequential/convert_preset/source_count); SSH.NET bumped 2024.1.0→2026.0.0 (NU1903); publish fixed (AssemblyName must NOT be passed on CLI — leaked into restore graph; publish without /p).
NEXT: (1) user test v1.3.0; (2) v1.4.0 flagship = BitTorrent core (bencode/trackers/DHT/BEP-9/10 — honest subsystem, see FEATURE-GAP-2026 §4); (3) partial-file player preview; (4) optional yt-dlp bridge.
BLOCKERS: none
LAST VERIFIED: 2026-10-24 — dotnet test 127/127; flutter analyze 0 errors; flutter test 8/8; smoke_test PASS; parity_smoke_test 12/12; tarball_cleanenv_test PASS; JSON-RPC live round-trip (engine.status + task.add over curl).
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain live at sdk/{dotnet,flutter} + ~/tools/{gtk3-dev,llvm-19,glvnd,xvfb-dev}; venv cmake+ninja at ~/.venv; source scripts/env.sh; no root; no background process survival between tool calls.
```

## DONE (high level)
- v1.0.x/v1.1.0/v1.2.0 releases (see VERSIONS.md).
- v1.3.0 (this session): beyond-IDM market parity per docs/FEATURE-GAP-2026.md; engine 108→127 tests; docs/CHANGELOG/VERSIONS updated; extensions repackaged 1.3.0; tarball + clean-env + parity green.

## NEXT
1. USER: test v1.3.0 tarball → feedback (v1.3.1 bugfix slot).
2. v1.4.0: BitTorrent core (bencode, .torrent+magnet, HTTP/UDP trackers, DHT, ut_metadata, piece picker, seed mode) — verified against local tracker + own seeder.
3. Partial-file preview player; yt-dlp optional bridge; per-server perf profiles; client certs; metalink PGP.

## LAST VERIFIED STATE
- Evidence: download/nova-dm-1.3.0-linux-x64.tar.gz sha256 73ca0059478f2818d5e629cbb71b9055b7b2b62bdc717938c639cd5015089eb8
- Tests: 127/127 engine, 8/8 flutter, smoke + parity + clean-env PASS (2026-10-24).

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md.
