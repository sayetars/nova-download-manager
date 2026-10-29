# RESUME-POINT — atomic continuation contract

updated: 2026-10-29 (T-022 — v1.7.1 free-forever policy round)
task: NONE OPEN — awaiting user feedback / next directive
substep: —

done (T-022, this session):
- Free-forever policy (user: «برنامه نباید برای هیچ قسمتیش نیاز ب پرداخت هزینه باشه»):
  * docs/FREE-FOREVER.md (EN+FA hard invariant, 8 commitments + enforcement table);
    THIRD-PARTY-NOTICES.md (13 recorded dependencies, all $0/free; platforms/tools;
    banned list; add-dependency checklist — also the machine allowlist).
  * scripts/free_audit.py CI gate (dependency allowlist + banned paid terms with
    negation-context allowance; historical files exempt) wired into
    .github/workflows/version-audit.yml. First run caught unrecorded SSH.NET → recorded.
  * Azure Trusted Signing (~$9.99/mo) REJECTED (DECISIONS D-013): VERSIONING §8
    rewritten to the free integrity path (SHA-256 sidecars + optional self-signed
    recipe); KNOWN-LIMITATIONS #37 updated; STATE/RESUME-POINT roadmaps cleaned.
  * Root LICENSE (first time) + installer/LICENSE.txt guarantee clause; both release
    packages now ship LICENSE + THIRD-PARTY-NOTICES.md + docs/FREE-FOREVER.md
    (package scripts updated).
  * User-facing: Settings → Versions "Nova is 100% free…" line + extension options
    About freeNote — both EN/FA.
- Recovery this session: post-reset — Dropbox creds file recreated (account verified);
  worklog restored via restore.py state; toolchain rebuilt per RECOVERY.md
  (dotnet 8.0.427, flutter 3.47.7/abaf9c5237, GTK3 user-space, llvm-19.1.7 + unversioned
  symlinks, venv cmake/ninja); rules read + ledger entry before work.
- Release-critical fix: first v1.7.1 linux tarball had a broken engine (missing
  top-level libe_sqlite3.so — package_release.sh copies FDD files only); fixed by
  staging the native lib into the flutter bundle; tarball rebuilt; clean-env PASS.
- v1.7.1 released: version.json 1.7.1 (build 8), 10 surfaces synced; dotnet 242/242;
  flutter 13 + 0 analyze errors; sync --check PASS; free audit PASS; nova status
  1.7.1/stable/11; bundle smoke PASS; clean-env tarball PASS; git commit 8ac1ecd,
  tag v1.7.1.
- Assets: nova-dm-1.7.1-linux-x64.tar.gz (sha256 727f7b78bf200389c2d86c23c967d48d298989ff3fea829850e7babce190aaa),
  nova-engine-1.7.1-win-x64.zip (sha256 45562fcb495641e2564e09d59d4f3c6a57970f67bc8ffcd3edc3bfdbdf1da40c),
  ext zips + .sha256 (chromium/firefox 1.7.1).

next:
1. USER: test nova-engine-1.7.1-win-x64.zip on Windows (engine + extensions + CLI;
   Settings/About show the free-forever line) and/or push to GitHub →
   windows-build.yml (audit + free-forever gates run first) for the full installer.
2. Windows feedback → v1.7.2 PATCH lane (bump version.json → sync → test → tag).
3. Future (all free): tray, embedded player, CWS/AMO store publishing, FFmpeg
   minimum-version policy in the yt-dlp bridge — paid services are BANNED (D-013).

evidence:
- dotnet test Nova.sln → 242/242 (BrowserPath tests after building EngineHost + BrowserHost -c Release).
- flutter analyze → 0 errors; flutter test → 13 passed + 1 skipped.
- python3 scripts/sync_versions.py --check → 10/10 + cross-checks PASS.
- python3 scripts/free_audit.py → PASS (13 deps, $0/free).
- nova status (shipped FDD engine, staged with runtimes) → engine_version 1.7.1,
  channel stable, db_schema_version 11, min_engine_for_extension 1.7.1.
- scripts/linux/smoke_test.sh PASS; scripts/linux/tarball_cleanenv_test.sh PASS
  (byte-exact end-to-end from the shipped tarball).
- Artifact sha256 values: VERSIONS.md builds table + CHANGELOG [1.7.1].

blockers: none (Windows GUI build = GitHub Actions or any Windows host; engine side delivered)
recovery: RECOVERY.md
