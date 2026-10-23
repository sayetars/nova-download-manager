# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.0.1
CURRENT TASK: T-011 (post-reset re-verification) COMPLETE — v1.0.1 re-verified end-to-end in fresh environment; flaky-test bug found & fixed (uncommitted-to-release, test-infra only); awaiting user test feedback
CURRENT SUBSTEP: —
DONE: v1.0.1 re-verified after env reset: 43/43 ×4 runs, analyze 0 errors, flutter 8/8, smoke suite PASS (build tree), shipped-tarball clean-env E2E PASS (raw-probe download, byte-exact), VLM visual QA PASS, Dropbox floor/worklog verified OK. Root-caused & fixed flaky BrowserPathTests (pipe-name race + fixed boot delay) via injectable pipeName; new tools: ipc_probe.py + tarball_cleanenv_test.sh
NEXT: user tests nova-dm-1.0.1-linux-x64.tar.gz on their machine; feedback → v1.0.2 (would include the test-infra fix). Optional: .deb/AppImage, store extension IDs, Windows parity rebuild
BLOCKERS: none
LAST VERIFIED: 2026-10-23 19:56 UTC — dotnet test 43/43 (×4); flutter test 8/8 + 0 analyze errors; smoke_test.sh PASS; ui_smoke.sh PASS; demo_download_shot.sh PASS + VLM; tarball_cleanenv_test.sh PASS (sha256 match e0199b86…c603e); Dropbox round-trip floor check OK
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain live at sdk/{dotnet,flutter} + ~/tools/{gtk3-dev,llvm-19,glvnd,xvfb-dev}; source env.sh via scripts/env.sh; no root; no background process survival between tool calls
```

## DONE (high level)
- v1.0.0 Windows-path release (previous session).
- v1.0.1 Linux x64 release: `download/nova-dm-1.0.1-linux-x64.tar.gz` (70.6 MB) + Dropbox `/Nova-Download-Manager/builds/`.
- Persistence layer + project memory (per UNIVERSAL-RULES mandate).
- T-011: post-reset re-verification of v1.0.1 (all green, evidence in download/evidence-v1.0.1-reverify.txt) + flaky-test root-cause fix (pipe-name race) — test-infra only, no user-facing behavior change; ships with next version.

## NEXT
1. User tests the Linux build (RUN-LINUX.sh) on their machine.
2. Feedback → triage → v1.0.2 fixes (v1.0.2 will include the IPC test-isolation fix).
3. Optional: .deb/AppImage packaging, publishing extension IDs into manifests, Windows parity rebuild of 1.0.1.

## LAST VERIFIED STATE
- git HEAD: see `git log -1` (v1.0.1 commit made at end of this task).
- All evidence listed in START-HERE LAST VERIFIED line.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md.
