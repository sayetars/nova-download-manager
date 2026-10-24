# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.1.0
CURRENT TASK: T-014 (v1.2.0 IDM Parity Polish — ALL P2 gaps) COMPLETE — per-server connection caps (#14), extension special keys + options page (#23), per-queue start/stop schedules (#26), periodic sync queues w/ ETag/Last-Modified (#28), sortable downloads list (#36), always-on-top progress mini-window (#44), complete-dialog customization, best-effort IDM import. IDM-PARITY matrix now: 0 MISSING rows (remaining PARTIAL rows are deliberate design trade-offs or platform-scoped).
CURRENT SUBSTEP: —
DONE: v1.2.0 shipped: SQLite migration v6 (sync_rule/sync_state/queue schedule_json), SyncService + PerServerPolicy + QueueSchedule + IdmImport (engine), 6 new IPC commands (list/save/delete_sync_rule, run_sync, import_idm, per_server_overview), Flutter sync page + queue schedule editor + sort menu + per-server settings + mini_app.dart (--mini), linux runner nova/window channel + always-on-top, windows runner HWND_TOPMOST path (not compiled — KNOWN-LIMITATIONS #9), extension keyMatches + preventKey overlay suppression + options page (both browsers), manifests 1.2.0.
NEXT: (1) user test of v1.2.0 tarball; (2) Windows parity rebuild (runner code ready for DnD + mini-window); (3) optional .deb/AppImage, tray parity on Windows, additional languages.
BLOCKERS: none
LAST VERIFIED: 2026-10-24 — dotnet test 108/108 (×2 runs); flutter analyze 0 errors; flutter test 8/8; smoke_test.sh PASS; parity_smoke_test.sh 12/12; mini_window_smoke.sh PASS (+ VLM QA PASS, screenshot nova-mini-window-crop.png); UI demo + VLM QA PASS (evidence-v1.2.0-vlm.json); tarball_cleanenv_test.sh PASS (no-dotnet boot, byte-exact).
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain live at sdk/{dotnet,flutter} + ~/tools/{gtk3-dev,llvm-19,glvnd,xvfb-dev}; venv cmake+ninja at ~/.venv; source scripts/env.sh; no root; no background process survival between tool calls. NOTE: clang needs libclang-common-19-dev (resource headers) — installed into ~/tools/llvm-19.
```

## DONE (high level)
- v1.0.0 Windows-path release (previous session).
- v1.0.1 Linux x64 release: `download/nova-dm-1.0.1-linux-x64.tar.gz` (70.6 MB) + Dropbox `/Nova-Download-Manager/builds/`.
- v1.1.0 Linux x64 release (this session): `download/nova-dm-1.1.0-linux-x64.tar.gz` (41 MB, gzip; engine self-contained 70 MB single-file) + Dropbox. IDM Parity Core (P0 1–12) + Plus (P1 13–19) implemented; test suite 43→81.
- T-012: IDM feature-parity gap analysis — docs/IDM-PARITY.md matrix now shows 42 HAVE / 4 remaining P2 gaps / 2 EXCLUDED.
- Persistence layer + project memory (per UNIVERSAL-RULES mandate); worklog restored from Dropbox state mirror after this session's env reset.

## NEXT
1. User tests v1.1.0 Linux tarball → collect feedback (v1.1.1 bugfix slot reserved).
2. P2 polish (v1.2.x): per-server connection caps, special interception keys, periodic sync queues, progress mini-window, sortable columns.
3. Windows parity rebuild of 1.1.0 (DnD runner code included); installer via scripts/build-release.ps1 on a Windows host.
4. Optional: .deb/AppImage packaging, publishing extension IDs.

## LAST VERIFIED STATE
- git HEAD: see `git log -1` (v1.1.0 commit made at end of this task).
- Evidence: download/evidence-v1.1.0-vlm.json, download/nova-linux-ui-download.png, smoke logs in /tmp during run, tarball sha256 in VERSIONS.md.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md.
