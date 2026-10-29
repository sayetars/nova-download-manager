# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.7.1
CURRENT TASK: T-023 (nova/ parallel-tree purge) COMPLETE — user-approved («تأیید حذف nova/») re-execution of the D-012 purge: the v0.1.0 parallel tree had been RE-CREATED 2026-10-29/30 by a session working from a wrong "nothing implemented" handoff summary (while v1.7.1, 242/242 tests, schema 11, was already the shipped product). Removed: gitlink `nova` (nested git repo, mode 160000) + 149 MB tree; narratives preserved in docs/history/legacy-nova-tree/ (3 new files); `nova/` guard in .gitignore; DECISIONS D-014. Security finding: the tree's scripts/upload-dropbox.sh embedded the LIVE Dropbox token in plaintext (never pushed — nested repo had no remote; neutralized by deletion). Cloud cleanup (verified by before/after listings): /Releases/v0.1.0/ (creds-bearing source zip), /Worklog/2026-10-30-…, /nova/** (old-tree remnants; narratives size-verified before deletion), stray root /builds v1.5.0 assets (48.4 MB, keep-2 superseded) — ~48.6 MB freed; only the user's own folders + canonical /Nova-Download-Manager remain. One genuine delta recorded as future item: PAC script evaluation (Jint/MIT) → KNOWN-LIMITATIONS #18. sync --check PASS; free audit PASS; no engine code touched (242/242 stands).
CURRENT SUBSTEP: —
DONE: v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.0 / v1.4.1 / v1.5.0 / v1.6.0 / v1.7.0 / v1.7.1 releases (VERSIONS.md); T-022 (free-forever round); T-023 (this session).
NEXT: (1) USER: send GitHub repo (empty, any name) + PAT (classic scopes repo+workflow, or fine-grained Contents+Workflows RW) → session pushes main + tags → .github/workflows/windows-build.yml builds the FULL Windows installer (audit gate first; engine + Flutter UI + extensions + Inno Setup; artifacts + auto-Release on v* tags using the built-in GITHUB_TOKEN — zero user secrets needed); (2) Windows feedback → v1.7.2 PATCH lane; (3) future (all free): PAC (Jint/MIT), tray, embedded player, store publishing — paid services BANNED (D-013, docs/FREE-FOREVER.md).
BLOCKERS: none (Windows GUI build = GitHub Actions; engine side already delivered on Dropbox)
LAST VERIFIED: 2026-10-30 — sync --check PASS (10/10 + cross-checks); free audit PASS; git status clean post-purge; Dropbox root listing re-verified (no parallel-tree products left).
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: reset AGAIN 2026-10-30 (sdk/, ~/tools wiped; my-project + git intact; venv python3 3.12.16 OK; no gh CLI). T-023 required only git+python — toolchain NOT rebuilt (RECOVERY.md §2 when builds needed; GitHub Actions is the Windows path). Dropbox creds restored (tools/dropbox/.dropbox-credentials.json, 600). Root worklog.md restored from the cloud state mirror + T-023 appended.
```

## DONE (high level)
- v1.0.x → v1.7.1 releases (see VERSIONS.md).
- v1.7.0 (T-021): version integrity (SSOT + CI + runtime gates + legacy purge) + AES-128 HLS.
- v1.7.1 (T-022): free-forever policy (docs, CI gate, D-013).
- T-023 (2026-10-30): nova/ parallel tree purged again (D-014) + surgical cloud cleanup.

## NEXT
1. USER: provide GitHub repo + PAT (repo+workflow scopes) → push main + tags → windows-build.yml (full Windows installer; audit gate first).
2. Report Windows runtime issues → v1.7.2 PATCH lane (bump version.json → sync → test → tag).
3. Future candidates (all free): PAC script support (Jint/MIT, KNOWN-LIMITATIONS #18), tray on Windows, embedded player (libmpv decision), CWS/AMO store publishing, FFmpeg minimum-version policy in the yt-dlp bridge.

## LAST VERIFIED STATE
- Evidence: this session's command outputs (dropbox.py before/after listings; sync --check PASS; free_audit PASS; git status clean; T-023 commit) + v1.7.1 release evidence (dotnet test 242/242; flutter analyze 0 errors / 13 tests; smoke + clean-env tarball PASS — CHANGELOG [1.7.1]).
- Cloud: parallel-tree products deleted (T-023); canonical /Nova-Download-Manager/{backups,state,builds} intact; checkpoint refreshed at end of T-023 via backup.py.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md (T-023 entry recorded 2026-10-30).
