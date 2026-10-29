# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.7.1
CURRENT TASK: T-022 (free-forever policy round) COMPLETE — v1.7.1 shipped: "no payment for any part of the program" is now a hard CI-enforced invariant (user directive «برنامه نباید برای هیچ قسمتیش نیاز ب پرداخت هزینه باشه»); docs/FREE-FOREVER.md (EN+FA) + THIRD-PARTY-NOTICES.md (13 deps, all $0/free — the machine allowlist) + root LICENSE; scripts/free_audit.py gate in version-audit.yml (dependencies must be recorded free; banned paid terms in code/active docs); Azure Trusted Signing (~$9.99/mo) REJECTED (D-013) → VERSIONING §8 = free integrity path (SHA-256 sidecars + optional self-signed); Settings→Versions + extension About show the bilingual free-forever line; both release packages ship LICENSE + NOTICES + FREE-FOREVER. Release-critical fix inside the session: first tarball's engine missed the top-level libe_sqlite3.so → caught by smoke, fixed, rebuilt, clean-env PASS. Tests: C# 242/242; Flutter 13 + 0 analyze errors; sync --check PASS; free audit PASS; nova status 1.7.1/stable/schema 11. Assets: nova-dm-1.7.1-linux-x64.tar.gz (47 MB, sha256 727f7b78…90aaa), nova-engine-1.7.1-win-x64.zip (101 MB, sha256 45562fcb…da40c), ext zips 1.7.1 (+.sha256). Toolchain rebuilt this session: dotnet 8.0.427 + flutter 3.47.7 (abaf9c5237) + GTK3 + llvm-19.1.7 + venv cmake/ninja.
CURRENT SUBSTEP: —
DONE: v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.0 / v1.4.1 / v1.5.0 / v1.6.0 / v1.7.0 / v1.7.1 releases (VERSIONS.md); T-022 (this session).
NEXT: (1) USER: test nova-engine-1.7.1-win-x64.zip on Windows (free-forever line visible in Settings/extension About) and/or push to GitHub → windows-build.yml (audit + free gates first) for the full installer; (2) Windows feedback → v1.7.2 PATCH lane; (3) future (all free): tray, embedded player, store publishing — paid services BANNED (D-013, docs/FREE-FOREVER.md).
BLOCKERS: none (Windows GUI build = GitHub Actions or any Windows host — engine side delivered)
LAST VERIFIED: 2026-10-29 — dotnet test 242/242; flutter analyze 0 errors / 13 tests; sync --check 10/10; free audit PASS; nova status = 1.7.1/stable/11; smoke + clean-env tarball PASS; win-x64 zip layout verified (self-contained, top-level e_sqlite3.dll).
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain REBUILT this session after reset (sdk/dotnet 8.0.427, sdk/flutter 3.47.7 abaf9c5237, ~/tools/gtk3-dev + ~/tools/llvm-19 + unversioned clang symlinks, ~/.venv cmake/ninja); no root; no background process survival between tool calls; 2 CPUs. publish/ + download/ hold the v1.7.1 build outputs (untracked, local only). Dropbox creds present (tools/dropbox/.dropbox-credentials.json, 600).
```

## DONE (high level)
- v1.0.x → v1.7.0 releases (see VERSIONS.md).
- v1.6.0 (T-020): yt-dlp native-merge, DHT peer-store persistence, honest handshake flags, first Windows engine deliverable + CI.
- v1.7.0 (T-021): version integrity (SSOT + CI + runtime gates + legacy purge) + AES-128 HLS + structured update checks.

## NEXT
1. USER: grab builds/ v1.7.0 assets from Dropbox — test the Windows engine zip; push the repo to GitHub and run "Nova Windows Build" (version-audit gate runs first) for the full installer.
2. Report Windows runtime issues → v1.7.1 PATCH lane (bump version.json → sync → test → tag).
3. Future candidates: tray on Windows, embedded player (libmpv decision), CWS/AMO store publishing, FFmpeg minimum-version policy in the yt-dlp bridge — Trusted Signing REJECTED per free-forever policy (D-013, docs/FREE-FOREVER.md).

## LAST VERIFIED STATE
- Evidence: this session's command outputs (dotnet test 242/242; flutter analyze/test; sync --check; nova status; smoke_test.sh; tarball_cleanenv_test.sh; sha256 of every artifact in CHANGELOG 1.7.0 + VERSIONS builds table).
- Cloud: v1.7.0 assets uploaded + v1.5.0 ext zips pruned per keep-2 (current 1.7.0 + previous 1.6.0); checkpoint via backup.py; PROJECT-TREE regenerated from the exact backup set.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md (T-021 entry recorded 2026-10-29).
