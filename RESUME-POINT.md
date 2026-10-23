# RESUME-POINT — atomic continuation contract

updated: 2026-10-24 (T-012 IDM parity research complete)
task: NONE OPEN — IDM gap analysis + roadmap delivered; awaiting user decision on starting v1.1.0 "IDM Parity Core"
substep: —

done:
- T-006 rules + memory infrastructure (UNIVERSAL-RULES.md adopted, all memory docs created)
- T-007 Dropbox persistence layer (tools/dropbox/{dropbox,backup,restore}.py) — verified live
- T-008 toolchain rebuilt user-space: sdk/dotnet 8.0.426, sdk/flutter 3.47.7, ~/tools/{gtk3-dev 3.24.49, llvm-19 (clang 19.1.7), glvnd+mesa swrast, xvfb-dev(netpbm/xwd)}, venv cmake+ninja
- T-009 verification: dotnet test 43/43; flutter analyze 0 errors; flutter test 8/8; flutter build linux --release OK; publish FDD + self-contained OK
- T-010 packaging + evidence: smoke_test.sh PASS (real IPC+download), ui_smoke.sh PASS (Xvfb GUI, engine auto-spawn, "Engine connected"), demo_download_shot.sh PASS ("Completed: 1" visible — VLM-verified), clean-env self-contained boot PASS, register-browser-host.sh verified
- upload: /Nova-Download-Manager/builds/{nova-dm-1.0.1-linux-x64.tar.gz (+.sha256), evidence-*.png} — round-trip SHA-256 match
- git commit: v1.0.1 (Linux build + persistence layer)
- T-011 (prior session): full re-verification + flaky-test fix (see worklog.md)
- T-012 (this session): deep IDM research (3 rounds, official sources) + Nova v1.0.1 code audit → 52-row capability matrix (11 HAVE / 16 PARTIAL / 23 MISSING / 2 EXCLUDED); deliverables: docs/IDM-PARITY.md (working backlog), download/Nova-IDM-Parity-Gap-Analysis-FA.{pdf,html} (14-page Persian RTL report, VLM QA-passed), cover preview PNG; all mirrored to Dropbox (/Nova-Download-Manager/{docs,reports}/) with round-trip sha256 verification; restored tools/dropbox/.dropbox-credentials.json after env reset

next:
1. USER: decide — start v1.1.0 "IDM Parity Core" (12 P0 items; recommended order: engine-only quick wins → proxy/logins infra → UI batch) or report v1.0.1 Linux-tarball bugs for v1.0.2
2. Any new session: read STATE.md → this file → worklog.md tail → continue
3. Gap-analysis doc updates after each implemented item (keep matrix in docs/IDM-PARITY.md in sync with code)

changed files (T-011): src/Nova.Ipc/{IpcServer,IpcClient}.cs (optional pipeName param, default unchanged), tests/Nova.Engine.Tests/{BrowserPathTests,IpcAndSchedulerTests}.cs (isolation + readiness poll), scripts/linux/{ipc_probe.py,tarball_cleanenv_test.sh} (new), RULES-READ-LEDGER.md (certificate), STATE/RESUME (this update), download/evidence-v1.0.1-reverify.txt (new)
changed files (T-010, prior): installer/linux/* (new), tools/dropbox/* (new), scripts/env.sh (new), scripts/linux/*.sh (patched to env.sh + system Xvfb), CHANGELOG/STATE/VERSIONS/DECISIONS/RECOVERY/RULES-READ-LEDGER/PROJECT-RULES/UNIVERSAL-RULES (new/updated), .gitignore (sdk + secrets)

evidence:
- sha256(nova-dm-1.0.1-linux-x64.tar.gz) = e0199b868f8de2dba405de55ee95fe16dd20aedfaa363e8b6f61546ccb3c603e
- Dropbox builds listing verified; download round-trip hash match
- VLM visual QA of nova-linux-ui-download.png: "Engine connected", "Completed: 1"

blockers: none
recovery: RECOVERY.md
