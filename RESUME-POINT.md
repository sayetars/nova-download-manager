# RESUME-POINT — atomic continuation contract

updated: 2026-10-30 (T-024 — IDM full-parity completion, v1.8.0 released)
task: NONE OPEN — v1.8.0 shipped; awaiting user's GitHub repo + PAT for the Windows CI build
substep: —

done (T-024, this session):
- Session certificate: UNIVERSAL-RULES.md read (SHA 87de0baa… matches ledger),
  T-024 recorded in RULES-READ-LEDGER.md. Version floor verified FIRST:
  v1.7.1 (VERSIONS.md + git tag + clean status) → worked ON TOP of it (D-012).
- AUDIT-FIRST discovery: commit fdba99f ("docs(T-023): PROJECT-TREE.txt
  regenerated") actually contained 2,004 lines of v1.8.0-start feature work
  (PAC/Jint, SystemProxy, sounds, tray, CategoryDirStore, HTTP/2,
  resume-ignore + the planMatches fix, V180Tests) — committed by a prior
  session AFTER its toolchain was wiped, so it had NEVER compiled (corrupted
  FA sourcesCount string, missing dart:ui/SoundPlayer imports, wrong
  didRequestAppExit signature). This session rebuilt the toolchain and FIXED
  that code instead of rewriting it (D-015).
- Deep IDM research: official features2.html (21 features), options.html
  (9 tabs), starting.html, command_line.html, idm_scheduler.html,
  idm-grabber/wheretosearch.html → docs/IDM-PARITY.md fully refreshed
  (63 rows; every implementable row HAVE; exclusions documented).
- Implemented + tested (V180CompletionTests.cs, 37 new tests):
  * 401/407 interactive login dialog (probe+slice auth retry via
    ProbeWithAuthAsync + CredentialsFor; auth_required error; ProvideAuth
    engine API; provide_auth IPC; UI dialog with save-to-vault + re-prompt)
  * LoginVault URL-prefix entries (longest-prefix wins; per-directory logins)
  * per-queue retry defaults (queue.max_retries.<name>) + lone-task infinite
    30 s retry (engine.lone_infinite_retry, default on)
  * per-queue post actions (queue.post_action.<name> = open:<path> | power)
    fired on active→idle transition (EngineHostApp)
  * Grabber: ExploreParentDirectories / ExploreSubdomains (registrable-domain
    boundary) / ExternalDepth / Min-MaxBytes + 2 bug fixes (result counters
    never populated; '/' in GetInvalidFileNameChars flattened folders)
  * PostActionRunner open: actions (BuildOpenStartInfo pure + tested)
  * autostart (AutoStartRegister: HKCU Run / XDG .desktop; get/set IPC;
    Settings toggle)
  * Extension Insert special key (key-state tracking; options page; rebuilt)
  * Windows runner drag-out (OLE DoDragDrop + CF_HDROP on nova/dragout;
    OleInitialize in main.cpp; shlwapi linked)
  * UI: auth dialog, queue-options dialog, grabber fields, shown-fields
    chooser (ui.tile_fields), drag affordance on completed tiles, strings
    EN/FA (+Fmt.date)
- Version: version.json → 1.8.0 (build 9); sync_versions 10 surfaces;
  free_audit PASS (Jint added to THIRD-PARTY-NOTICES.md).
- Verification: dotnet test 320/320 (full suite, pre-built binaries; the
  1-3 full-suite stragglers are the documented 2-CPU flakes — isolated
  re-runs all green); flutter analyze 0 errors; flutter test 13/13;
  tarball clean-env PASS (byte-exact download through the shipped engine).
- Release: nova-dm-1.8.0-linux-x64.tar.gz (50,760,022 B, sha256 2e6ccda1…)
  + nova-engine-1.8.0-win-x64.zip (106,168,361 B, sha256 a318af06…) +
  both extension zips + .sha256 sidecars → Dropbox /Nova-Download-Manager/
  builds/ (1.7.0 assets removed per keep-2). CHANGELOG [1.8.0], VERSIONS.md
  row + builds table, KNOWN-LIMITATIONS (#6/#18 closed, #8/#10 updated,
  §39 added), DECISIONS D-015, STATE/RESUME-POINT, worklog T-024.

next:
1. USER: provide GitHub repo (empty, any name) + PAT (classic scopes: repo +
   workflow; or fine-grained Contents+Workflows RW) → session pushes main +
   tags (v1.4.0…v1.8.0) → windows-build.yml runs (audit gate → engine tests →
   Flutter UI → extensions → Inno Setup installer → artifacts; a v* tag push
   also creates a GitHub Release via the built-in GITHUB_TOKEN — zero user
   secrets). Poll run status via the GitHub REST API (no gh CLI here).
2. Windows test feedback → v1.8.x PATCH lane (bump version.json → sync →
   test → tag).
3. Future (all free): Linux tray + Linux drag-out (GTK runner work),
   embedded player, arbitrary non-modifier special keys, CWS/AMO publishing —
   paid services BANNED (D-013).

evidence:
- git log (v1.8.0 commit + tag); dotnet test 320/320; flutter analyze/test
  outputs; python3 scripts/sync_versions.py --check → PASS;
  scripts/free_audit.py → PASS; tarball_cleanenv_test.sh → PASS;
  dropbox.py list before/after (uploads present; 1.7.0 removed).

blockers: none (GitHub push awaits user-supplied repo + PAT — by design, no
secrets live in the repo)
recovery: RECOVERY.md
