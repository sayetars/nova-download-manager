# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.4.1
CURRENT TASK: T-017 (v1.4.1 BitTorrent polish) COMPLETE — PEX (BEP-11: PexMessage codec + ut_pex id 2 in handshake + per-peer diff broadcast + inbound feed), DHT peer-store serving (announce stores w/ token+implied_port+TTL, get_peers serves values, find_node, lifelong DHT loop w/ announce), per-file selection (TorrentMeta.PieceMaskForFiles, PiecePicker.Wanted, sidecar mask persistence + resume re-derive, split-only-selected, selected totals), UDP scrape (BEP-15 action=2, 1..74 hashes), super-seeding (BEP-16 HAVE-drip per peer + reveal-on-confirm + round-robin fallback, per-task super_seed); SQLite migration v9 (selected_files, super_seed); AddDownloadRequest/Snapshot +selected_files/+super_seed; Flutter Add-dialog .torrent bencode preview + file checkboxes + superseed toggle (EN/FA); extensions 1.4.1 rezipped.
CURRENT SUBSTEP: —
DONE: v1.4.1 tarball download/nova-dm-1.4.1-linux-x64.tar.gz (sha256 fe34c36a7527493b37105583254cdbb2149fe8c97e5bbd79b93608ebb1c1c384, 48266349 B); round-trip verified on Dropbox; extension zips 1.4.1 uploaded; 169/169 dotnet (+9 in TorrentV141Tests: PEX codec+wire, DHT store serve+bad-token, UDP scrape, piece mask, ParseSelectedFiles, multi-file selection E2E, super-seed E2E); flutter 8/8 + 0 analyze errors; smoke PASS; parity 12/12 PASS; tarball clean-env PASS.
NEXT: (1) user test v1.4.1 tarball; (2) v1.5.0 candidates: partial-file player preview (G17), yt-dlp optional bridge (G28), per-server perf profiles (G27), client certs (G25), metalink PGP (G26); (3) BT extras if asked: PEX flag-byte honored for uTP preference, DHT peer-store persistence across restarts.
BLOCKERS: none
LAST VERIFIED: 2026-10-25 — dotnet test 169/169; flutter analyze 0 errors; flutter test 8/8; smoke_test PASS; parity_smoke_test 12/12; tarball_cleanenv_test PASS (v1.4.1); Dropbox round-trip sha256 fe34c36a…c1c384 verified.
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain live at sdk/{dotnet,flutter} + ~/tools/{gtk3-dev,llvm-19}; venv cmake+ninja at ~/.venv; source scripts/env.sh; no root; no background process survival between tool calls; clang-19 symlinks (clang/clang++) restored after env reset.
```

## DONE (high level)
- v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.0 releases (see VERSIONS.md).
- v1.4.1 (this session, after env reset + full toolchain rebuild: dotnet 8.0.426 + Flutter 3.47.8 + GTK 3.24.49 + clang 19.1.7 + cmake/ninja): v1.4.x BT polish track complete — PEX / DHT peer-store / file selection / UDP scrape / super-seed; engine 160→169 tests; extensions 1.4.1; tarball + clean-env + parity green; Dropbox uploaded + round-trip verified.

## NEXT
1. USER: test v1.4.0 tarball → feedback (v1.4.1 bugfix slot).
2. v1.4.x: PEX (BEP-11), DHT peer-store serving, per-file selection, UDP scrape, super-seeding.
3. Partial-file preview player; yt-dlp optional bridge; per-server perf profiles; client certs; metalink PGP.

## LAST VERIFIED STATE
- Evidence: download/nova-dm-1.4.1-linux-x64.tar.gz sha256 fe34c36a7527493b37105583254cdbb2149fe8c97e5bbd79b93608ebb1c1c384
- Tests: 169/169 engine, 8/8 flutter, smoke + parity 12/12 + clean-env PASS (2026-10-25).

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md.
