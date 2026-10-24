# RESUME-POINT — atomic continuation contract

updated: 2026-10-24 (T-015 v1.3.0 "Beyond IDM — Market Parity" complete — shipped)
task: NONE OPEN — awaiting user feedback / next directive (v1.4.0 flagship = BitTorrent)
substep: —

done (T-015, this session, after env reset + toolchain rebuild):
- Research: docs/FEATURE-GAP-2026.md — 28-row matrix vs FDM/JDownloader/aria2/Motrix/Persepolis/uGet/NDM + RFCs; 16 items IMPLEMENTED in v1.3.0, 5 EXCLUDED (captcha, hoster plugins, DLC, …), BT = v1.4.0 flagship, rest roadmap
- Engine: Metalink.cs (v4/v3 XML + RFC 6249 Link/Digest + sidecars + FilenameTokens + CookieFile), Hashing.cs (algo-qualified sha256/sha1/md5/crc32 + PieceHashInfo), FeedService.cs, ArchiveTools.cs (extract + convert), SftpTask.cs (SSH.NET), WebDav.cs
- TaskRunner: multi-source SourcePool (round-robin + strikes), sequential picker, metalink/sidecar expansion in analyze, piece verify + repair, generalized hash verify, cookies/headers via ApplyTaskHeaders (probe + segments + vault retry + repair fetches)
- Schema v7: task columns (cookies/extra_headers/sequential/expected_hash/piece_hashes/convert_preset) + feed_rule + feed_seen; repo mapping updated
- IPC: new messages (SaveFeedRuleRequest, RunFeedRequest, DeleteFeedRuleRequest, ImportCookiesRequest, TaskPostProcessRequest), router cases (list/save/delete_feed_rule, run_feed, import_cookies, dav_list, task_postprocess), ToSpec + update_task extended; TaskSnapshot +6 fields
- EngineHost: JsonRpcServer.cs (JSON-RPC 2.0 HTTP+WS on 127.0.0.1:16801, token, port-walk on collision, method map 24 cmds), FeedService wired, rpc settings
- UI: feeds_page.dart (new), shell nav + switch, settings sections (remote control/integrity/post-processing), Advanced add-dialog (cookies/headers/hash/sequential), strings EN+FA (~47 new keys)
- Extensions: opt-in cookie pass-through (protocol.ts, background.ts cookieHeaderFor, options page checkbox, cookies permission), rebuilt + re-zipped 1.3.0
- Release: versions 1.3.0 everywhere; engine/browserhost/cli/self-contained published; package_release.sh VER bump; tarball sha256 73ca0059478f2818…; smoke + parity 12/12 + clean-env PASS
- Gotchas fixed en route: publish with /p:AssemblyName leaks into restore graph (ambiguous project name) — publish WITHOUT /p; single-file publish poisons runtimeconfig for FDD (separate PublishDir); NOT NULL DEFAULT columns reject explicit NULL binds; HttpListener is disposed after failed Start (recreate per attempt); XDocument namespace-safe lookups for Atom/propfind; %ext% has no leading dot

next:
1. USER: test v1.3.0 (download/nova-dm-1.3.0-linux-x64.tar.gz) → feedback
2. v1.4.0 BitTorrent core (bencode, .torrent+magnet, BEP-3/15 trackers, BEP-5 DHT, BEP-9/10, piece picker, seed mode; verify via local tracker + own seeder)
3. Partial-file preview player; optional yt-dlp bridge; per-server perf profiles; client certs; metalink PGP
4. Any new session: read STATE.md → this file → worklog.md tail → continue

evidence:
- sha256(nova-dm-1.3.0-linux-x64.tar.gz) = 73ca0059478f2818… (full in VERSIONS.md)
- 127/127 dotnet; 8/8 flutter; smoke/parity/clean-env PASS; JSON-RPC live round-trip

blockers: none
recovery: RECOVERY.md
