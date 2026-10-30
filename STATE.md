# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.8.0
CURRENT TASK: T-024 (IDM full-parity completion) COMPLETE — user directive («میخوام راجب قابلیت ها و امکانات internet download manager عمیق تحقیق و جست جو کنی و همه شون رو توی برنامه خودمون پیاده کنی»). Fresh primary-source research (official IDM features/options/scheduler/grabber/CLI pages) → docs/IDM-PARITY.md refreshed (63 rows). AUDIT-FIRST per D-014: discovered commit fdba99f already held 2,004 lines of never-compiled v1.8.0-start work (PAC/SystemProxy/sounds/tray/CategoryDirStore/HTTP/2/resume-ignore) under a misleading "docs" message → toolchain rebuilt, that code FIXED (corrupted FA string, missing imports, wrong didRequestAppExit signature), not rewritten. Closed every remaining implementable row: interactive 401/407 login dialog (probe+slice auth retry, provide_auth IPC, vault URL-prefix entries = IDM per-directory logins), per-queue retry defaults + lone-task infinite 30 s retry, per-queue open-file/run-program post actions, Grabber step-3/4 options (parent dirs/subdomains/external levels/size filters), Insert special key (IDM default), Windows tray + OLE drag-out, per-event sounds, progress-view modes, shown-fields chooser, autostart (HKCU Run / XDG), weekly auto-update. Fixed 3 latent bugs the new tests exposed: multi-segment resume restarting from zero since v1.1.0, Grabber result counters always 0, Grabber folder replication never working. 320/320 C#; Flutter 13 + 0 analyze; sync --check + free_audit PASS (Jint recorded); tarball clean-env PASS; v1.8.0 artifacts on Dropbox (builds keep-2). DECISIONS D-015.
CURRENT SUBSTEP: —
DONE: v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.x / v1.5.0 / v1.6.0 / v1.7.0 / v1.7.1 / v1.8.0 releases (VERSIONS.md); T-022 (free-forever); T-023 (nova/ purge); T-024 (this session).
NEXT: (1) USER: send GitHub repo (empty, any name) + PAT (classic scopes repo+workflow, or fine-grained Contents+Workflows RW) → session pushes main + tags → windows-build.yml builds the FULL Windows installer (audit gate first; engine + Flutter UI + extensions + Inno Setup; artifacts + auto-Release on v* tags — zero user secrets needed); (2) Windows real-machine feedback → v1.8.x PATCH lane; (3) future (all free): Linux tray + Linux drag-out (GTK runner work), embedded player, arbitrary non-modifier special keys, CWS/AMO store publishing — paid services BANNED (D-013, docs/FREE-FOREVER.md).
BLOCKERS: none (Windows GUI build = GitHub Actions; engine side delivered on Dropbox)
LAST VERIFIED: 2026-10-30 — dotnet test 320/320 (full suite; isolated re-runs confirm the 2-3 full-suite stragglers are the documented 2-CPU timing flakes); flutter analyze 0 errors / 13 tests; sync --check PASS; free_audit PASS; tarball clean-env PASS; Dropbox builds listing verified (1.8.0 current + 1.7.1 previous).
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: rebuilt 2026-10-30 for T-024 (sdk/dotnet 8.0.427 + sdk/flutter 3.47.7 restored from RECOVERY.md §2; GTK3-dev + LLVM-19 + cmake/ninja installed for the Linux UI build; Node present; venv python3 3.12 OK; no gh CLI). Dropbox creds at tools/dropbox/.dropbox-credentials.json (600). Root worklog.md restored from the cloud state mirror + T-024 entry appended.
```

## DONE (high level)
- v1.0.x → v1.8.0 releases (see VERSIONS.md).
- v1.8.0 (T-024): IDM full-parity completion — see CHANGELOG [1.8.0] + docs/IDM-PARITY.md + DECISIONS D-015.
- v1.7.1 (T-022): free-forever policy; v1.7.0 (T-021): version integrity + AES-128 HLS; T-023: nova/ parallel-tree purge + cloud cleanup.

## NEXT
1. USER: provide GitHub repo + PAT (repo+workflow scopes) → push main + tags → windows-build.yml (full Windows installer; audit gate first).
2. Windows test feedback → v1.8.x PATCH lane (bump version.json → sync → test → tag).
3. Future candidates (all free): Linux tray + Linux drag-out, embedded player (libmpv decision), arbitrary non-modifier special keys, CWS/AMO store publishing.

## LAST VERIFIED STATE
- Evidence: this session's command outputs (320/320 with binaries pre-built; isolated re-runs green; flutter analyze 0 errors / 13 tests; sync --check PASS; free_audit PASS; tarball clean-env PASS with byte-exact download; dropbox.py before/after listings).
- Cloud: /Nova-Download-Manager/builds/ holds 1.8.0 (4 artifacts + sidecars) + 1.7.1 (previous); 1.7.0 removed per keep-2; canonical /Nova-Download-Manager/{backups,state,builds} intact; checkpoint refreshed at end of T-024 via backup.py.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md (T-024 entry recorded 2026-10-30).
