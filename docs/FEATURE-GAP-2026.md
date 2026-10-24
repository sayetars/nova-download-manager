# Feature-Gap Analysis 2026 — Beyond IDM (Market Parity)

> Date: 2026-10-24 · Baseline audited: **Nova v1.2.0** (IDM parity complete — see `IDM-PARITY.md`).
> Purpose: second, broader research round requested by the user — compare Nova against the
> *whole* download-manager market, not only IDM, find every capability class we still lacked,
> and implement it. Outcome: **v1.3.0 "Beyond IDM — Market Parity"**.

Research sources: freedownloadmanager.org/features.htm, jdownloader.org knowledgebase +
supported-plugins list, aria2 1.37 manual (aria2.github.io), Motrix manual/diagnostics page,
Persepolis DM pages, uGet (sourceforge/Debian feature lists), Neat Download Manager pages,
Wikipedia "Comparison of download managers", RFC 5854 (Metalink/4), RFC 6249 (Metalink/HTTP),
RFC 3230 (Instance Digests), aria2 `--checksum` / `--stream-piece-selector` semantics,
sha256sum(1)/SFV sidecar formats — cross-checked against a direct code audit of this
repository (engine, IPC router, Flutter pages, extension sources).

Status legend: **WAS MISSING** (absent in v1.2.0) · **PARTIAL** · **IMPLEMENTED in v1.3.0** ·
**EXCLUDED** (deliberate, reason documented) · **ROADMAP** (planned, honest sequencing).

---

## 1. Competitor capability classes (what the market has that IDM does not)

| # | Capability | Seen in | Nova v1.2.0 | v1.3.0 |
|---|---|---|---|---|
| G1 | **BitTorrent** (.torrent / magnet, DHT, trackers, selective files) | FDM 6, aria2, Motrix, JDownloader 2, uGet (via aria2) | WAS MISSING | ROADMAP v1.4.0 flagship (§4) |
| G2 | **Metalink v4/v3** (mirrors + hashes + piece hashes, `.meta4`/`.metalink`) | aria2 | WAS MISSING | IMPLEMENTED |
| G3 | **Metalink/HTTP RFC 6249** (Link rel=duplicate + Digest) | aria2 | WAS MISSING | IMPLEMENTED |
| G4 | **Checksum sidecars** (`.sha256`/`.md5`/`.sfv`) + `--checksum` verify | aria2, sha256sum workflow | WAS MISSING | IMPLEMENTED |
| G5 | **Multi-source parallel download** (segments from mirrors simultaneously) | aria2, FDM | PARTIAL (failover-only) | IMPLEMENTED |
| G6 | **Piece-level integrity + damaged-download repair** | aria2 `--check-integrity` | PARTIAL (whole-file only) | IMPLEMENTED |
| G7 | **Cookies** (per-task, cookies.txt import, browser pass-through) | aria2 cookie engine | PARTIAL (strip-on-redirect only) | IMPLEMENTED (opt-in pass-through + import) |
| G8 | **Custom HTTP headers per task** | aria2 | WAS MISSING | IMPLEMENTED |
| G9 | **SFTP** | aria2, FDM (SSH remote mgmt) | WAS MISSING | IMPLEMENTED (SSH.NET, segmented) |
| G10 | **WebDAV** | JDownloader cloud, FDM | WAS MISSING | IMPLEMENTED (dav/davs + PROPFIND listing) |
| G11 | **RSS/Atom feed subscriptions → auto-download** (podcasts, release feeds) | JDownloader 2, FDM-adjacent | WAS MISSING | IMPLEMENTED (FeedService + Feeds page) |
| G12 | **Auto-extract archives after download** (zip/7z/rar) | JDownloader 2 | PARTIAL (ZIP preview/selective only) | IMPLEMENTED (zip in-proc; 7z/rar external) |
| G13 | **Remote control API** (JSON-RPC/daemon; Android app bridge) | aria2 RPC, Motrix RPC, FDM remote | WAS MISSING | IMPLEMENTED (JSON-RPC 2.0 HTTP + WS, token) |
| G14 | **Parameterized URIs / filename tokens** (`%index%`, `%date%`, …) | aria2, uGet batch | PARTIAL (`[01-99]` ranges only) | IMPLEMENTED |
| G15 | **Sequential/in-order download + partial media preview** | aria2 inorder/geom, FDM preview | WAS MISSING | IMPLEMENTED (sequential + preview groundwork) |
| G16 | **Post-download media conversion** (extract audio, remux) | FDM 6 ("convert file formats") | PARTIAL (ffmpeg mux for HLS/DASH only) | IMPLEMENTED (mp3/remux presets + UI) |
| G17 | **Video/audio preview before completion** | FDM 6 | WAS MISSING | PARTIAL → sequential mode enables moov-first mp4 preview; full player preview in v1.4.0 polish |
| G18 | Captcha solving / OCR + wait timers | JDownloader 2 | — | EXCLUDED: legal/ToS risk + maintenance burden |
| G19 | Premium hoster/decrypter plugin ecosystem (hundreds of sites) | JDownloader 2 | — | EXCLUDED: DMCA/ToS exposure; Nova stays a general-purpose manager |
| G20 | Link containers (DLC/RSDF/CCF) | JDownloader 2 | — | EXCLUDED: dying formats; standard .txt/.metalink/JSON import covers the need |
| G21 | Android remote app | FDM 6 | — | COVERED by G13: any device can drive the JSON-RPC endpoint over SSH/VPN |
| G22 | Browser: force/prevent interception keys, takeover editor | IDM/NDM (Delete-key bypass) | already HAVE (v1.1.0/v1.2.0) | — |
| G23 | Portable mode | FDM 3.9 | effectively HAVE (tarball + RUN-LINUX.sh, no admin) | — |
| G24 | IPv6 / Happy Eyeballs | aria2 | HAVE (OS + HttpClient stack) | — |
| G25 | Client-certificate TLS auth | aria2 `--certificate` | WAS MISSING | ROADMAP (niche; needs cert-store UX) |
| G26 | Metalink/HTTP signing (OpenPGP signature check) | RFC 6249 §5 | WAS MISSING | ROADMAP (needs GPG dependency decision) |
| G27 | Speed graph / server performance profiles (`--server-stat-of`) | aria2 | PARTIAL (speed graph HAVE; per-server stats absent) | ROADMAP P3 |
| G28 | Video-page grabber breadth (1000+ extractor plugins) | JDownloader/yt-dlp | PARTIAL (HLS/DASH + direct media) | ROADMAP: optional yt-dlp bridge (external tool, off by default) |

## 2. What v1.3.0 shipped (all verified)

Engine (all tested in `tests/Nova.Engine.Tests/V13FeatureTests.cs`, 19 tests):
Metalink v4/v3 + RFC 6249 + Digest; sidecars (coreutils/BSD/SVF); CRC-32 (IEEE vector);
piece repair (pass + honest failure); multi-source round-robin; cookies/headers on every
request path; Netscape cookies.txt; RSS + Atom E2E (once-only adds); ZIP extract
(zip-slip-safe); filename tokens; WebDAV PROPFIND + scheme rewrite; sequential byte-exact;
duplicate-add visibility.

Host: JSON-RPC server (HTTP POST + batch + WebSocket + 401 on bad token; live-verified),
feeds/cookies/dav_list/task_postprocess IPC, feed timer, RPC port-walk.

UI (EN+FA): Feeds page; Remote control / Integrity / Post-processing settings; Advanced
add-dialog section (cookies, headers, expected hash, sequential).

Extensions (Chromium + Firefox, 1.3.0): opt-in cookie pass-through with `cookies` permission.

## 3. Deliberate divergences (unchanged from IDM-PARITY §6)

DRM refusal; interception opt-in; Persian-first RTL; no captcha/hoster plugins.

## 4. v1.4.0 roadmap (honest sequencing — BitTorrent is a subsystem, not a checkbox)

1. **BitTorrent core** (G1): bencode, `.torrent` + magnet, HTTP/UDP trackers (BEP-3/15),
   DHT (BEP-5), extension protocol + ut_metadata (BEP-10/9), rarest-first + sequential
   pickers, multi-file, resume bitfield, seeding. Verified via a local tracker + our own
   seeder (no external swarm). This is the single remaining headline gap vs FDM/Motrix/JD2.
2. **Partial-file player preview** (G17 finish): in-app player on the `.novadl` temp file.
3. **yt-dlp optional bridge** (G28): external-tool integration, off by default.
4. **Per-server performance profiles** (G27) + client certificates (G25) + metalink PGP (G26).

## 5. Verification matrix for this document

| Claim | Evidence |
|---|---|
| 127/127 C# tests | `dotnet test Nova.sln` (v1.3.0, 2026-10-24) |
| 8/8 Flutter tests, 0 analyze errors | `flutter test`, `flutter analyze` |
| smoke (bundle boot → IPC → byte-exact file) | `scripts/linux/smoke_test.sh` PASS |
| parity E2E 12/12 | `scripts/linux/parity_smoke_test.sh` PASS |
| shipped-tarball clean-env, byte-exact | `scripts/linux/tarball_cleanenv_test.sh` PASS |
| JSON-RPC live status + task.add | engine boot + curl round-trip (2026-10-24) |
