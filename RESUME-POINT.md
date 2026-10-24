# RESUME-POINT — atomic continuation contract

updated: 2026-10-24 (T-013 IDM parity implementation complete — v1.1.0 shipped)
task: NONE OPEN — all P0 + most P1 parity items implemented, verified and released; awaiting user feedback / next directive
substep: —

done:
- T-006 rules + memory infrastructure (UNIVERSAL-RULES.md adopted, all memory docs created)
- T-007 Dropbox persistence layer (tools/dropbox/{dropbox,backup,restore}.py) — verified live
- T-008 toolchain rebuilt user-space (sdk/dotnet 8.0.426, sdk/flutter 3.47.7, ~/tools/{gtk3-dev, llvm-19 + libclang-common-19-dev headers, glvnd+mesa swrast + libgles2/libgbm, xvfb-dev incl. netpbm lib}, ~/.venv cmake+ninja)
- T-009 verification: dotnet test 43/43; flutter analyze 0 errors; flutter test 8/8; flutter build linux OK
- T-010 packaging + evidence: v1.0.1 tarball + smoke suite + Dropbox round-trip
- T-011 post-reset re-verification + flaky-test isolation fix
- T-012 IDM parity research (52-row matrix + Persian PDF report)
- T-013 (this session, after full env reset):
  - toolchain + project state restored (worklog from Dropbox, credentials re-created, scripts/linux restored from git)
  - Engine: QuotaTracker (rolling byte windows, auto-pause/resume, byte_quota_log persistence, quota_state IPC, engine_status.quota), live segment publishing (task_segments IPC), FluentFTP byte-delta fix, NovaDatabase quota helpers; 12 new tests (10 quota + 2 segment views)
  - UI: task properties editor (task_properties.dart), logins vault editor, categories editor, quota settings + banner, toolbar speed-limiter toggle, details-sheet segment bars + speed graph, drag & drop (nova/dnd channel), check-update; strings EN+FA extended; updateTask provider extended
  - Release: versions bumped (engine 1.1.0, extensions 1.1.0, pubspec 1.1.0+2), extensions repackaged, FDD engine/browserhost/cli + single-file self-contained engine published, flutter build linux OK (after clang-19 headers + GtkTargetEntry casts), package_release.sh, RUN-LINUX.sh hardened (DOTNET_BUNDLE_EXTRACT_BASE_DIR + Nova.Grabber.dll cleanup)
  - Verification: dotnet 81/81, flutter 8/8 + 0 analyze errors, smoke_test PASS, parity_smoke_test 12/12, tarball_cleanenv_test PASS, ui_smoke PASS, demo screenshot + VLM QA PASS
  - Docs: CHANGELOG/VERSIONS/IDM-PARITY matrix (42 HAVE) updated; STATE/RESUME/worklog updated

next:
1. USER: test v1.1.0 tarball (download/nova-dm-1.1.0-linux-x64.tar.gz) → feedback → v1.1.1 bugfix or continue P2
2. Remaining P2 gaps (v1.2.x): per-server connection caps, special interception keys, periodic sync queues, progress mini-window, sortable columns
3. Windows rebuild of 1.1.0 on a Windows host (runner DnD code is in place: WM_DROPFILES in windows/runner/flutter_window.cpp)
4. Any new session: read STATE.md → this file → worklog.md tail → continue

changed files (T-013):
- src/Nova.Engine/{QuotaTracker.cs (new), DownloadEngine.cs, TaskRunner.cs, FtpTask.cs}
- src/Nova.Persistence/{NovaDatabase.cs, LoginVault.cs, GrabberService.cs (in-flight from prior session)}
- src/Nova.EngineHost/{EngineHostApp.cs, IpcMessageRouter.cs}
- src/Nova.Ipc/Messages.cs (QuotaStateDto, EngineStatusResponse.Quota, version 1.1.0)
- src/Nova.Grabber/ + tests/Nova.Engine.Tests/ParityTests.cs (in-flight from prior session + quota/segment tests)
- apps/nova_desktop/lib/{pages/{task_properties.dart (new), downloads_page.dart, settings_page.dart, shell.dart}, providers/engine.dart, core/{models.dart, strings.dart}}
- apps/nova_desktop/{windows/runner/{flutter_window.cpp, flutter_window.h}, linux/runner/my_application.cc}
- installer/linux/RUN-LINUX.sh, scripts/linux/{package_release.sh (new), tarball_cleanenv_test.sh, parity_smoke_test.sh}
- extensions/{build.mjs, package.json, chromium/manifest.json, firefox/manifest.json}
- CHANGELOG.md, VERSIONS.md, docs/IDM-PARITY.md, STATE.md, RESUME-POINT.md, worklog.md

evidence:
- sha256(nova-dm-1.1.0-linux-x64.tar.gz) = 748289f6eb14c242193d45bc3c7f5158efc2f87dbdfe1e2a8a60a8489a1d2ad3
- parity E2E 12/12 (engine status CLI, byte-exact 8 MiB, expand, remote ZIP, grabber page+3 resources, IDM /a, speed cap, export)
- clean-env tarball boot + raw-probe download byte-exact
- VLM visual QA: window/connection/completed-task OK, no glitches (download/evidence-v1.1.0-vlm.json)

blockers: none
recovery: RECOVERY.md
