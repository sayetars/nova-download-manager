# RESUME-POINT — atomic continuation contract

updated: 2026-10-25 (T-018 v1.5.0 "G-track" complete — shipped)
task: NONE OPEN — awaiting user feedback / next directive
substep: —

done (T-018, this session):
- Environment re-verified (no reset this time): dotnet 8.0.426 + Flutter 3.47.8 live via scripts/env.sh; baseline re-run 169/169 (2 pre-existing timing flakes on first run, clean on re-run — noted as 2-CPU load artifact)
- G17 partial preview: Nova.Engine/Preview/PreviewServer.cs (loopback 127.0.0.1 token-auth HTTP; GET/HEAD + Range incl. suffix ranges + 416 with Content-Range; unranged GET clamped to contiguous downloaded prefix computed from live plans or persisted segments; MIME map; FileShare.ReadWrite reader on the .novadl temp; completed files served read-only); IPC preview_start/preview_stop (lazy singleton in router); Flutter preview_page.dart dialog (live available/total from tasksProvider, copy URL, system-player handoff start/xdg-open/open, EN/FA); task-menu entry for previewable media states (downloading/paused/completed/interrupted)
- G28 yt-dlp bridge: Nova.Engine/YtDlp/YtDlpBridge.cs (binary discovery: setting ytdlp.path > explicit > PATH probe yt-dlp/yt-dlp_linux/youtube-dl; -J --no-playlist analyze with is_live + DRM (codec/protocol) refusal; format table with DirectDownloadable = progressive http(s) | audio-only http(s) | complete m3u8/dash via engine media pipeline; -f <id> -g resolve; stderr mapped to UI-safe reasons); IPC ytdlp_status/analyze/download (download forces Sequential for preview, referer=page, MediaSelection JSON provenance); Add-dialog quality panel (radio formats, needs-merge marking, preselect best direct, localized errors)
- G27 server profiles: Nova.Engine/ServerProfiles/ServerProfileService.cs (EWMA bps α=.25 capped samples ≥256 KiB, EWMA TTFB, ok/fail counts, bytes, last-active; 5s batched SQLite persistence via ServerProfileRepository; Score = bps·success²/(1+latency s); stable OrderMirrors fastest-first for unknown-hosts-last); TaskRunner hooks (probe TTFB+result, slice send TTFB+result, drain throughput incl. RecordSpeed also counting an ok sample); metalink mirrors reordered within same-priority buckets; IPC server_profiles (with active_now) / reset_server_profiles; Settings _ServerProfilesSection table + reset
- G25 client certs: Nova.Domain/Profiles.cs ClientCertBinding; Nova.Persistence client_cert table (migration v10, unique host_pattern, passphrase stored server-side only); ClientCertificateService (PFX/PEM-pair/store-thumbprint loading, 10s cached list, per-host most-specific wildcard/exact match, Validate() for pre-save checks); Configure(SocketsHttpHandler) via SslOptions.LocalCertificateSelectionCallback wired into BOTH EngineHostApp HttpClients (init order fixed: services before CreateHttpClient); IPC list/save/delete_client_certs (never echoes passphrase)
- G26 metalink PGP: BouncyCastle.Cryptography 2.7.0 added to Nova.Engine; PgpVerifier.VerifyDetached (armored/binary sig parse incl. compressed packets; multi-key keyring; Verified/NoKey/Invalid/Malformed), FingerprintOf, NormalizeSignaturePayload (base64-of-armored and plain); Metalink.ParseXml + <signature type=pgp>; TaskRunner VerifyPgpAsync (policy off|warn|require; returns should-abort on require-failure; .sig/.asc sidecar fetch best-effort) + task.PgpSignature/PgpStatus (migration v10 columns, repo mapping); PgpKeyringService + pgp_keyring table; IPC list/save/delete_pgp_keys; Settings security section (certs manager + PGP manager + policy dropdown)
- IPC: Messages.cs +8 request DTOs; EngineStatusResponse.EngineVersion default corrected 1.3.0→1.5.0; router +12 cases
- Flutter: models +pgpStatus/hasPgpSignature/isPreviewable; providers +previewStart/openPreview/ytdlpAnalyze/ytdlpDownload +5 providers; ~55 strings EN+FA; media_kit attempted then REVERTED (no system libmpv, no root; system-player handoff instead — KNOWN-LIMITATIONS #13)
- Tests: tests/Nova.Engine.Tests/V150Tests.cs (+26; heavy classes serialized with TorrentSerial collection); fixes en route: test .novadl filename suffix bug, fake yt-dlp script arg check, cert server needed CopyWithPrivateKey, SslStream server needed real HTTP response, cache-TTL overflow in ClientCertificateService (bool _cacheLoaded flag), RecordSpeed now counts an ok sample, video-only https formats are NOT DirectDownloadable (needs merge)
- Release: VER bumps (package_release.sh 1.5.0, pubspec 1.5.0+6, manifests 1.5.0, build.mjs zips, EngineVersion); FDD engine/browserhost/cli + self-contained engine published; flutter linux release; bundle prep (engine+browserhost+libe_sqlite3 copied in); smoke PASS; parity 12/12; package → download/nova-dm-1.5.0-linux-x64.tar.gz (48,397,691 B, sha256 14562b5549c85d7061ada91b2ecc02768a6fb36e5fb233faaf57ea359cccfbec); clean-env PASS; Dropbox uploads (tarball + 2 extension zips) with round-trip sha256 verified; source backup via tools/dropbox/backup.py (81 dirty files included); git commit ef6a00f

next:
1. USER: test v1.5.0 (download/nova-dm-1.5.0-linux-x64.tar.gz) → feedback
2. v1.5.x candidates: yt-dlp native-merge mode (video+audio), embedded player decision (libmpv), DHT peer-store persistence, uTP/PEX flag byte
3. Windows release path when a Windows host is available (see KNOWN-LIMITATIONS "Testing gaps")
4. Any new session: read STATE.md → this file → worklog.md tail → continue

evidence:
- sha256(nova-dm-1.5.0-linux-x64.tar.gz) = 14562b5549c85d7061ada91b2ecc02768a6fb36e5fb233faaf57ea359cccfbec
- 195/195 dotnet; flutter 13 passed + 1 skipped; 0 analyze errors; smoke/parity 12/12/clean-env PASS; Dropbox round-trip verified; git ef6a00f

blockers: none
recovery: RECOVERY.md
