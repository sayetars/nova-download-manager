# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.7.0
CURRENT TASK: T-021 (version-integrity round + AES-128 HLS + round-5 research) COMPLETE — v1.7.0 shipped: version.json is the SINGLE SOURCE OF TRUTH (10 stamped surfaces + 3 cross-checks via scripts/sync_versions.py; CI audit gate: version-audit.yml + windows-build.yml `needs: audit`); runtime gates (boot schema fail-fast in EngineHostApp.LoadDb, truthful engine_status with engine_version/channel/db_schema_version/min_engine_for_extension, extension EN/FA options banner vs stamped floor, Flutter Settings→Versions card); Nova.Domain.VersionInfo + SemVersion wired everywhere the literals used to be (EngineStatusResponse was hardcoded "1.5.0", UAs were 1.0–1.3, BT v-string was a 1.6.0 literal); check_update parses a JSON update manifest with SemVer compare; AES-128 HLS downloaded+decrypted per RFC 8216 (EXT-X-KEY parse, explicit IV + sequence-IV fallback, mixed playlists; DRM/SAMPLE-AES still refused); legacy nova/ tree (73 MB, unreferenced) purged with history archived to docs/history/legacy-nova-tree/ (DECISIONS D-012); missing git tags v1.5.0/v1.6.0 recreated. Tests: C# 208→242 ALL PASS (0 flakes this run); Flutter 13 + 0 analyze errors; sync --check 10/10 PASS; engine boot sanity via `nova status` (1.7.0/stable/schema 11); bundle smoke PASS; clean-env tarball PASS. Assets rebuilt: nova-dm-1.7.0-linux-x64.tar.gz (48,446,039 B, sha256 f6a5a596…), nova-engine-1.7.0-win-x64.zip (105,026,000 B, sha256 199be705…), ext zips 1.7.0 (+.sha256). Toolchain: dotnet 8.0.427 + flutter 3.47.7 (sdk/, env.sh) — no rebuild needed this session.
CURRENT SUBSTEP: —
DONE: v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.0 / v1.4.1 / v1.5.0 / v1.6.0 / v1.7.0 releases (VERSIONS.md); T-021 (this session).
NEXT: (1) USER: test nova-engine-1.7.0-win-x64.zip on Windows (engine+extensions+CLI; AES-128 HLS sites now work) and/or push to GitHub → windows-build.yml (audit gate runs first) for the full installer; (2) Windows feedback → PATCH lane; (3) future: Azure Trusted Signing (~$9.99/mo — recipe in docs/VERSIONING.md §8), tray, embedded player, extension store publishing (MV3 review notes in worklog T-021).
BLOCKERS: none (Windows GUI build = GitHub Actions or any Windows host — engine side delivered)
LAST VERIFIED: 2026-10-29 — dotnet test 242/242; flutter analyze 0 errors / 13 tests; sync_versions --check 10/10; nova status = 1.7.0/stable/11; smoke + clean-env tarball PASS; win-x64 cross-compile + packaging verified (binaries produced; execution needs a Windows machine).
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain intact from T-020 — sdk/dotnet (8.0.427), sdk/flutter (3.47.7), ~/tools/gtk3-dev + ~/tools/llvm-19, ~/.venv (cmake/ninja); no root; no background process survival between tool calls; 2 CPUs; disk freed by purging nova/ + stale stage dirs (was 89% full → 85%). publish/ + download/ present (untracked, local testing only).
```

## DONE (high level)
- v1.0.x → v1.7.0 releases (see VERSIONS.md).
- v1.6.0 (T-020): yt-dlp native-merge, DHT peer-store persistence, honest handshake flags, first Windows engine deliverable + CI.
- v1.7.0 (T-021): version integrity (SSOT + CI + runtime gates + legacy purge) + AES-128 HLS + structured update checks.

## NEXT
1. USER: grab builds/ v1.7.0 assets from Dropbox — test the Windows engine zip; push the repo to GitHub and run "Nova Windows Build" (version-audit gate runs first) for the full installer.
2. Report Windows runtime issues → v1.7.1 PATCH lane (bump version.json → sync → test → tag).
3. Future candidates: Azure Trusted Signing adoption (VERSIONING.md §8), tray on Windows, embedded player (libmpv decision), CWS/AMO store publishing, FFmpeg minimum-version policy in the yt-dlp bridge.

## LAST VERIFIED STATE
- Evidence: this session's command outputs (dotnet test 242/242; flutter analyze/test; sync --check; nova status; smoke_test.sh; tarball_cleanenv_test.sh; sha256 of every artifact in CHANGELOG 1.7.0 + VERSIONS builds table).
- Cloud: v1.7.0 assets uploaded + v1.5.0 ext zips pruned per keep-2 (current 1.7.0 + previous 1.6.0); checkpoint via backup.py; PROJECT-TREE regenerated from the exact backup set.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md (T-021 entry recorded 2026-10-29).
