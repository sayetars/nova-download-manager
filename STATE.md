# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.1.0
CURRENT TASK: T-013 (implement ALL IDM parity gaps) COMPLETE — v1.1.0 "IDM Parity Core + Plus" built, tested, packaged and uploaded: 81/81 C# tests (+12 new), 8/8 Flutter tests, 0 analyze errors, parity E2E 12/12, tarball clean-env PASS, UI smoke + VLM QA PASS. P0 items 1–12 all implemented; P1 items 13–19 (FTP, Grabber, AV, ZIP preview, CLI, import/export, sync-class features) implemented and E2E-verified.
CURRENT SUBSTEP: —
DONE: v1.1.0 shipped: byte-quota windows (QuotaTracker, restart-safe), task properties editor, logins vault editor UI, categories editor, speed-limiter toolbar toggle, per-segment progress bars + speed graph (task_segments IPC), native drag & drop (Win32 WM_DROPFILES + GTK drag-dest → nova/dnd channel), check-update, FTP byte-delta fix, live segment publishing. Environment rebuilt from scratch after reset (dotnet 8.0.426, flutter 3.47.7, clang 19.1.7 + headers, gtk3-dev, cmake/ninja venv, glvnd+mesa, xwd/netpbm).
DONE: v1.0.1 re-verified previously (43/43, 8/8); T-012 IDM research + Persian PDF delivered.
NEXT: (1) user test of v1.1.0 tarball → v1.1.1/v1.2.0 for remaining P2 gaps (per-server connection caps, special keys, sync queues, progress mini-window, column sorting); (2) Windows parity rebuild of 1.1.0 (runner DnD code ready); (3) optional .deb/AppImage + extension store IDs.
BLOCKERS: none
LAST VERIFIED: 2026-10-24 — dotnet test 81/81 (after full rebuild); flutter analyze 0 errors; flutter test 8/8; parity_smoke_test.sh 12/12; tarball_cleanenv_test.sh PASS (no-dotnet boot, byte-exact); ui_smoke.sh PASS; VLM QA PASS (nova-linux-ui-download.png + evidence-v1.1.0-vlm.json); Dropbox upload + round-trip hash verified.
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
