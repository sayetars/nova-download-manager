# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.8.0
CURRENT TASK: T-025 (GitHub Windows-CI push lane) IN PROGRESS — user's GitHub App private key RECEIVED + VALIDATED (JWT→GET /app OK: Universal-AI-Developer, owner sayetars, App ID 5545710; key at tools/github/app-private-key.pem, 600/gitignored). App permissions verified: contents=write, workflows=write, repository_creation=write. REMAINING BLOCKER: 0 installations — user must install the App on the target repo (github.com/apps/universal-ai-developer/installations/new → select repo) + create the empty repo (github.com/new). Then: fill repo in tools/github/.github-credentials.json → scripts/github_app_probe.py verify → scripts/github_release_push.py plan → push (main + tags) → windows-build.yml → Windows installer + auto-Release. Earlier this session: D-016 history sanitation (live Dropbox creds purged from all 40 commits via git-filter-repo; tags migrated; fsck clean; 0 hits) + github_release_push.py + github_app_probe.py tooling + tools/github/ local-credential lane.
CURRENT SUBSTEP: awaiting user to (a) create the empty repo (github.com/new) and (b) install the App on it (github.com/apps/universal-ai-developer/installations/new) — everything else is ready
DONE: v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.x / v1.5.0 / v1.6.0 / v1.7.0 / v1.7.1 / v1.8.0 releases (VERSIONS.md); T-022 (free-forever); T-023 (nova/ purge); T-024 (IDM full-parity, v1.8.0); T-025 in progress (GitHub push lane: D-016 sanitation + tooling done, push pending user credentials).
NEXT: (1) USER (last two clicks): create empty repo github.com/new (any name; public OK — history is clean) AND install the App on it: github.com/apps/universal-ai-developer/installations/new → Only select repositories → pick the repo → Install. Alternative: install with "All repositories" and the session creates the repo itself via the API (repository_creation=write granted). Then tell me the repo name (owner/name) — session runs probe → plan → push (main + tags v1.4.0…v1.8.0) → windows-build.yml → Windows installer + auto-Release on v1.8.0; (2) Windows real-machine feedback → v1.8.x PATCH lane; (3) future (all free): Linux tray + Linux drag-out (GTK runner work), embedded player, arbitrary non-modifier special keys, CWS/AMO store publishing — paid services BANNED (D-013, docs/FREE-FOREVER.md).
BLOCKERS: none (Windows GUI build = GitHub Actions; engine side delivered on Dropbox)
LAST VERIFIED: 2026-10-31 (T-025) — GitHub App key VALIDATED (JWT→GET /app 200: Universal-AI-Developer/sayetars; contents+workflows+repository_creation all write; 0 installations yet — install pending); post-filter-repo: git fsck --full clean; rev-list --all = 40 commits; tags v1.4.0…v1.8.0 present on rewritten SHAs; `git log --all -S<dropbox-secret>` = 0 hits; sync --check PASS; github_release_push.py + github_app_probe.py compile + guard-paths tested; secrets-audit PASS on tracked tree. Prior (T-024, 2026-10-30): dotnet test 320/320; flutter analyze 0 errors / 13 tests; free_audit PASS; tarball clean-env PASS; Dropbox builds listing verified (1.8.0 current + 1.7.1 previous).
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
