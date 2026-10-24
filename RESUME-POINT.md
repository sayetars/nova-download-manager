# RESUME-POINT — atomic continuation contract

updated: 2026-10-24 (T-016 v1.4.0 "BitTorrent core" complete — shipped)
task: NONE OPEN — awaiting user feedback / next directive (v1.4.x = BT polish track)
substep: —

done (T-016, this session, after env reset + toolchain rebuild):
- Environment reconverged per RECOVERY.md: .NET 8.0.426 + Flutter 3.47.7 (exact) + GTK 3.24.49 + clang 19.1.7 (symlinked) + cmake/ninja; 127/127 baseline re-verified before any new work
- Engine: src/Nova.Engine/Torrent/{Bencode, TorrentMeta(+Creator), Trackers (BEP-3 HTTP + BEP-15 UDP), DhtClient (BEP-5), PeerWireClient (BEP-3/10/9), PiecePicker, TorrentSession, TorrentTask(+SeederRegistry)}.cs
- Integration: DownloadKind.Torrent; magnet:/+local .torrent URL admission; TaskRunner dispatch before probe; migration v8 (bt_info_hash, seed_after); repo mapping; TaskSnapshot +2 fields; ToSpec seed_after; bt_dht/bt_upload_cap/bt_listen_port/bt_listen/bt_metadata_timeout settings
- Tests: TorrentProtocolTests (24: bencode vectors, binary-safe keys, metainfo, multi-file path sanitize, magnet hex/base32, loopback HTTP+UDP trackers, picker, DHT two-node KRPC, wire framing, ut_metadata), TorrentE2eTests (6: byte-exact download via own seeder+tracker, deterministic resume from pre-seeded bitfield, magnet metadata via ut_metadata, 3-downloader fan-out, seed-back upload, corrupt-temp re-verify), TorrentEngineTests (3: engine-level .torrent task, magnet task, snapshot fields) — serialized via [Collection("TorrentSerial")]
- Bugs found & fixed en route (all test-covered): extension-message length prefix inverted (regular messages lost their id byte); REQUEST payload length misread as payload.Length-8 instead of BE field; block released on arrival (duplicate traffic); ut_metadata prefix walker grammar-blind (binary d/e bytes); DhtClient unbound socket (port 0 → EINVAL); announce-list encoded flat instead of tiered; sidecar recreated after FinishAsync deleted it; metadata_size advertised after the handshake was already sent; HttpTracker read binary peers as string
- UI: magnet banner + seed-after toggle in Add dialog; magnet acceptance in URL validation; clipboard + drop magnet/.torrent; TaskUi btInfoHash/seedAfter; strings EN/FA (+4 keys)
- Extensions: magnet links collected in ALL-links dialog (both); 1.4.0 manifests; rebuilt + rezipped
- Release: versions bumped (pubspec 1.4.0+4, manifests, scripts VER); tarball sha256 e61360c39a1aeb1e698ae55e283eaee9a67f9cffca7fe1a74860130c6f8cf772; engine+browserhost+cli published; smoke + parity 12/12 + clean-env PASS; Dropbox uploads + round-trip verified; docs updated (CHANGELOG, VERSIONS, DOWNLOAD-ENGINE, IPC-PROTOCOL, FEATURE-GAP-2026, KNOWN-LIMITATIONS)

next:
1. USER: test v1.4.0 (download/nova-dm-1.4.0-linux-x64.tar.gz) → feedback
2. v1.4.x BT polish: PEX (BEP-11), DHT peer-store serving, per-file selection in multi-file torrents, UDP tracker scrape, super-seeding
3. v1.5.0 candidates: partial-file player preview (G17); yt-dlp optional bridge (G28); per-server perf profiles (G27); client certs (G25); metalink PGP (G26)
4. Any new session: read STATE.md → this file → worklog.md tail → continue

evidence:
- sha256(nova-dm-1.4.0-linux-x64.tar.gz) = e61360c39a1aeb1e698ae55e283eaee9a67f9cffca7fe1a74860130c6f8cf772
- 160/160 dotnet; 8/8 flutter; 0 analyze errors; smoke/parity/clean-env PASS; Dropbox round-trip verified

blockers: none
recovery: RECOVERY.md
