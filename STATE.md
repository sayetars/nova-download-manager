# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.0.1
CURRENT TASK: T-012 (IDM parity research) COMPLETE — 52-feature IDM matrix audited against code; gap analysis + 4-phase roadmap delivered (docs/IDM-PARITY.md + Persian PDF report); awaiting user go-ahead to start v1.1.0 "IDM Parity Core" (12 P0 items)
CURRENT SUBSTEP: —
DONE: v1.0.1 re-verified after env reset: 43/43 ×4 runs, analyze 0 errors, flutter 8/8, smoke suite PASS (build tree), shipped-tarball clean-env E2E PASS (raw-probe download, byte-exact), VLM visual QA PASS, Dropbox floor/worklog verified OK. Root-caused & fixed flaky BrowserPathTests (pipe-name race + fixed boot delay) via injectable pipeName; new tools: ipc_probe.py + tarball_cleanenv_test.sh
DONE: T-012: deep IDM-vs-Nova research (official IDM docs ×3 rounds + code audit) → 52-row capability matrix (11 HAVE / 16 PARTIAL / 23 MISSING / 2 EXCLUDED) + roadmap v1.1.0→v2.0.0; Persian RTL PDF report QA-passed (VLM visual QA, 14 pages); all artifacts uploaded to Dropbox (docs/ + reports/) with round-trip hash verification
NEXT: (1) user decision: start v1.1.0 IDM Parity Core (recommended order: dynamic segmentation + 32-connections first, then proxy/SOCKS + logins vault, then UI batch) or iterate on v1.0.1 user feedback; (2) v1.0.2 if bugs reported; (3) optional .deb/AppImage, store extension IDs, Windows parity rebuild
BLOCKERS: none
LAST VERIFIED: 2026-10-24 — T-012 deliverables verified: 14-page Persian PDF (content complete via pypdfium2, 0 replacement chars, all 10 chapters present), VLM visual QA PASS on 6 key pages, Dropbox round-trip sha256 match; earlier v1.0.1 evidence: dotnet test 43/43 (×4); flutter 8/8 + 0 analyze errors; smoke suite PASS; tarball clean-env E2E PASS
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain live at sdk/{dotnet,flutter} + ~/tools/{gtk3-dev,llvm-19,glvnd,xvfb-dev}; source env.sh via scripts/env.sh; no root; no background process survival between tool calls
```

## DONE (high level)
- v1.0.0 Windows-path release (previous session).
- v1.0.1 Linux x64 release: `download/nova-dm-1.0.1-linux-x64.tar.gz` (70.6 MB) + Dropbox `/Nova-Download-Manager/builds/`.
- Persistence layer + project memory (per UNIVERSAL-RULES mandate).
- T-011: post-reset re-verification of v1.0.1 (all green, evidence in download/evidence-v1.0.1-reverify.txt) + flaky-test root-cause fix (pipe-name race) — test-infra only, no user-facing behavior change; ships with next version.
- T-012: IDM feature-parity gap analysis — docs/IDM-PARITY.md (working backlog, 52-row matrix + P0/P1/P2 + roadmap v1.1.0→v2.0.0) + Persian RTL PDF report; mirrored to Dropbox (/docs/ and /reports/).

## NEXT
1. User decides: start v1.1.0 "IDM Parity Core" (12 P0 items from docs/IDM-PARITY.md) or report v1.0.1 bugs first.
2. v1.0.2 bugfix release if the user reports issues with the Linux tarball (includes T-011 test-isolation fix).
3. Optional: .deb/AppImage packaging, publishing extension IDs into manifests, Windows parity rebuild of 1.0.1.

## LAST VERIFIED STATE
- git HEAD: see `git log -1` (v1.0.1 commit made at end of this task).
- All evidence listed in START-HERE LAST VERIFIED line.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md.
