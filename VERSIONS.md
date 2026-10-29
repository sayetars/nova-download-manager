# VERSIONS — Nova Download Manager

| Version | Date | Status | Notes |
|---------|------|--------|-------|
| v1.7.1 | 2026-10-29 | RELEASED (Linux x64 + Windows engine x64) | **Free-forever policy round.** docs/FREE-FOREVER.md (hard invariant, EN+FA); THIRD-PARTY-NOTICES.md audit (13 deps, all $0); scripts/free_audit.py CI gate (dependency allowlist + banned paid terms, in version-audit.yml); Trusted Signing REJECTED (D-013) → VERSIONING §8 free path (SHA-256 + self-signed); root LICENSE; free-forever line in Settings→Versions + extension About (EN/FA); LICENSE+NOTICES+FREE-FOREVER ship in both packages. 242/242 C#; Flutter 13 + 0 analyze; sync --check + free audit PASS; smoke + clean-env PASS. |
| v1.0.0 | 2026-10-23 | RELEASED (Windows path) | Engine + Flutter UI + extensions + installer + docs. 43/43 C# tests ×2, 8/8 Flutter tests, 0 analyze errors. Commits up to b06e865 + checkpoint c0dfa89. |
| v1.7.0 | 2026-10-29 | RELEASED (Linux x64 + Windows engine x64) | **Version-integrity round + AES-128 HLS.** `version.json` SSOT + `sync_versions.py` (10 stamped surfaces, 3 cross-checks) + CI audit gate (a drifted tree cannot build); runtime gates (boot schema fail-fast, truthful `engine_status`, extension EN/FA compatibility banner, Settings→Versions); `check_update` JSON manifest + `SemVersion`; legacy `nova/` tree (73 MB) purged (D-012); git tags v1.5.0/v1.6.0 recreated. AES-128 HLS downloaded + decrypted per RFC 8216 (explicit IV, sequence-IV fallback, mixed playlists; DRM still refused). C# 208→**242** tests; Flutter 13 + 0 analyze; linux tarball + win zip + ext zips rebuilt; smoke + clean-env PASS. |
| v1.6.0 | 2026-10-29 | RELEASED (Linux x64 + Windows engine x64) | Completeness round + first Windows deliverable: yt-dlp native-merge wired end-to-end (RPC native flag + UI selectable merge formats), DHT peer-store persistence (migration v11 + registry), honest handshake flag bits (BEP-5 DHT advertised, peer flags parsed), extensions 1.6.0. **nova-engine-1.6.0-win-x64.zip cross-compiled from Linux** (NovaEngine.exe + NovaBrowserHost.exe + nova.exe self-contained + extensions + register-nativehost.ps1 + README) + `.github/workflows/windows-build.yml` for full Windows CI. 208 C# tests (207/208 — one timing flake documented), 13 Flutter + 0 analyze; linux tarball 47 MB rebuilt. |
| v1.0.1 | 2026-10-23 | RELEASED (Linux x64) | First Linux build. 43/43 + 8/8 + 0 errors re-verified on Linux; release-bundle smoke suite PASS; self-contained no-runtime boot PASS; tar.gz + sha256 on Dropbox (round-trip verified). |
| v1.1.0 | 2026-10-24 | RELEASED (Linux x64) | IDM Parity Core + most of Plus: byte quotas, task properties editor, logins vault UI, categories editor, speed-limiter toggle, segment bars + speed graph, drag & drop (Win32+GTK), check-update, FTP byte-delta fix, live segment IPC. 81/81 C# + 8/8 Flutter + 0 analyze errors; parity E2E 12/12; clean-env tarball PASS; VLM QA PASS. |
| v1.2.0 | 2026-10-24 | RELEASED (Linux x64) | IDM Parity Polish — P2 complete: per-server caps, special keys (extension options page), per-queue schedules, periodic sync queues (ETag/Last-Modified), sortable downloads list, always-on-top mini-window (--mini), complete-dialog customization, IDM import. 108/108 C# (+27) + 8/8 Flutter + 0 analyze; parity E2E 12/12; mini smoke + VLM PASS; UI VLM PASS; clean-env tarball PASS. |
| v1.3.0 | 2026-10-24 | RELEASED (Linux x64) | Beyond IDM — Market Parity (docs/FEATURE-GAP-2026.md): Metalink v4/v3 + RFC 6249 + sidecars + piece repair, multi-source parallel, cookies/headers, SFTP, WebDAV, RSS/Atom feeds, auto-extract, ffmpeg convert, filename tokens, sequential, JSON-RPC remote (HTTP+WS), Feeds page + Advanced add-dialog (EN/FA), extension cookie pass-through 1.3.0. 127/127 C# (+19) + 8/8 Flutter + 0 analyze; smoke + parity 12/12 + clean-env PASS. |
| v1.4.0 | 2026-10-24 | RELEASED (Linux x64) | BitTorrent core: bencode, .torrent+magnet, HTTP/UDP trackers (BEP-3/15), DHT (BEP-5), peer wire + BEP-10/9 ut_metadata, rarest-first/sequential pickers, resume bitfield, seeding, multi-file split; magnet UI + extension support 1.4.0. 160/160 C# (+33) + 8/8 Flutter + 0 analyze; loopback swarm E2E (own seeder + local tracker) byte-exact. |
| v1.4.1 | 2026-10-25 | RELEASED (Linux x64) | BitTorrent polish: PEX (BEP-11) + ut_pex handshake, DHT peer-store serving (announce→values, find_node, lifelong DHT loop), per-file selection in multi-file torrents (piece mask + sidecar + UI checkboxes), UDP tracker scrape (BEP-15 action=2), super-seeding (BEP-16) per-task. 169/169 C# (+9) + 8/8 Flutter + 0 analyze; smoke + parity 12/12 + clean-env PASS. |
| v1.5.0 | 2026-10-25 | RELEASED (Linux x64) | G-track: partial video preview server (loopback, token-auth, contiguous-prefix clamp, Range/HEAD) + Preview dialog (system-player handoff, live availability), yt-dlp optional bridge (analyze/resolve → normal engine pipeline; DRM/live refused), per-server performance profiles (EWMA speed/TTFB/success, SQLite persistence, fastest-first mirror ordering, Settings table), TLS client certificates (PFX/PEM/store bindings via LocalCertificateSelectionCallback), Metalink PGP verification (BouncyCastle, <signature type=pgp> + .sig/.asc sidecars, off/warn/require policy). Migration v10. 195/195 C# (+26) + 13 Flutter + 0 analyze; smoke + parity 12/12 + clean-env PASS. (2026-10-28 correction: the previously-claimed Dropbox upload/round-trip of the tarball never happened — see builds table.) |
| v1.6.0 | 2026-10-29 | RELEASED (Linux x64 + Windows engine x64) | Completeness round + first Windows deliverable (see table above for details). |
| v1.0.2 | — | NOT STARTED | Reserved for user-test feedback fixes. |

## Version floor logic
- Trusted sources: this file + git HEAD + Dropbox /Nova-Download-Manager/state/VERSIONS.md + CHANGELOG.md.
- Floor = highest trusted version present across sources. Working below floor is forbidden.
- After any reset: compare local VERSIONS.md vs the Dropbox mirror; converge to the newest, then continue.

## Builds published to Dropbox (/Nova-Download-Manager/builds/)
Cloud pruned 2026-10-28 per the user's space directive (CLOUD-POLICY.md): keep only current + previous release assets. Rows below record publication history; entries no longer in the cloud are marked [removed].

| Artifact | Version | Cloud status (2026-10-29) |
|----------|---------|---------------------------|
| **nova-dm-1.7.1-linux-x64.tar.gz (47 MB, sha256 727f7b78…90aaa)** | v1.7.1 | PUBLISHED 2026-10-29 (clean-env PASS) |
| **nova-engine-1.7.1-win-x64.zip (101 MB, sha256 45562fcb…a40c…)** | v1.7.1 | PUBLISHED 2026-10-29 |
| nova-chromium-1.7.1.zip / nova-firefox-1.7.1.zip (+ .sha256) | v1.7.1 | PUBLISHED 2026-10-29 |
| **nova-dm-1.7.0-linux-x64.tar.gz (48,446,039 B, sha256 f6a5a596…13e9a690)** | v1.7.0 | PUBLISHED 2026-10-29 (clean-env PASS) |
| **nova-engine-1.7.0-win-x64.zip (105,026,000 B, sha256 199be705…9fcd485)** | v1.7.0 | PUBLISHED 2026-10-29 |
| nova-chromium-1.7.0.zip / nova-firefox-1.7.0.zip (+ .sha256) | v1.7.0 | PUBLISHED 2026-10-29 |
| nova-engine-1.6.0-win-x64.zip (101 MiB, sha256 96be7baf…60970e) | v1.6.0 | PRESENT (previous release — keep per policy) |
| nova-dm-1.6.0-linux-x64.tar.gz (47 MiB, sha256 1b730550…d0ca21) | v1.6.0 | [removed 2026-10-29] superseded per keep-2 |
| nova-chromium-1.6.0.zip / nova-firefox-1.6.0.zip (+ .sha256) | v1.6.0 | [removed 2026-10-29] superseded per keep-2 |
| nova-chromium-1.5.0.zip / nova-firefox-1.5.0.zip (+ .sha256) | v1.5.0 | [removed 2026-10-29] superseded by v1.7.0 (current) + v1.6.0 (previous) per keep-2 |
| nova-dm-1.4.1-linux-x64.tar.gz (48,266,349 B, sha256 fe34c36a7527493b37105583254cdbb2149fe8c97e5bbd79b93608ebb1c1c384) | v1.4.1 | [removed 2026-10-29] was previous-release; superseded by v1.6.0 (current) + v1.5.0 zips (previous) per CLOUD-POLICY keep-2 |
| nova-chromium-1.4.1.zip / nova-firefox-1.4.1.zip | v1.4.1 | [removed 2026-10-29] same policy |
| nova-dm-1.5.0-linux-x64.tar.gz | v1.5.0 | NOT IN CLOUD — never was (T-019 correction); v1.6.0 tarball now supersedes it |
| nova-dm-1.0.1 / 1.1.0 / 1.2.0 / 1.3.0 / 1.4.0 tarballs; 1.2.0 / 1.4.0 extension zips | ≤ v1.4.0 | [removed 2026-10-28] were published 2026-10-23/24 (sha256 recorded in git history of this file) |
| evidence-*.png / evidence-*-vlm.json | v1.0.1–1.2.0 | [removed 2026-10-28] QA evidence — not resume-essential |
