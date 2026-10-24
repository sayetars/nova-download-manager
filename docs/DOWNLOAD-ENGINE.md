# Download Engine

The engine lives in `src/Nova.Engine` (+ `Nova.Scheduler` for admission and `Nova.Media` for streams). This document describes the real implementation.

## HTTP layer

- `HttpClient` over `SocketsHttpHandler`, `AllowAutoRedirect=false`, decompression **off** (ranges must stay raw), connect timeout 15 s.
- Redirects are followed by a custom `SafeRedirectHandler` (≤20 hops) that **strips Authorization/Referer/Cookie when the redirect leaves the origin** — credentials never leak to another domain.
- Probe (`HttpProber`): `GET` with `Range: bytes=0-0`. A `206` proves range support; headers give `Content-Length` (from `Content-Range`), `ETag` (weak prefix stripped), `Last-Modified`, `Content-Disposition` (RFC 5987 UTF-8 filenames — Persian names preserved), and the final URL after redirects.
- Status mapping: 401/403/404/410 → permanent failure; 408/429/5xx → retryable; 416 → range handling (see below); DNS/TLS/timeout → structured categories.

## Task lifecycle

States: `Created → Analyzing → Queued → Downloading ⇄ Paused, Retrying, Interrupted → Verifying → Completed | Failed | Cancelled`.

- **Pause** cancels a cooperative token; workers finish their in-flight chunk, counters are persisted, state → `Paused`. Data written before the last persisted counter is simply re-downloaded (idempotent same-offset writes).
- **Cancel** additionally deletes the `.novadl` temp file.
- **Engine shutdown** marks running tasks `Interrupted` (recoverable); the startup recovery pass re-queues them when `engine.resume_on_start` is enabled.

## Multi-part downloading

1. Plan builder: if the server supports ranges and the file is ≥2 MB, split into `min(max_connections, size/512 KB)` contiguous, non-overlapping segments; otherwise a single stream (server without ranges gets exactly one connection regardless of the configured maximum).
2. Workers run concurrently per segment; each writes into **one shared `FileStream`** guarded by an async lock at absolute offsets — no write/write race is possible by construction.
3. Downloads proceed in ≤8 MB slices per connection so pause/cancel is honored quickly.
4. Segment progress (`start, end, received`) is persisted to SQLite and committed at ≥1 s intervals; the invariant "persisted counters never exceed bytes actually on disk" makes resume safe.
5. Unknown length: single connection, streamed to EOF; size is learned only at completion.

## Resume & recovery

- On resume the engine re-probes; if `ETag`/`Last-Modified` changed or the size differs, partial data is discarded and the download restarts (event emitted) — data from two different file versions can never mix.
- Validator absent + size equal → resume as normal.
- Crash/restart recovery: DB rows in running states → `Interrupted` → optionally re-queued.

## Retries

- Exponential backoff with jitter (1.5 s → cap 5 min), honoring `Retry-After`. Per-task budget (`max_retries`, default 5). Permanent errors (401/403/404) do not retry.
- Disk-space gate before start and a 50 MB headroom check; `disk_full` is a structured error.

## Integrity

- Optional expected SHA-256 → the engine computes the hash of the finalized temp file; mismatch = `hash_mismatch` failure and the file is **not** delivered. Pass state is recorded on the task.

## Files

- Temp file: `<final>.novadl`, random-access, fsync on finalize, then an atomic `File.Move` to a **unique** final name (`name (2).ext`) — completed files are never overwritten and incomplete files are never reported as complete.
- Names: Unicode-safe sanitizer strips path components (path traversal), Windows-reserved names, and control characters.

## Scheduler (`Nova.Scheduler`)

The only component that starts queued tasks. 500 ms admission loop honoring:

- global `scheduler.max_concurrent` (default 3),
- per-queue `max_concurrent` caps and speed caps,
- time-window rules (days + start/end, overnight windows supported) — outside windows no new starts,
- battery policy (`scheduler.start_on_battery`),
- anti-starvation aging: each 5 minutes of wait adds one priority notch (max +2).

## Rate limiting

Token bucket (global + per-task + per-queue), bytes-priced, await-fair. Sliding-window (4 s) speed meter; ETA is `-1` (unknown) rather than a guess when the rate is not measurable.

## HLS / DASH (`Nova.Media`)

- HLS: master → variant selection (best bandwidth, or preferred height, or lowest), media playlist parse (segments, init segment, keys). `EXT-X-KEY != NONE` → refused (`drm_protected`); live (no `EXT-X-ENDLIST`) → refused (`live_stream`). Segments are downloaded sequentially and concatenated (TS streams concatenate natively; fMP4 handled with init segments).
- DASH: MPD parse (SegmentTemplate duration & timeline, SegmentList), representations with bandwidth/size, `ContentProtection` → refused. Separate audio+video representations: muxed via **ffmpeg if found on PATH**; otherwise saved as two clearly named files (documented limitation, no fake muxing).
- The task progress panel shows segment counts (`segments_done/segments_total`) for media tasks.

## BitTorrent (`Nova.Engine/Torrent/`, v1.4.0)

A protocol-correct BitTorrent v1 stack, implemented from the BEPs and verified against
our own seeder + a loopback tracker (all tests offline; no external swarm dependency).

| Piece | File | BEP |
|---|---|---|
| Bencode codec (canonical, binary-safe, sorted keys) | `Bencode.cs` | 3 |
| Metainfo + magnet parse/build + torrent creator | `TorrentMeta.cs` | 3/9 |
| HTTP tracker announce (compact + dict peers) | `Trackers.cs` | 3 |
| UDP tracker connect/announce (txn ids, conn-id cache) | `Trackers.cs` | 15 |
| DHT node: KRPC, 160-bit k-buckets, get_peers iteration | `DhtClient.cs` | 5 |
| Peer wire: handshake, choke/interest, bitfield, request/piece | `PeerWireClient.cs` | 3 |
| Extension handshake + ut_metadata (fetch AND serve) | `PeerWireClient.cs` | 10/9 |
| Rarest-first / sequential piece picker + in-flight dedup | `PiecePicker.cs` | 3 strategy |
| Session: discovery, pumps, verify, resume, seed, split | `TorrentSession.cs` | — |
| TaskRunner adapter + seeder registry | `TorrentTask.cs` | — |

**Disk model** — one contiguous `.novadl` temp (random-access writes at piece offsets);
`<name>.novadl.bf` bitfield sidecar persists VERIFIED pieces only (crash-safe resume);
magnet metadata is persisted as `<infohash>.torrent` next to the destination; multi-file
torrents split into per-file paths on completion; single-file temp moves to final (unique
path on collision).

**Engine flow** — `magnet:` URIs and local `.torrent` paths bypass HTTP probing entirely
(`DownloadKind.Torrent`), progress/quota/speed wiring is identical to FTP/SFTP tasks, and
`seed_after` keeps the session serving after the task reports Completed (stopped when the
engine exits or the task is removed).

**Settings** — `bt_dht` (default on), `bt_upload_cap` (bytes/s while seeding),
`bt_listen_port` (fixed port) / `bt_listen=1` (ephemeral), `bt_metadata_timeout` (seconds).

## v1.4.1 — BitTorrent polish (PEX / DHT serving / file selection / scrape / super-seed)

**PEX (BEP-11)** — `PexMessage` codec encodes/decodes the `added` / `added.f` / `dropped`
compact lists (binary-safe, port-0 entries filtered, malformed payloads tolerated).
`ut_pex` is advertised in our BEP-10 handshake with the fixed id 2; the session keeps a
per-peer sent-set and broadcasts DIFFS every 30 s (only directly-connected peers are
announced — no gossip of second-hand endpoints), and inbound PEX feeds the discovery
queue with dedup. The extension handshake also parses the peer's `v` (client name) and
`reqq` (request-pipeline depth, clamped 1..64).

**DHT peer-store serving (BEP-5)** — `announce_peer` with a valid token now STORES the
announced endpoint (sender IP + announced port, `implied_port` honored; 64 peers per
info-hash, 30-minute TTL). `get_peers` replies carry stored peers as `values` (compact
6-byte form, ≤50 per reply) and otherwise fall back to the 8 closest routing-table
nodes; `find_node` is implemented the same way. The per-session DHT loop is lifelong:
every 90 s it re-queries `get_peers`, feeds discovered peers to the pool, and re-announces
our listen port.

**Per-file selection (multi-file torrents)** — `TorrentMeta.PieceMaskForFiles` maps
selected file indices to the wanted-piece mask (pieces shared at file boundaries are
wanted when ANY overlapping file is selected); `PiecePicker.SetWanted` gates picking,
completion (`IsComplete` = every WANTED piece verified) and remaining-bytes. Selection
travels as `selected_files` CSV on the task (IPC `add_download`), persists in the
bitfield sidecar (backward-compatible tail after the have-map), is re-derived to a file
set on resume, and completion splits ONLY the selected files. Partial tasks report
selected totals so progress shows 0→100 % for the chosen subset. The Flutter Add dialog
lists a local `.torrent`'s files (in-app bencode reader) with checkboxes.

**UDP tracker scrape (BEP-15 action=2)** — `UdpTrackerClient.ScrapeAsync` takes 1..74
info-hashes, reuses cached connection ids (stale reconnect once), and returns
seeders/completed/leechers per hash; tracker error actions surface as `InvalidDataException`.

**Super-seeding (BEP-16)** — per-task `super_seed` (or `bt_superseed=1`). In seed mode we
send NO bitfield; each peer receives exactly one unrevealed piece via HAVE. When the peer
confirms (its own HAVE back), the piece is revealed to the rest of the swarm and the next
piece is dripped; once every piece has been revealed at least once, assignment falls back
to round-robin. Verified byte-exact against our own downloader.

**Persistence** — SQLite migration v9 adds `selected_files` (CSV) and `super_seed`
(per-task); `TaskSnapshot` exposes `selected_files` and `super_seed` to the UI.
