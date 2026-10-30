# RESUME-POINT — atomic continuation contract

updated: 2026-10-31 (T-025 — GitHub Windows-CI push lane: sanitation + tooling done, push awaits user auth)
task: T-025 IN PROGRESS — GitHub push lane
substep: awaiting user's repo URL + auth credentials (see next)

done (T-025, this session):
- Session certificate: UNIVERSAL-RULES.md read (SHA 87de0baa… verified vs
  ledger), T-025 recorded in RULES-READ-LEDGER.md. Version floor verified
  FIRST: v1.8.0 (VERSIONS.md + git tag v1.8.0 + clean status) — T-024
  (IDM full-parity) was already COMPLETE when this session started; nothing
  re-implemented (D-012 discipline).
- User sent GitHub App credentials: App ID 5545710, Client ID Iv23li….
  Recorded in tools/github/.github-credentials.json (chmod 600, gitignored).
  OAuth device-flow probe with the Client ID → HTTP 400 device_flow_disabled:
  App ID + Client ID alone CANNOT authenticate (GitHub Apps authenticate via
  a JWT signed with the app private key .pem, exchanged for installation
  tokens).
- SECURITY (D-016): pre-push audit found the LIVE Dropbox credentials
  (APP_KEY/APP_SECRET/REFRESH_TOKEN) hardcoded in git-tracked
  scripts/nova_dropbox_upload.py — a second instance of the D-014 leak class
  (that purge only inspected the nested nova/ repo). The file was dead
  legacy (ROOT pointed at the purged nova/ tree; artifact removed in
  D-014). Purged the path from ALL history via git-filter-repo
  --invert-paths: 40 commits preserved, 6 tags (v1.4.0…v1.8.0) migrated to
  rewritten SHAs (HEAD 3c2643f → 08a6be3), git fsck clean, zero
  credential-string hits anywhere in history, sync --check PASS, working
  tree unchanged apart from the removed file. scripts/__pycache__/*.pyc
  untracked + __pycache__/ gitignored. NOTE: SHA references in older
  memory docs are now narrative labels (documented in D-016 tradeoff).
- Tooling: scripts/github_release_push.py (plan / push [--force] /
  status [--watch]; auth = GitHub-App JWT→installation-token OR classic PAT;
  pre-push tracked-tree secrets scan aborts on token/PEM-like strings; push
  authorized via `git -c http.extraheader` so tokens never enter
  .git/config). tools/github/README.md documents the credentials format;
  .gitignore + tools/dropbox/backup.py excludes extended so
  .github-credentials.json / *.pem / *.private-key are never committed or
  archived. PROJECT-TREE.txt regenerated.
- Process lesson recorded: `restore.py state` refreshes ALL STATE_DOCS from
  the cloud — run it BEFORE editing memory docs, never after (this session
  lost one round of ledger/DECISIONS/STATE edits to it and re-applied them).
- Verification this session: python3 -m py_compile OK; guard-path test
  (plan without .pem → exact fix guidance, exit 1); secrets-audit PASS on
  the tracked tree (one false positive fixed: loose slack-token regex
  matched ssl.AuthenticateAsServerAsync); git fsck/rev-list/tag/-S scans
  as above; sync_versions --check PASS.

next:
1. USER (pick one, then send repo URL):
   a) GitHub App path: App settings → General → Private keys → Generate a
      private key (.pem) → send/paste it (saved to
      tools/github/app-private-key.pem, 600); install the App on the repo
      (App page → Install App) with Repository permissions
      Contents=Read and write + Workflows=Read and write.
   b) PAT path (simplest): classic PAT with repo+workflow scopes.
   c) Device-flow path (no secrets pasted): enable "Device Flow" in the
      App settings; session then guides a github.com/login/device code
      login (still needs the App installed with the same permissions).
2. Create the target repo (github.com/new — empty, any name; public OK —
   history is now clean). Send "owner/name".
3. Session: update .github-credentials.json → `plan` → `push` (main + tags
   v1.4.0…v1.8.0) → `status --watch` windows-build.yml (audit gate →
   engine tests on Windows → Flutter UI → extensions → Inno Setup
   installer → artifacts; v1.8.0 tag push auto-creates the GitHub
   Release via built-in GITHUB_TOKEN — zero user secrets).
4. Windows real-machine feedback → v1.8.x PATCH lane.
5. Future (all free): Linux tray + Linux drag-out, embedded player,
   arbitrary non-modifier special keys, CWS/AMO publishing (D-013).

evidence:
- git log (post-rewrite v1.8.0 HEAD 08a6be3 + tags); fsck clean; history
  secret-scan 0 hits; sync --check PASS; py_compile + guard-path outputs;
  device-flow HTTP 400 probe output; /tmp/nova-git-backup-pre-rewrite.tar.gz
  (session-local safety copy, never uploaded).

blockers: none on the session side — push waits only on user-supplied
repo URL + auth (.pem or PAT or enabled device flow), by design (no
secrets live in the repo).

recovery: RECOVERY.md
