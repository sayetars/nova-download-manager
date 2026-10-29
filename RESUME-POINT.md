# RESUME-POINT — atomic continuation contract

updated: 2026-10-30 (T-023 — nova/ parallel-tree purge + cloud cleanup)
task: NONE OPEN — awaiting user's GitHub repo + PAT for the Windows CI build
substep: —

done (T-023, this session):
- Session certificate: UNIVERSAL-RULES.md read (SHA 87de0baa… matches ledger),
  T-023 recorded in RULES-READ-LEDGER.md. Version floor verified FIRST:
  v1.7.1 (VERSIONS.md + git tag + clean status).
- nova/ parallel v0.1.0 tree purged (user approval «تأیید حذف nova/»):
  gitlink (160000, nested repo) untracked; 149 MB tree deleted; narratives
  preserved → docs/history/legacy-nova-tree/{WORKLOG-2026-10-30-parallel-
  v0.1.0, KNOWN-LIMITATIONS-v0.1.0-parallel, README-v0.1.0-parallel}.md;
  `nova/` guard in .gitignore; DECISIONS D-014 (D-012 precedent re-applied).
- Security: the parallel tree's scripts/upload-dropbox.sh embedded the live
  Dropbox refresh token in plaintext (committed to the nested repo, never
  pushed — no remote). Rule 79/95 violation neutralized by deletion.
- Dropbox cleanup (surgical; before/after listings recorded): /Releases/v0.1.0/**
  (creds-bearing source zip), /Worklog/2026-10-30-implementation-session.md,
  /nova/** (old-tree remnants; ROUND-12.md 4323 B + WORKLOG.md 1086 B verified
  size-identical to local preserved copies before deletion), stray root /builds
  v1.5.0 assets (48.4 MB — keep-2 superseded by 1.7.1/1.7.0). ~48.6 MB freed;
  root now only the user's own folders + canonical /Nova-Download-Manager.
- Root worklog.md (lost in the env reset) restored from the cloud state mirror;
  T-023 entry appended. Dropbox creds file restored (600, D-004 pattern).
- Genuine feature delta found in the parallel tree → future item: PAC script
  evaluation (Jint, MIT) — docs/KNOWN-LIMITATIONS.md #18.
- Environment: reset again (sdk/, ~/tools gone) — toolchain NOT rebuilt (not
  needed for T-023; RECOVERY.md §2 when needed). GitHub Actions is the primary
  Windows build path from here.

next:
1. USER: provide GitHub repo (empty, any name) + PAT (classic scopes: repo +
   workflow; or fine-grained Contents+Workflows RW) → session pushes main +
   tags (v1.4.0…v1.7.1) → windows-build.yml runs (audit gate → engine tests →
   Flutter UI → extensions → Inno Setup installer → artifacts; a v* tag push
   also creates a GitHub Release via the built-in GITHUB_TOKEN — zero user
   secrets). Poll run status via the GitHub REST API (no gh CLI here).
2. Windows test feedback → v1.7.2 PATCH lane (bump version.json → sync →
   test → tag).
3. Future (all free): PAC (Jint), tray, embedded player, CWS/AMO publishing —
   paid services BANNED (D-013, docs/FREE-FOREVER.md).

evidence:
- git ls-tree HEAD nova → 160000 commit b048c86… (gitlink, nested repo);
  post-purge git status clean; single T-023 commit (git log -1).
- dropbox.py list / before+after (parallel-tree products present → absent).
- python3 scripts/sync_versions.py --check → PASS; scripts/free_audit.py →
  PASS (no dependency change; 242/242 engine tests stand from v1.7.1).

blockers: none (GitHub push awaits user-supplied repo + PAT — by design, no
secrets live in the repo)
recovery: RECOVERY.md
