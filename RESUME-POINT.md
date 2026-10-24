# RESUME-POINT — atomic continuation contract

updated: 2026-10-25 (T-017 v1.4.1 "BitTorrent polish" complete — shipped)
task: NONE OPEN — awaiting user feedback / next directive (v1.5.0 = G-item track)
substep: —

done (T-017, this session, after env reset + full toolchain rebuild):
- Environment re-converged per RECOVERY.md: .NET 8.0.426 + Flutter 3.47.8 (exact) + GTK 3.24.49 + clang 19.1.7 (symlinked clang/clang++) + cmake/ninja via pip; 160/160 baseline re-verified before new work
- PEX (BEP-11): PexMessage.cs (codec: added/added.f/dropped, TryParseCompact port-0 filter, PexPeerState diff bookkeeping); ut_pex=2 in extension handshake + v/reqq parsing; session BroadcastPexAsync (30s diffs) + TryHandlePexAsync into _discovered; RemovePeer cleanup
- DHT peer-store (BEP-5): announce_peer stores (token-validated, implied_port, 64/hash, 30-min TTL), get_peers serves values + closest-nodes fallback, find_node implemented, StoredPeerCount/HasTokenFor test hooks; session DHT loop now lifelong (get_peers + announce every 90s)
- Per-file selection: TorrentMeta.PieceMaskForFiles/SelectedFileBytes; PiecePicker.SetWanted gates picking/completion/remaining; sidecar carries wanted mask (backward-compatible) + resume re-derives file set; SplitMultiFileAsync extracts only selected; selected totals on task; TorrentTask.ParseSelectedFiles CSV
- UDP scrape (BEP-15): UdpTrackerClient.ScrapeAsync (1..74 hashes, connection-id reuse, error action)
- Super-seeding (BEP-16): SuperSeedPeerState, AssignSuperSeedPieceAsync (drip unrevealed piece via HAVE, no bitfield), RevealSuperSeedPieceAsync (confirm → swarm-wide HAVE → next piece), round-robin fallback; SeedLoopAsync handles MsgHave in superseed
- Integration: task.SelectedFiles/task.SuperSeed → spec → session (per-task, like seedAfter); migration v9 (selected_files, super_seed); repo mapping; AddDownloadRequest/Spec/Snapshot +2 fields; router wiring
- UI: core/torrent_meta.dart (minimal bencode reader); Add dialog: .torrent file checkbox list (name+size), super-seed toggle under seeding toggle, seeding toggle extended to .torrent files; strings EN+FA (+7 keys); TaskUi +selectedFiles/+superSeed
- Tests: TorrentV141Tests (+9) — PEX codec roundtrip + malformed tolerance, wire-level ut_pex routing (accept-then-handshake order fixed the earlier deadlock), DHT announce→values serve + bad-token reject, UDP scrape multi-hash, piece mask mapping, ParseSelectedFiles CSV, multi-file selection E2E (only file_0 extracted, byte-exact), super-seed E2E (drip logs + byte-exact)
- Bugs fixed en route: PEX registration parsed endpoints cleanly (ParseEndpoint); SendMessageInternalAsync/ParseSelectedFiles made public for tests; multi-file harness "path" must be bencoded LIST; scrape harness needs hex-string keys (byte[] ref-equality); client must READ the peer's ext handshake before its extensions are known
- Release: VER bumps (package_release.sh, pubspec 1.4.1+5, manifests 1.4.1, build.mjs zip names, cleanenv test); engine/browserhost/cli/self-contained published; smoke PASS (bundle needs libe_sqlite3.so copied), parity 12/12, tarball sha256 fe34c36a…c1c384 round-trip verified; CHANGELOG/VERSIONS updated; Dropbox uploads (tarball + 2 extension zips)

next:
1. USER: test v1.4.1 (download/nova-dm-1.4.1-linux-x64.tar.gz) → feedback
2. v1.5.0 candidates: partial-file player preview (G17); yt-dlp optional bridge (G28); per-server perf profiles (G27); client certs (G25); metalink PGP (G26)
3. BT extras when asked: honor PEX flag byte (uTP preference), DHT peer-store persistence across restarts, per-file prioritized piece picking (first/last piece prefetch)
4. Any new session: read STATE.md → this file → worklog.md tail → continue

evidence:
- sha256(nova-dm-1.4.1-linux-x64.tar.gz) = fe34c36a7527493b37105583254cdbb2149fe8c97e5bbd79b93608ebb1c1c384
- 169/169 dotnet; 8/8 flutter; 0 analyze errors; smoke/parity 12/12/clean-env PASS; Dropbox round-trip verified

blockers: none
recovery: RECOVERY.md
