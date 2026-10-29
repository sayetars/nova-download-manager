# RESUME-POINT — atomic continuation contract

updated: 2026-10-29 (T-021 — v1.7.0 version-integrity round + AES-128 HLS + round-5 research)
task: NONE OPEN — awaiting user feedback / next directive
substep: —

done (T-021, this session):
- Version integrity (user: "نسخه‌بندی یادت نره تا قاطی نشه نسخه‌ها باهم"):
  * version.json = SSOT; scripts/sync_versions.py stamps 10 surfaces
    (Directory.Build.props, generated src/Nova.Domain/VersionInfo.cs, pubspec, generated
    lib/core/build_version.dart, 2 extension manifests, installer.iss #define,
    MIN_ENGINE_VERSION in shared/protocol.ts + options.js, extensions/package.json)
    + 3 cross-checks (dbSchema == latest migration; protocol.ipc == Protocol.Version;
    no versioned artifact literals in workflows) — `--check` is the CI gate.
  * Drift fixed on entry: pubspec 1.5.0+6→1.7.0+7, installer 1.0.0→1.7.0,
    EngineStatusResponse default "1.5.0"→VersionInfo, UA literals 1.0–1.3→VersionInfo.UserAgent,
    BT v-string→VersionInfo.PeerWireClient, CI zip literals→wildcards, CI branch-list bug
    (`ain, master]`→`[main, master]`), packagers VER=1.6.0 hardcodes→version.json readers.
  * CI: .github/workflows/version-audit.yml (push/PR audit + tag==version.json on v*);
    windows-build.yml gained an `audit` job that `windows` needs.
  * Runtime: boot fail-fast (schema mismatch refuses to start); engine_status truthful
    (engine_version/channel/db_schema_version/min_engine_for_extension); extension options
    About section EN/FA with OK/outdated/offline banner + JS semver; Flutter Settings→Versions.
  * Nova.Domain.SemVersion (SemVer 2.0.0 subset, prerelease-aware) + tests.
  * check_update: JSON update manifest + SemVer compare (legacy passthrough kept).
  * Legacy purge: nova/ tree (73 MB, unreferenced, v0.1.0-era) + scripts/nova-sources.zip
    git-rm'd; narrative preserved in docs/history/legacy-nova-tree/ (DECISIONS D-012).
  * Git tags: v1.5.0 (90db0d9) + v1.6.0 (93ce179) recreated; v1.7.0 tagged on the release
    commit. docs/VERSIONING.md = full policy EN+FA (incl. Azure Trusted Signing recipe §8).
- AES-128 HLS (round-5 research headline): HlsParser full EXT-X-KEY semantics (method/URI/
  IV/KEYFORMAT, per-segment mapping, METHOD=NONE reset, dedupe, MEDIA-SEQUENCE,
  DrmProtected classification); HlsCrypt (AES-128-CBC + PKCS7 strip, IV=tag or
  big-endian sequence, 512 MiB guard); MediaPipeline decrypt-on-download path (key cache,
  mixed playlists, init passthrough); MediaCandidatesService marks AES-128 as supported.
- Tests: C# 208 → 242 (V170Tests: SemVersion, VersionIntegrity, HlsCrypt, HlsKeyParsing,
  EncryptedHlsE2e ×5; MediaTests AES test converted to SAMPLE-AES). Flutter 13 + 0 analyze.
- Assets rebuilt at v1.7.0: linux tarball (48,446,039 B, sha256 f6a5a5966ac6e78b501e9c0b73ea93c3c67b940e71566f1919f9416e13e9a690,
  clean-env PASS), win-x64 engine zip (105,026,000 B, sha256 199be705f41b03c70409e4898616b83a8dbfae022c9bc6569b3c772999fcd485),
  ext zips + .sha256 (chromium f334284f…, firefox afdcf5db…).
- Research round 5 (recorded in docs + worklog): Azure Trusted Signing ≈$9.99/mo
  individual path; AES-128/RFC 8216 details; FFmpeg 8.1 HLS fixes (min-version policy
  candidate); MV3 store notes; Orion native-messaging upstream bug → KNOWN-LIMITATIONS #36.

next:
1. USER: test the v1.7.0 Windows engine zip (Dropbox builds/) — AES-128 HLS sites, `nova
   status` version block, extension About banner; push to GitHub → windows-build.yml for
   the full installer (audit gate runs first).
2. Windows/extension feedback → v1.7.1 PATCH lane (edit version.json → sync → test → tag).
3. Future: tray, embedded player, store publishing (Trusted Signing REJECTED — free-forever policy D-013).

evidence:
- dotnet test Nova.sln → 242/242; flutter analyze → 0 errors; flutter test → 13 pass.
- python3 scripts/sync_versions.py --check → 10/10 surfaces + 3 cross-checks PASS.
- nova status (shipped FDD engine) → engine_version 1.7.0, channel stable, db_schema 11.
- scripts/linux/smoke_test.sh PASS; scripts/linux/tarball_cleanenv_test.sh PASS (env -i,
  byte-exact sha256).
- Artifact sha256 values: CHANGELOG [1.7.0] + VERSIONS.md builds table.

blockers: none (Windows GUI build = GitHub Actions or any Windows host; engine side delivered)
recovery: RECOVERY.md
