# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.0.1
CURRENT TASK: none open — v1.0.1 (first Linux x64 build) RELEASED and uploaded to Dropbox; awaiting user test feedback
CURRENT SUBSTEP: —
DONE: v1.0.1 Linux build: engine 43/43 tests, Flutter 8/8 tests + 0 analyze errors, release bundle smoke-tested end-to-end (IPC + real download + GUI under Xvfb + VLM visual QA), self-contained no-runtime boot verified, package uploaded + round-trip hash-verified on Dropbox
NEXT: user tests nova-dm-1.0.1-linux-x64.tar.gz on their Linux machine; iterate on feedback (bugs → v1.0.2). Optional backlog: .deb/AppImage packaging, real store extension IDs, Windows 1.0.1 parity rebuild
BLOCKERS: none
LAST VERIFIED: 2026-10-23 19:20 UTC — dotnet test 43/43 PASS; flutter test 8/8 PASS; smoke_test.sh PASS; ui_smoke.sh PASS; demo_download_shot.sh PASS; self-contained clean-env boot PASS; Dropbox round-trip SHA-256 match (e0199b868f8de2dba405de55ee95fe16dd20aedfaa363e8b6f61546ccb3c603e)
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain live at sdk/{dotnet,flutter} + ~/tools/{gtk3-dev,llvm-19,glvnd,xvfb-dev}; source env.sh via scripts/env.sh; no root; no background process survival between tool calls
```

## DONE (high level)
- v1.0.0 Windows-path release (previous session).
- v1.0.1 Linux x64 release (this session): `download/nova-dm-1.0.1-linux-x64.tar.gz` (70.6 MB) + Dropbox `/Nova-Download-Manager/builds/`.
- Persistence layer + project memory (per UNIVERSAL-RULES mandate).

## NEXT
1. User tests the Linux build (RUN-LINUX.sh) on their machine.
2. Feedback → triage → v1.0.2 fixes.
3. Optional: .deb/AppImage packaging, publishing extension IDs into manifests, Windows parity rebuild of 1.0.1.

## LAST VERIFIED STATE
- git HEAD: see `git log -1` (v1.0.1 commit made at end of this task).
- All evidence listed in START-HERE LAST VERIFIED line.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md.
