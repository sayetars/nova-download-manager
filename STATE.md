# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.8.0
CURRENT TASK: T-025 (GitHub Windows-CI push lane) IN PROGRESS — user sent GitHub App ID 5545710 + Client ID (recorded in tools/github/.github-credentials.json, 600/gitignored); device-flow probe → device_flow_disabled, so those two alone cannot authenticate. Done this session: (1) D-016 pre-push sanitation — live Dropbox credentials found hardcoded in tracked scripts/nova_dropbox_upload.py (+ its history, single intro commit 3c219fd) → path purged from ALL history via git-filter-repo (40 commits kept, tags migrated, fsck clean, 0 hits in history, sync --check PASS); (2) scripts/github_release_push.py added (plan/push/status --watch; app-JWT or PAT auth; pre-push secrets scan; token via http.extraheader only); (3) tools/github/ local-credential lane (gitignored + backup.py-excluded). WAITING ON USER: repo URL (github.com/new, empty) + EITHER the GitHub App private key (.pem from App settings → Private keys → Generate; app installed on the repo with Contents RW + Workflows RW) OR a classic PAT (repo+workflow). Then: push main + tags → version-audit.yml + windows-build.yml → Windows installer + auto-Release.
CURRENT SUBSTEP: awaiting user's repo URL + auth credentials
DONE: v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.x / v1.5.0 / v1.6.0 / v1.7.0 / v1.7.1 / v1.8.0 releases (VERSIONS.md); T-022 (free-forever); T-023 (nova/ purge); T-024 (IDM full-parity, v1.8.0); T-025 in progress (GitHub push lane: D-016 sanitation + tooling done, push pending user credentials).
NEXT: (1) USER: repo URL + auth — EITHER GitHub App .pem private key (App settings → Private keys → Generate; then App → Install App on the repo; Repository permissions Contents=RW, Workflows=RW) OR classic PAT (repo+workflow). Device-flow alternative: enable the "Device Flow" checkbox in the App settings, then the session guides a code login. Once received → `python3 scripts/github_release_push.py plan` → `push` (main + tags) → version-audit.yml + windows-build.yml → Windows installer + auto-Release on v1.8.0; (2) Windows real-machine feedback → v1.8.x PATCH lane; (3) future (all free): Linux tray + Linux drag-out (GTK runner work), embedded player, arbitrary non-modifier special keys, CWS/AMO store publishing — paid services BANNED (D-013, docs/FREE-FOREVER.md).
BLOCKERS: none (Windows GUI build = GitHub Actions; engine side delivered on Dropbox)
LAST VERIFIED: 2026-10-31 (T-025) — post-filter-repo: git fsck --full clean; rev-list --all = 40 commits; tags v1.4.0…v1.8.0 present on rewritten SHAs; `git log --all -S<dropbox-secret>` = 0 hits; sync --check PASS; github_release_push.py compiles + guard-paths tested (missing-PEM guidance) + secrets-audit PASS on tracked tree. Prior (T-024, 2026-10-30): dotnet test 320/320; flutter analyze 0 errors / 13 tests; free_audit PASS; tarball clean-env PASS; Dropbox builds listing verified (1.8.0 current + 1.7.1 previous).
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: rebuilt 2026-10-30 for T-024 (sdk/dotnet 8.0.427 + sdk/flutter 3.47.7 restored from RECOVERY.md §2; GTK3-dev + LLVM-19 + cmake/ninja installed for the Linux UI build; Node present; venv python3 3.12 OK; no gh CLI). Dropbox creds at tools/dropbox/.dropbox-credentials.json (600). Root worklog.md restored from the cloud state mirror + T-024 entry appended.
```

## DONE (high level)
- v1.0.x → v1.8.0 releases (see VERSIONS.md).
- v1.8.0 (T-024): IDM full-parity completion — see CHANGELOG [1.8.0] + docs/IDM-PARITY.md + DECISIONS D-015.
- v1.7.1 (T-022): free-forever policy; v1.7.0 (T-021): version integrity + AES-128 HLS; T-023: nova/ parallel-tree purge + cloud cleanup.

## NEXT
1. USER: repo URL (github.com/new — empty, no README) + auth: GitHub App .pem (install app on repo, Contents+Workflows RW) or classic PAT (repo+workflow) → `python3 scripts/github_release_push.py plan` then `push` → windows-build.yml (full Windows installer; audit gate first).
2. Windows test feedback → v1.8.x PATCH lane (bump version.json → sync → test → tag).
3. Future candidates (all free): Linux tray + Linux drag-out, embedded player (libmpv decision), arbitrary non-modifier special keys, CWS/AMO store publishing.

## LAST VERIFIED STATE
- Evidence: this session's command outputs (320/320 with binaries pre-built; isolated re-runs green; flutter analyze 0 errors / 13 tests; sync --check PASS; free_audit PASS; tarball clean-env PASS with byte-exact download; dropbox.py before/after listings).
- Cloud: /Nova-Download-Manager/builds/ holds 1.8.0 (4 artifacts + sidecars) + 1.7.1 (previous); 1.7.0 removed per keep-2; canonical /Nova-Download-Manager/{backups,state,builds} intact; checkpoint refreshed at end of T-024 via backup.py.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md (T-024 entry recorded 2026-10-30).
