# RESUME-POINT — atomic continuation contract

updated: 2026-10-24 ~19:20 UTC (v1.0.1 released)
task: NONE OPEN — v1.0.1 Linux build complete, verified, uploaded
substep: —

done:
- T-006 rules + memory infrastructure (UNIVERSAL-RULES.md adopted, all memory docs created)
- T-007 Dropbox persistence layer (tools/dropbox/{dropbox,backup,restore}.py) — verified live
- T-008 toolchain rebuilt user-space: sdk/dotnet 8.0.426, sdk/flutter 3.47.7, ~/tools/{gtk3-dev 3.24.49, llvm-19 (clang 19.1.7), glvnd+mesa swrast, xvfb-dev(netpbm/xwd)}, venv cmake+ninja
- T-009 verification: dotnet test 43/43; flutter analyze 0 errors; flutter test 8/8; flutter build linux --release OK; publish FDD + self-contained OK
- T-010 packaging + evidence: smoke_test.sh PASS (real IPC+download), ui_smoke.sh PASS (Xvfb GUI, engine auto-spawn, "Engine connected"), demo_download_shot.sh PASS ("Completed: 1" visible — VLM-verified), clean-env self-contained boot PASS, register-browser-host.sh verified
- upload: /Nova-Download-Manager/builds/{nova-dm-1.0.1-linux-x64.tar.gz (+.sha256), evidence-*.png} — round-trip SHA-256 match
- git commit: v1.0.1 (Linux build + persistence layer)

next:
1. USER: extract download/nova-dm-1.0.1-linux-x64.tar.gz on a Linux x64 machine → ./RUN-LINUX.sh
2. Iterate on user feedback (bugs → v1.0.2); optional .deb/AppImage, extension store IDs, Windows parity rebuild
3. Any new session: read STATE.md → this file → worklog.md tail → continue

changed files (this task): installer/linux/* (new), tools/dropbox/* (new), scripts/env.sh (new), scripts/linux/*.sh (patched to env.sh + system Xvfb), CHANGELOG/STATE/VERSIONS/DECISIONS/RECOVERY/RULES-READ-LEDGER/PROJECT-RULES/UNIVERSAL-RULES (new/updated), .gitignore (sdk + secrets)

evidence:
- sha256(nova-dm-1.0.1-linux-x64.tar.gz) = e0199b868f8de2dba405de55ee95fe16dd20aedfaa363e8b6f61546ccb3c603e
- Dropbox builds listing verified; download round-trip hash match
- VLM visual QA of nova-linux-ui-download.png: "Engine connected", "Completed: 1"

blockers: none
recovery: RECOVERY.md
