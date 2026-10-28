# RESUME-POINT — atomic continuation contract

updated: 2026-10-29 (T-020 — v1.6.0 completeness + first Windows deliverable)
task: NONE OPEN — awaiting user feedback / next directive
substep: —

done (T-020, this session):
- Toolchain rebuilt per RECOVERY.md §2: dotnet 8.0.427 (sdk/dotnet), flutter 3.47.7 (sdk/flutter),
  GTK3-dev (~/tools/gtk3-dev), cmake+ninja (~/.venv), clang-19 (~/tools/llvm-19 — note: extracted
  tree has clang-19 without a `clang` symlink; create symlinks or export PATH to the versioned bin).
- Fixed the two pre-reset compile errors in src/Nova.Engine/YtDlp/YtDlpRunner.cs (AddRange on
  Collection<string> → AddArgs extension; missing using System.Text.Json.Serialization) — the suite
  could not even build before this.
- v1.6.0 features (the "make it more complete" list):
  * yt-dlp native-merge COMPLETED end-to-end: the runner existed but nothing could trigger it.
    ytdlp_download IPC now takes native/audio_format_id/total_bytes; router builds a
    YtDlpNativeSelection (snake_case Encode fix) and hands the PAGE url to yt-dlp itself
    (--newline --progress, --merge-output-format mp4/mkv for a+b specs, cookies/referer,
    .part kept on pause, cleaned on cancel). UI: merge-only formats selectable + native badge
    (EN/FA string ytdlpNativeBadge).
  * DHT peer-store persistence: migration v11 (dht_peer_store), DhtClient.Export/ImportPeerStore,
    DhtPeerStoreRegistry (static, configured by EngineHostApp with Nova.Persistence.DhtPeerStoreDb),
    sessions warm-start + flush every ~6 min + on dispose.
  * Handshake flag bits (uTP/PEX item): OurReserved now BEP-10 + BEP-5 DHT (both implemented;
    FAST/uTP NOT claimed — honest advertisement), peer's reserved bytes parsed both directions into
    PeerSupportsDht/PeerSupportsExtensionProtocol/PeerSupportsFast; v string "Nova DM 1.6.0".
  * extensions 1.6.0 (build.mjs reads the version from the manifest now); Directory.Build.props → 1.6.0.
- Windows deliverable (user's standing request): dotnet publish -r win-x64 --self-contained for
  EngineHost + BrowserHost + CLI → nova-engine-1.6.0-win-x64.zip (101 MiB; SHA256SUMS inside;
  installer/windows/register-nativehost.ps1 + README-WINDOWS.md included).
  .github/workflows/windows-build.yml = one-push CI for the FULL Windows product (engine tests on
  windows-latest + flutter build windows + extensions + Inno Setup + artifacts + release on v* tags).
  User needs only: create a GitHub repo, push source, run the workflow (no secrets).
- Tests: C# 195 → 208 (+13 V160Tests); full suite 207/208 — Validator_Change_Restarts_Download
  fails ONLY as a timing flake on this 2-CPU sandbox (throttled loopback, 15 s window; passes/fails/
  passes on identical code, isolated runs green; HTTP path untouched by this round). Flutter:
  13 tests + 0 analyze errors.
- Linux v1.6.0 tarball rebuilt: nova-dm-1.6.0-linux-x64.tar.gz (47 MiB, sha256 1b730550e9d46312…d0ca21).
- Cloud (per CLOUD-POLICY): v1.6.0 assets uploaded to builds/ (win zip, linux tarball, ext zips + .sha256);
  v1.4.1 tarball + v1.4.1 ext zips pruned (keep current+previous). worklog.md re-restored from /state/
  after the reset wiped it, T-020 appended.

next:
1. USER: grab builds/nova-engine-1.6.0-win-x64.zip from Dropbox — engine + browser extensions +
   CLI run on Windows today; for the GUI, push the repo to GitHub and run "Nova Windows Build"
   (or run the workflow on any Windows host) → installer artifact.
2. Report Windows runtime issues → v1.0.2-style hotfix lane.
3. Future: embedded player (libmpv on Windows), uTP transport, tray, store publishing.
4. Any new session: read STATE.md → this file → worklog.md tail → RECOVERY.md (if reset) → continue.

evidence:
- dotnet test Nova.sln → 207/208 (documented flake); flutter test → 13 pass; flutter analyze → 0 errors.
- publish/win-x64/{engine,browserhost,cli}/*.exe produced from Linux; nova-engine-1.6.0-win-x64.zip
  (sha256 96be7bafbbd3dd6811bc765ce4ae246382d1b34a9bb705cb5b8c72aadc60970e) + SHA256SUMS.
- nova-dm-1.6.0-linux-x64.tar.gz (sha256 1b730550e9d46312eec4e0b71180fec88bd5c0e1937ea3f98ac9c00e41d0ca21).
- Cloud uploads verified by dropbox.py list after upload.

blockers: none (Windows GUI build = GitHub Actions or any Windows host; engine side delivered)
recovery: RECOVERY.md
