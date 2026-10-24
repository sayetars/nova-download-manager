# Changelog

All notable changes to Nova Download Manager. Format follows Keep a Changelog; versioning is semantic (product pre-1.0 parity is intentional).

## [1.5.0] — 2026-10-25

**The G-track** — the five flagship gaps identified in the v1.4.1 post-ship review: partial video preview while downloading (G17), optional yt-dlp bridge (G28), per-server performance profiles (G27), TLS client certificates (G25) and Metalink PGP verification (G26). C# suite grew 169 → 195 tests (+26 in `V150Tests`); Flutter 8 → 13 tests + 0 analyze errors; smoke PASS + parity 12/12 + tarball clean-env PASS; tarball round-trip verified on Dropbox.

### Added — engine
- **Partial preview server (G17)**: `Preview/PreviewServer.cs` — a loopback-only, token-authenticated HTTP server that streams the in-progress `.novadl` file to any media player. GET/HEAD + full Range support (seek by re-request); unranged GETs are clamped to the CONTIGUOUS downloaded prefix so the player only ever sees real bytes (the advertised length grows as data arrives); ranged reads may cross sparse regions (zeros — players tolerate, same as torrent streaming); MIME guessing per media type; segment map comes live from the engine (running plans) or the repository (paused/interrupted). IPC: `preview_start` / `preview_stop`.
- **yt-dlp bridge (G28)**: `YtDlp/YtDlpBridge.cs` — optional, user-supplied binary (Settings → yt-dlp path; auto-probes `yt-dlp`/`yt-dlp_linux`/`youtube-dl` on PATH; never bundled or downloaded). `ytdlp_status` / `ytdlp_analyze` (`-J --no-playlist`, parsed into a format table) / `ytdlp_download` (resolves the chosen format to a direct URL via `-g`, then downloads through the NORMAL engine pipeline — multi-segment, resume, mirrors, partial preview via forced sequential allocation; HLS/DASH URLs route to the engine media pipeline). Invariant #3 kept: DRM-tagged formats are never surfaced; live streams are refused. In-app player embedding (media_kit) was attempted and reverted — no system libmpv in the dev sandbox and no root (documented in KNOWN-LIMITATIONS; preview hands off to the system player instead).
- **Per-server performance profiles (G27)**: `ServerProfiles/ServerProfileService.cs` — EWMA throughput (α=0.25), EWMA TTFB and ok/fail counters per host, fed by the probe, every slice request and every drain in `TaskRunner`; snapshots persist to SQLite (`server_profile` table, migration v10) and survive restarts. Metalink mirrors within the same priority bucket are re-ordered fastest-first by the blended score (throughput × success² ÷ (1+latency)). IPC: `server_profiles` (with active-now counts) / `reset_server_profiles`; Settings shows the table (speed, latency, success rate, served bytes, score) per server.
- **TLS client certificates (G25)**: `Security/ClientCertificateService.cs` + `client_cert` table — bind a certificate to an exact host or `*.wildcard` (most-specific match wins). Sources: PFX/P12 (with passphrase), PEM cert+key, or a user-store thumbprint. Wired through `SocketsHttpHandler.SslOptions.LocalCertificateSelectionCallback` on BOTH engine HttpClients (no handler rebuild on edits — the callback reads a 10 s cache). IPC: `list_client_certs` / `save_client_cert` (validated before saving) / `delete_client_cert`; passphrase is never echoed over IPC. Verified with an in-test mTLS server that requires the client cert.
- **Metalink PGP (G26)**: `Security/PgpVerifier.cs` (BouncyCastle) — detached-signature verification (armored or base64), multi-key keyring attempts, one broken key never breaks the ring. Metalink `<signature type="pgp">` (RFC 5854) is captured at analyze; `.sig`/`.asc` sidecars are fetched best-effort. Policy `engine.metalink_pgp` = off | **warn** | require — require fails the task with `pgp_failed`/`pgp_required`/`pgp_no_key`, warn records `pgp_status` on the task (verified/failed/nokey/error) and shows a badge in the UI. Keyring: `pgp_keyring` table + IPC `list_pgp_keys`/`save_pgp_key`/`delete_pgp_key` (fingerprint extracted on import).

### Added — persistence / IPC
- SQLite migration v10: `server_profile`, `client_cert` (unique host pattern), `pgp_keyring` (unique fingerprint), `download_task.pgp_signature` + `pgp_status` columns; task repository mapping end-to-end.
- `TaskSnapshot` + `pgp_status`/`has_pgp_signature` (task rows and events).

### Added — UI (Flutter, EN + FA)
- **Preview dialog (G17)**: task context menu gains "Preview" for video/audio/HLS/DASH tasks while downloading, paused, interrupted or completed — live availability bar, copy-URL button and one-click handoff to the system player; the engine's private `127.0.0.1` stream URL is shown for manual players.
- **yt-dlp quality panel (G28)**: the Add dialog gains "Analyze with yt-dlp" for page URLs — the format table (resolution, extension, size, protocol, needs-merge marking) with radio selection; preselects the best directly-downloadable format; localized failure reasons (no binary / live / DRM / generic).
- **Settings → Security**: client-certificate manager (source dropdown PFX/PEM/store, host pattern, paths, passphrase, list + delete) and the PGP keyring (paste armored key, labeled list, delete) plus the Metalink PGP policy dropdown (off/warn/require).
- **Settings → Server performance**: the live per-server table (avg speed, latency, success %, served bytes, score, active-now bolt) with a reset action; Settings → yt-dlp: path field + availability chip (version + path from the engine).
- ~55 new strings, EN + FA (RTL-safe).

### Verified end-to-end (loopback-only)
- Preview: unranged GET clamps to contiguous prefix (byte-exact body), ranged 206 + 416 handling, suffix ranges, token auth (404 on bad token/unknown task), HEAD length, MIME map, contiguity math (gaps stop the prefix).
- yt-dlp: format parsing (DRM + storyboard excluded; direct flags for progressive/audio-only/m3u8), analyze + resolve against a fake `yt-dlp` binary, structured errors for missing binary / failed analysis.
- Profiles: EWMA + score math, persistence round-trip, stable fastest-first mirror ordering (unknown hosts last), live recording during a real engine download (2 MiB served recorded).
- Client certs: mTLS handshake with the bound PEM cert (thumbprint matched server-side) vs. handshake failure without a binding; host-pattern matching (exact / wildcard / non-match); repo upsert-on-host + passphrase retrieval.
- PGP: RSA-2048 keygen + sign + verify round-trip; tampered data → Invalid; missing keyring → NoKey; garbage → Malformed; metalink XML signature extraction → verified; E2E warn policy (valid sig → task completes `verified`; wrong sig → completes `failed`) and require policy (wrong sig → task FAILS with `pgp_failed`).
- Full suites: dotnet 195/195, flutter 13 passed + 1 skipped, 0 analyze errors; smoke / parity 12/12 / clean-env PASS; release tarball sha256 round-trip verified on Dropbox.

### Notes
- Two pre-existing timing-sensitive tests (`DownloadEngineTests` suite) flake intermittently under full-suite parallel load on the 2-CPU dev sandbox — they pass in isolation and in the majority of full runs; reproduced at v1.4.1 baseline before any v1.5.0 change. Heavy v1.5.0 suites are serialized with the torrent collection to reduce contention.

## [1.4.1] — 2026-10-25

**BitTorrent polish** — the v1.4.x roadmap items: PEX peer exchange, DHT peer-store serving, per-file selection in multi-file torrents, UDP tracker scrape, and super-seeding. C# suite grew 160 → 169 tests (+9 in `TorrentV141Tests`); Flutter 8/8 + 0 analyze errors; smoke PASS + parity 12/12 + tarball clean-env PASS; tarball round-trip verified on Dropbox.

### Added — engine (`src/Nova.Engine/Torrent/`)
- **PEX (BEP-11)**: new `PexMessage` codec (binary-safe `added`/`added.f`/`dropped` compact lists, tolerant decoding, port-0 filtering); `ut_pex` advertised in the BEP-10 extension handshake (fixed id 2, alongside `ut_metadata`); per-peer sent-set diffing in `TorrentSession.BroadcastPexAsync` (30 s cadence, loop-safe — only directly-connected peers are announced); inbound PEX feeds the discovery queue with dedup; per-client `v`/`reqq` parsing from the extension handshake (`PeerClientName`, `PeerRequestQueueDepth`).
- **DHT peer-store serving (BEP-5)**: `announce_peer` now validates the token and STORES the announced endpoint (sender-IP + announced port, `implied_port` honored, 64 peers/hash, 30-min TTL); `get_peers` replies serve stored peers as `values` (compact form) and fall back to the 8 closest routing-table nodes; `find_node` implemented with closest-node replies; DHT loop in `TorrentSession` is now lifelong (get_peers + announce every 90 s, was one-shot).
- **Per-file selection (multi-file torrents)**: `TorrentMeta.PieceMaskForFiles` / `SelectedFileBytes` piece↔file mapping; `PiecePicker.Wanted` mask gates picking, completion (`IsComplete` = all wanted pieces) and remaining-bytes; selection flows task (`selected_files` CSV) → spec → session; unwanted pieces are never requested and completion splits only the selected files; the wanted mask persists in the bitfield sidecar (backward-compatible layout) and is re-derived to a file set on resume; partial tasks report selected totals (`TotalBytes` = selected bytes).
- **UDP tracker scrape (BEP-15 action=2)**: `UdpTrackerClient.ScrapeAsync` for 1..74 info-hashes (seeders/completed/leechers), connection-id reuse + stale reconnect, error-action handling (tested against a loopback scrape harness).
- **Super-seeding (BEP-16)**: `TorrentSessionOptions.SuperSeed` / per-task `super_seed` — in seed mode we send NO bitfield; each peer is dripped one unrevealed piece via HAVE, and only after the peer confirms (its own HAVE back) is the piece revealed swarm-wide and the next piece assigned (round-robin fallback once every piece is out).
- **Protocol detail**: peer-wire `reqq` clamps our request pipeline to the peer's advertised depth; `TorrentPeer.TryParseCompact` for tolerant PEX/DHT parsing.

### Added — persistence / IPC
- SQLite migration v9 (`selected_files` CSV, `super_seed`); repository mapping end-to-end.
- `AddDownloadRequest.SelectedFiles` / `.SuperSeed`, `AddDownloadSpec`, `TaskSnapshot.SelectedFiles` / `.SuperSeed` wired through the router.

### Added — UI (Flutter, EN + FA)
- Add dialog: local `.torrent` files are parsed in-app (new minimal bencode reader `core/torrent_meta.dart`) and listed with per-file checkboxes (name + size) → partial download; "Super-seeding (BEP-16)" toggle appears when seeding is enabled; seeding toggle now also applies to `.torrent` files (not just magnets); new strings EN + FA (superSeedTorrent, torrentFiles, torrentFilesAll…).

### Verified end-to-end (loopback-only)
- PEX codec roundtrip incl. flags + malformed/truncated/port-0 tolerance; wire-level PEX routed by the receiver's declared id between two live handshaked clients.
- DHT: announce stored → get_peers serves it back as `values`; garbage token rejected (store unchanged).
- UDP scrape stats parsed for multiple hashes.
- Multi-file E2E with file selection: only the selected file is downloaded/split (shared boundary piece included), byte-exact.
- Super-seed E2E: byte-exact transfer through the HAVE-drip protocol (drip logs asserted).

## [1.4.0] — 2026-10-24

**BitTorrent core** — the last headline gap vs FDM / Motrix / JDownloader 2 / qBittorrent. A real, protocol-correct v1 stack implemented from the specs and verified against our own seeder + loopback tracker (no external swarm dependency in tests). C# suite grew 127 → 160 tests (+33 across `TorrentProtocolTests`, `TorrentE2eTests`, `TorrentEngineTests`); Flutter 8/8 + 0 analyze errors.

### Added — engine (`src/Nova.Engine/Torrent/`)
- **Bencode codec (BEP-3)**: canonical, binary-safe (byte-string keys and values), lexicographic key sorting enforced at encode time (valid info-hash requirement), grammar-aware `MeasureFirstValue` for `[dict][raw bytes]` splits (tested against BEP-3's own example vectors).
- **Metainfo**: `.torrent` parser (single- + multi-file, path-traversal sanitized), magnet URI parser (hex + base32 `urn:btih`), magnet builder, info-hash computation, and a `TorrentCreator` for building torrents from local files.
- **HTTP tracker client (BEP-3)**: percent-encoded `info_hash`/`peer_id`, compact + dictionary peer lists, failure-reason propagation, `event` semantics (tested against a loopback tracker harness).
- **UDP tracker client (BEP-15)**: connect/announce with transaction ids, connection-id caching + stale reconnect, `left`/`uploaded`/`downloaded` accounting (tested against a loopback UDP tracker).
- **DHT node (BEP-5)**: KRPC ping/find_node/get_peers/announce_peer over UDP, 160-bit k-bucket routing table (8 per bucket, stale-node eviction), sender-bound rotating announce tokens, iterative get_peers lookup toward the info hash, standard router bootstrapping (`router.bittorrent.com` & co.); off by default in tests via `bt_dht=0`.
- **Peer wire protocol (BEP-3)**: handshake, choke/unchoke/interested/not-interested/have/bitfield/request/piece/cancel, 16 KiB block pipeline (12 deep), keepalives; extension protocol handshake (BEP-10) with `metadata_size` advertisement and **ut_metadata (BEP-9)** for magnet metadata exchange — both client and server sides (we fetch AND serve metadata).
- **Piece picker**: rarest-first with randomized tie-break, optional sequential mode, block-level in-flight deduplication, per-piece release/requeue, availability tracking from bitfields + have messages.
- **TorrentSession orchestrator**: tracker + DHT + static-peer discovery, inbound listener (seeding), channel-buffered per-peer pumps (cancelling a channel read never kills a TCP stream), SHA-1 piece verification on read-back with automatic requeue of corrupt pieces, crash-safe resume via bitfield sidecar (`.novadl.bf`), metadata sidecar (`.torrent` per info-hash for magnet resume), multi-file split-on-completion, seeding with upload cap + `TorrentSeederRegistry` for post-completion serving.
- **Engine integration**: `magnet:` URIs and local `.torrent` paths are first-class `add_download` URLs (`DownloadKind.Torrent`, schema validation extended), `TaskRunner` dispatch to `RunTorrentAsync` before HTTP probing, progress/quota/speed-meter wiring identical to FTP/SFTP, per-task `seed_after` flag, settings `bt_dht` / `bt_upload_cap` / `bt_listen_port` / `bt_listen` / `bt_metadata_timeout`, SQLite migration v8 (`bt_info_hash`, `seed_after`), `TaskSnapshot` + `bt_info_hash` + `seed_after` fields.

### Added — UI (Flutter, EN + FA)
- Add dialog accepts `magnet:` links and dropped `.torrent` files; magnet detection banner; "keep seeding after completion" toggle (FA translations included).
- Clipboard monitor and drag & drop now recognize magnet links; task model carries `btInfoHash` / `seedAfter`.

### Added — extensions (1.4.0)
- "Download ALL links" page dialog now collects magnet: anchors alongside HTTP(S) links (both Chromium and Firefox builds).

### Verified end-to-end (loopback-only swarms)
- Full download from our own seeder via a local HTTP tracker — byte-exact, seeder upload accounting, temp + bitfield cleanup after completion.
- Magnet task: metadata fetched via ut_metadata from the seeder, then downloaded byte-exact **through the real engine** (add → dispatch → DB → events).
- Resume from a persisted bitfield downloads exactly the missing pieces (deterministic pre-seeded state).
- Multi-downloader fan-out (3 concurrent) and completed-session seeding back to a second downloader.
- Corrupt/garbage temp data is fully re-verified and re-downloaded.

## [1.3.0] — 2026-10-24

**Beyond IDM — Market Parity** — closes the gaps that IDM never had but FDM / JDownloader 2 / aria2 / Motrix have (research matrix: `docs/FEATURE-GAP-2026.md`). C# suite grew 108 → 127 tests (+19 in `V13FeatureTests`); Flutter 8/8 + 0 analyze errors; smoke + parity E2E 12/12 + tarball clean-env PASS.

### Added — engine
- **Metalink v4 (RFC 5854) + v3**: `.meta4`/`.metalink` tasks expand into mirrors + whole-file hashes + piece hashes (priority + maxconnections attributes honored, tested); Metalink/HTTP **RFC 6249** — `Link: rel=duplicate` mirrors + RFC 3230 `Digest` header (SHA-256/SHA-1/MD5, base64→hex) captured at probe time.
- **Checksum sidecars**: best-effort discovery of `.sha256` / `.md5` / `.sfv` next to the file URL (coreutils + BSD-tag + SFV formats, name-matched, tested); opt-out via `engine.sidecar_verify=false`.
- **Algo-qualified integrity framework**: `sha256:` / `sha1:` / `md5:` / `crc32:` expected hashes (inline CRC-32, IEEE check-vector tested) with `Validation` surfaced on the task.
- **Piece-hash verification + repair** (aria2 `--check-integrity` parity): Metalink pieces verified after download, damaged ranges re-fetched and rewritten in place (2 passes), honest `piece_verify_failed` failure (tested: correct pieces pass, corrupted expectation fails).
- **Multi-source parallel download** (aria2/FDM parity): mirrors are no longer failover-only — workers round-robin across the source pool simultaneously with per-source strike-based health marking; worker-level fallback absorbs source failures; engine-level mirror rotation kept as the last resort (tested).
- **Cookies + custom HTTP headers per task**: `cookies` header + `extra_headers` JSON applied on probe, all segment requests, vault-retry, repair fetches and every source (tested end-to-end via header observation). Redirect handler still strips Authorization cross-origin.
- **Netscape cookies.txt import**: `import_cookies` IPC parses 7-field + `#HttpOnly_` lines into per-host cookie headers (tested).
- **RSS/Atom feed subscriptions**: `feed_rule`/`feed_seen` tables (migration v7), `FeedService` 30 s due-check timer, RSS 2.0 + Atom (namespace-safe), enclosure → download with `%title%.%ext%` naming, keyword filters, per-rule interval, seen-state dedupe across restarts (tested E2E: adds once, second run skips).
- **SFTP protocol** (SSH.NET 2026.0.0): segmented parallel-connection downloads with per-segment seeks, single-connection resume, directory listing (scheme admitted: `sftp://`).
- **WebDAV**: `dav://`→`http`, `davs://`→`https` rewrite (ranged-GET flow unchanged) + `dav_list` PROPFIND Depth-1 browser (zip-slip-safe parser, tested).
- **Archive auto-extract**: ZIP in-process (zip-slip guarded, tested); 7z/RAR via 7-Zip/bsdtar/unrar discovery when installed; `extract.auto` + `extract.delete_archive` settings.
- **ffmpeg conversion presets**: per-task `mp3` (audio extract) and `remux` (stream-copy to MP4); `ffmpeg.path` setting; honest failure when ffmpeg is missing; on-demand via `task_postprocess` IPC.
- **Filename token templates** (aria2 parameterized-URI parity): `%date% %time% %index% %domain% %category% %title% %ext%` — case-insensitive (tested).
- **Sequential (in-order) segment allocation**: aria2 `--stream-piece-selector=inorder` parity for partial media preview (tested byte-exact).

### Added — remote control
- **JSON-RPC 2.0 over HTTP + WebSocket** (aria2-RPC / FDM-remote parity): `127.0.0.1:16801/rpc/` (port walk on collision, `rpc.enabled` / `rpc.port` / `rpc.token` settings), 24 mapped methods (`engine.status`, `task.add/list/pause/resume/…`, `feed.*`, `settings.*`), batch arrays, camelCase params normalized, Bearer-token auth (401 without), GET → service banner, WebSocket live `engine_status` stream after token handshake. Verified live: status + task.add round-trip.

### Added — UI (Flutter, EN + FA)
- **Feeds page** (new sidebar section): list / add / edit / run-now / delete with result toasts.
- **Settings**: Remote control section (enable + token display), Integrity section (sidecar verification), Post-processing section (auto-extract + delete-archive + ffmpeg path).
- **Add dialog → Advanced**: per-task cookies, extra headers, expected hash, sequential download; URL filter now admits `sftp://`, `dav://`, `davs://`.

### Changed — extensions (both browsers, 1.3.0)
- **Opt-in cookie pass-through**: new "Send site cookies with downloads" option (privacy-first, OFF by default, `cookies` permission added); when enabled, login-gated downloads carry the site's cookies to the engine.

### Changed — platform
- SQLite migration v7 (6 task columns + `feed_rule` + `feed_seen`).
- `TaskSnapshot` wire additions: `mirrors`, `expected_hash`, `has_cookies`, `sequential`, `convert_preset`, `source_count`.
- SSH.NET 2026.0.0 (2024.1.0 flagged vulnerable by NuGet audit).
- Engine + IPC version strings → 1.3.0; UA `NovaDownloadManager/1.3`.

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
