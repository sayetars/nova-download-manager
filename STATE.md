# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.8.0
CURRENT TASK: T-026 (Windows CI bring-up + GitHub release) COMPLETE — repo sayetars/nova-download-manager created via the App; 13 evidence-driven CI rounds fixed 5 latent Windows-only product bugs (finalize-with-open-handle, verifier share-conflict, pipe-DACL single-connection, IPC instance leak, ephemeral PEM keys) + Windows runner C++ + installer [Code] + cross-platform zips; run 41146761713 FULL GREEN (318/318 engine tests on real Windows, Flutter Windows UI compiled, Inno Setup installer built); v1.8.0 tag moved to the fixed commit → tag run green → GitHub Release LIVE at github.com/sayetars/nova-download-manager/releases/tag/v1.8.0 with nova-setup-1.8.0.exe (39.4 MB) attached. See D-017 + worklog T-026.
CURRENT SUBSTEP: —
DONE: v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.x / v1.5.0 / v1.6.0 / v1.7.0 / v1.7.1 / v1.8.0 releases (VERSIONS.md); T-022 (free-forever); T-023 (nova/ purge); T-024 (IDM full-parity, v1.8.0); T-025 in progress (GitHub push lane: D-016 sanitation + tooling done, push pending user credentials).
NEXT: (1) USER: install nova-setup-1.8.0.exe on a real Windows machine (from the GitHub Release — includes the 5 Windows fixes; supersedes the Dropbox v1.8.0 engine zip, which predates them) → feedback → v1.8.x PATCH lane; (2) repo maintenance now happens via scripts/github_release_push.py (plan/push/status); (3) future (all free): Linux tray + Linux drag-out (GTK runner work), embedded player, arbitrary non-modifier special keys, CWS/AMO store publishing — paid services BANNED (D-013, docs/FREE-FOREVER.md).
BLOCKERS: none (Windows GUI build = GitHub Actions; engine side delivered on Dropbox)
LAST VERIFIED: 2026-10-31 (T-026) — windows-build.yml run 41146761713 every-step-green + tag run 41147670500 green + Release v1.8.0 live with nova-setup-1.8.0.exe; local dotnet test 318/318. (T-025) GitHub App key VALIDATED (JWT→GET /app 200: Universal-AI-Developer/sayetars; contents+workflows+repository_creation all write; 0 installations yet — install pending); post-filter-repo: git fsck --full clean; rev-list --all = 40 commits; tags v1.4.0…v1.8.0 present on rewritten SHAs; `git log --all -S<dropbox-secret>` = 0 hits; sync --check PASS; github_release_push.py + github_app_probe.py compile + guard-paths tested; secrets-audit PASS on tracked tree. Prior (T-024, 2026-10-30): dotnet test 320/320; flutter analyze 0 errors / 13 tests; free_audit PASS; tarball clean-env PASS; Dropbox builds listing verified (1.8.0 current + 1.7.1 previous).
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: rebuilt 2026-10-30 for T-024 (sdk/dotnet 8.0.427 + sdk/flutter 3.47.7 restored from RECOVERY.md §2; GTK3-dev + LLVM-19 + cmake/ninja installed for the Linux UI build; Node present; venv python3 3.12 OK; no gh CLI). Dropbox creds at tools/dropbox/.dropbox-credentials.json (600). Root worklog.md restored from the cloud state mirror + T-024 entry appended.
```

## DONE (high level)
- v1.0.x → v1.8.0 releases (see VERSIONS.md).
- v1.8.0 (T-024): IDM full-parity completion — see CHANGELOG [1.8.0] + docs/IDM-PARITY.md + DECISIONS D-015.
- v1.7.1 (T-022): free-forever policy; v1.7.0 (T-021): version integrity + AES-128 HLS; T-023: nova/ parallel-tree purge + cloud cleanup.

## NEXT
1. USER: (a) create empty repo github.com/new (any name); (b) install App on it — github.com/apps/universal-ai-developer/installations/new (or "All repositories" and the session creates the repo via API). Send repo name → probe → plan → push → windows-build.yml (full Windows installer; audit gate first).
2. Windows test feedback → v1.8.x PATCH lane (bump version.json → sync → test → tag).
3. Future candidates (all free): Linux tray + Linux drag-out, embedded player (libmpv decision), arbitrary non-modifier special keys, CWS/AMO store publishing.

## LAST VERIFIED STATE
- Evidence: this session's command outputs (320/320 with binaries pre-built; isolated re-runs green; flutter analyze 0 errors / 13 tests; sync --check PASS; free_audit PASS; tarball clean-env PASS with byte-exact download; dropbox.py before/after listings).
- Cloud: /Nova-Download-Manager/builds/ holds 1.8.0 (4 artifacts + sidecars) + 1.7.1 (previous); 1.7.0 removed per keep-2; canonical /Nova-Download-Manager/{backups,state,builds} intact; checkpoint refreshed at end of T-024 via backup.py.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md (T-025 entry recorded 2026-10-31).
