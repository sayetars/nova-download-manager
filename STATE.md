# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.4.0
CURRENT TASK: T-016 (v1.4.0 BitTorrent core) COMPLETE — src/Nova.Engine/Torrent/ (Bencode, TorrentMeta+Creator, HttpTracker BEP-3, UdpTracker BEP-15, DhtClient BEP-5, PeerWireClient BEP-3/10/9, PiecePicker, TorrentSession, TorrentTask+SeederRegistry); magnet:/+local .torrent first-class add_download URLs (DownloadKind.Torrent); SQLite migration v8 (bt_info_hash, seed_after); TaskSnapshot +bt_info_hash/+seed_after; bt_* settings (dht/upload_cap/listen_port/listen/metadata_timeout); Flutter: magnet banner + seed-after toggle in Add dialog (EN/FA), clipboard+drop magnet support, TaskUi btInfoHash/seedAfter; extensions 1.4.0 magnet links in ALL-links dialog (repackaged + zipped).
CURRENT SUBSTEP: —
DONE: v1.4.0 tarball download/nova-dm-1.4.0-linux-x64.tar.gz (sha256 e61360c39a1aeb1e698ae55e283eaee9a67f9cffca7fe1a74860130c6f8cf772, 48077125 B); round-trip verified on Dropbox; extension zips 1.4.0 uploaded; 160/160 dotnet (+33 torrent tests in TorrentProtocolTests/TorrentE2eTests/TorrentEngineTests); flutter 8/8 + 0 analyze errors; smoke PASS; parity 12/12 PASS; tarball clean-env PASS.
NEXT: (1) user test v1.4.0 tarball; (2) v1.4.x roadmap = PEX (BEP-11), DHT peer-store serving, per-file selection in multi-file torrents, UDP tracker scrape, super-seeding; (3) partial-file player preview (G17); (4) optional yt-dlp bridge (G28); (5) per-server perf profiles (G27) + client certs (G25) + metalink PGP (G26).
BLOCKERS: none
LAST VERIFIED: 2026-10-24 — dotnet test 160/160; flutter analyze 0 errors; flutter test 8/8; smoke_test PASS; parity_smoke_test 12/12; tarball_cleanenv_test PASS; BitTorrent E2E byte-exact via own seeder + loopback tracker (file task, magnet task, resume, multi-downloader, seed-back).
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain live at sdk/{dotnet,flutter} + ~/tools/{gtk3-dev,llvm-19}; venv cmake+ninja at ~/.venv; source scripts/env.sh; no root; no background process survival between tool calls; clang-19 symlinks (clang/clang++) restored after env reset.
```

## DONE (high level)
- v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 releases (see VERSIONS.md).
- v1.4.0 (this session, after env reset + toolchain rebuild): BitTorrent core per docs/FEATURE-GAP-2026.md §4.1; engine 127→160 tests; extensions repackaged 1.4.0; tarball + clean-env + parity green; Dropbox uploaded + round-trip verified.

## NEXT
1. USER: test v1.4.0 tarball → feedback (v1.4.1 bugfix slot).
2. v1.4.x: PEX (BEP-11), DHT peer-store serving, per-file selection, UDP scrape, super-seeding.
3. Partial-file preview player; yt-dlp optional bridge; per-server perf profiles; client certs; metalink PGP.

## LAST VERIFIED STATE
- Evidence: download/nova-dm-1.4.0-linux-x64.tar.gz sha256 e61360c39a1aeb1e698ae55e283eaee9a67f9cffca7fe1a74860130c6f8cf772
- Tests: 160/160 engine, 8/8 flutter, smoke + parity 12/12 + clean-env PASS (2026-10-24).

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md.
