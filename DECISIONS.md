# DECISIONS — Nova Download Manager

Format: decision / alternatives / reason / evidence / tradeoff / consequence.

---
D-001 · 2026-10-23 [reconstructed from prior session]
decision: Linux IPC rides .NET's named-pipe→UDS mapping (<tempdir>/CoreFxPipe_<name>) instead of a second IPC stack.
alternatives: raw UDS server abstraction; TCP localhost.
reason: keeps one protocol (nipc/1) and one server implementation cross-platform; violates the no-TCP invariant never.
evidence: src/Nova.Ipc/IpcServer.cs (Platform guard), apps/nova_desktop/lib/core/engine_client.dart UnixSocketTransport.
tradeoff: socket path depends on TMPDIR convention.
consequence: Flutter side uses dart:io InternetAddressType.unix directly — no C++ bridge needed on Linux.

---
D-002 · 2026-10-24 (T-006)
decision: Adopt user-supplied UNIVERSAL-RULES.md (sha256 87de0baa…) as the permanent constitution; PROJECT-RULES.md sits under it.
alternatives: ad-hoc session behavior; only system-prompt rules.
reason: explicit user mandate ("طبق این قوانین از این به بعد رفتار کن").
evidence: UNIVERSAL-RULES.md at repo root; RULES-READ-LEDGER.md certificate.
tradeoff: heavier ceremony per task.
consequence: every session starts with rules-read + state/version floor verification.

---
D-003 · 2026-10-24 (T-007)
decision: SDKs (dotnet 8, flutter 3.47.7) install under /home/z/my-project/sdk/, NOT ~/ or /usr — and sdk/ is gitignored.
alternatives: ~/.dotnet + ~/flutter (prior session — both were wiped in the reset); system paths (no root).
reason: /home/z/my-project is the only path observed to survive resets; keeping toolchain inside it maximizes post-reset usability while staying out of git.
evidence: this reset kept my-project intact but removed ~/flutter, ~/.dotnet, ~/tools.
tradeoff: SDKs are large (~4 GB total with caches) inside the project mount; re-fetch is documented in RECOVERY.md so backups stay small.
consequence: build scripts reference sdk/dotnet + sdk/flutter; RECOVERY.md documents exact versions/URLs.

---
D-004 · 2026-10-24 (T-007)
decision: Dropbox credentials are stored ONLY in tools/dropbox/.dropbox-credentials.json (chmod 600, gitignored, excluded from every backup archive).
alternatives: embed credentials in backup tars for full-wipe self-recovery; store in env vars.
reason: rule 79/95 — live credentials must never enter normal archives (the tars live in cloud storage; a leak would expose the whole Dropbox).
evidence: backup.py EXCLUDE_NAMES + _keep() filter.
tradeoff: a TOTAL wipe (including my-project) requires the user to re-supply credentials once.
consequence: RECOVERY.md documents that single manual step explicitly.

---
D-005 · 2026-10-24 (T-008)
decision: All long-running operations are chunked foreground steps (≤ ~9.5 min each) with resume support; NO background jobs.
alternatives: nohup/setsid backgrounding; tmux.
reason: verified in-sandbox: every process (even setsid-detached) is killed when a tool call ends; partial curl -C - downloads and .done-marker scripts survive on disk.
evidence: setsid sleep test killed between calls; /tmp/nova-bootstrap logs show early process death.
tradeoff: more orchestration steps.
consequence: bootstrap scripts must be idempotent/resumable by design.

---
D-006 · 2026-10-24 (T-008)
decision: Build version stays 1.0.x line: v1.0.1 = first Linux x64 build (not 1.1.0) — feature parity with v1.0.0, platform extension only.
alternatives: bump minor for platform support.
reason: semantic versioning — no new API/behaviour, new packaging target.
evidence: CHANGELOG 1.0.0 baseline.
tradeoff: none material.
consequence: assembly version 1.0.1, artifact name nova-dm-1.0.1-linux-x64.tar.gz.

---
D-007 · 2026-10-24 (T-009 preparation)
decision: Engine ships as single-file framework-dependent publish `NovaEngine` placed beside the flutter bundle (per scripts/linux/smoke_test.sh layout from prior session).
alternatives: self-contained single-file (~70 MB, slower start); separate engine package.
reason: dotnet runtime requirement is documented; framework-dependent keeps the tarball small and honest; matches the smoke script contract.
evidence: scripts/linux/smoke_test.sh expects build/linux/x64/release/bundle/NovaEngine.
tradeoff: user must have .NET 8 runtime OR we also ship self-contained variant.
consequence: produce BOTH variants — bundle tar (FDD) + a self-contained engine tar for runtime-less machines; test FDD path in smoke.

---
D-009 · 2026-10-23 (T-011)
decision: Fix flaky BrowserPathTests by making the IPC pipe name injectable (optional `pipeName` on IpcServer ctor + IpcClient.ConnectAsync, default = current well-known name) instead of respinning v1.0.1.
alternatives: disable xUnit parallelism (hides the class of bug); fixed longer sleep (still racy); respin v1.0.1 tarball (no user-facing change → invalid reason to invalidate a verified artifact).
reason: root cause of the intermittent 3-test failure was two parallel test classes racing for the single well-known pipe name `nova-engine-v1` (IpcTests' in-proc server vs BrowserPathFixture's spawned engine); secondarily a fixed 1500ms engine-boot sleep that cold-JIT boots can exceed. Evidence: first full run after reset failed exactly the 3 ok:true-expecting browser tests while the ok:false-expecting malformed test passed (symptom of auth_failed against the wrong server), all 4 passed in isolation, and 4 consecutive full runs green after isolation fix.
evidence: tests/Nova.Engine.Tests/{IpcAndSchedulerTests,BrowserPathTests}.cs; 4× dotnet test 43/43; download/evidence-v1.0.1-reverify.txt.
tradeoff: a tiny public-surface addition (optional param) exists ahead of the next release.
consequence: v1.0.1 tarball stays valid (zero runtime behavior change with defaults); the fix ships in v1.0.2; CI-style parallel test runs are now deterministic on Linux (NOVA_PIPE_PATH isolation) and safe-by-unique-name everywhere.

---
D-008 · 2026-10-24 (T-010)
decision: Ship dual engine variants with a smart launcher (RUN-LINUX.sh) that swaps the self-contained NovaEngine into the bundle when no .NET 8 runtime is detected.
alternatives: self-contained only (150 MB+ tar, slower start); FDD only (blocks runtime-less testers).
reason: user asked for a build "to test" — friction must be near-zero on any Linux box.
evidence: clean-env (env -i, no dotnet) boot test — socket, ipc.token, nova.db all created; swap logic verified by size heuristic (apphost <1 MB vs single-file ~67 MB).
tradeoff: tarball grows to ~71 MB; swap mutates the extracted tree (documented).
consequence: one package serves both runtime and runtime-less machines.

---
D-010 · 2026-10-28 (T-019)
decision: Cloud retention policy (user directive, formalized in CLOUD-POLICY.md): Dropbox carries ONLY lean source checkpoints (keep 3), current+previous release assets, and the state/worklog doc tree. To make checkpoints lean, git history was REWRITTEN (filter-branch) to purge accidentally-committed build outputs (publish/, tool-results/) — .git 224 MiB → 976 KiB; tracked set 87 MiB → 2.7 MiB; all 21 commit hashes changed. Hash map (old→new): HEAD 52f3d15 → 6e005ae; v1.5.0 ef6a00f → 90db0d9; v1.4.1 a2fb028 → af85119; v1.4.0 tag → f46b257.
alternatives: keep fat history and buy Dropbox space; orphan fresh history (loses provenance); no rewrite, exclude-only going forward (leaves 405 MiB of bloated backups in the cloud forever).
reason: account measured at 1.81 GB (1.68 GiB) of 2.00 GiB used; the two v1.4.1 "source" backups were 220.2 + 185.0 MiB because publish/ binaries (incl. a 72 MiB self-contained NovaEngine) and 12 tool-results/*.txt had been committed to git — build outputs were being archived and uploaded as "sources", exactly what the user banned.
evidence: tools/dropbox/inventory.py output; the two remote manifests (file_count 301/315, sizes 194/221 MiB); git ls-files publish = 58 files / 84 MiB pre-purge; git fsck clean post-purge.
tradeoff: old commit hashes referenced in pre-T-019 documents no longer resolve (map above); remote metadata (size + sha256) must be re-verified after upload (backup.py does).
consequence: checkpoints are now ~1.4 MiB (v1.5.0 measured); keep-3 pruning + 10 MiB per-file guard + 60 MiB archive ceiling in backup.py; inventory.py is the audit gate (JUNK must be empty).

---
D-011 · 2026-10-28 (T-019)
decision: worklog.md stays OUT of git (.gitignore) by design; its ONLY cloud copy is the /state/ live mirror written by backup.py (STATE_DOCS). A PROJECT-TREE.txt snapshot is generated from the exact backup file-set (scripts/gen_project_tree.py) and mirrored alongside it.
alternatives: commit worklog.md to git (mixes append-only narrative with code history; bloats diffs); keep it only on disk (lost in resets — exactly what happened on 2026-10-28).
reason: the 2026-10-28 partial reset destroyed the only local copy; it was recovered from Dropbox /state/worklog.md (v1.4.1-era) and reconstructed for T-018/T-019.
evidence: worklog.md in .gitignore line 5; backup.py STATE_DOCS; the recovery performed this session.
tradeoff: /state/ holds exactly one generation (latest overwrite) — worklog history between backups is not versioned remotely (mitigated by keep-3 checkpoints that each embed it).
consequence: every backup.py run MUST see worklog.md on disk; sessions must append entries before uploading.

## D-011 — T-020 history rewrite: tools/dotnet runtime removal (2026-10-29)
decision: a full .NET 8.0.33 runtime tree (tools/dotnet, 185 MB tracked / 76 MB in .git) had been accidentally committed by an interim restore commit and shipped inside every source checkpoint (128 MB archive — over the 62.9 MB CLOUD-POLICY budget). Untracked it, hardened .gitignore (tools/dotnet/, tools/llvm*/), and rewrote history with filter-branch (git rm --cached --ignore-unmatch tools/dotnet -- --all) + reflog expire + gc --prune=now.
hash map (old → new): v1.6.0 commit 301905d → 93ce179; untrack commit 68af332 → cf777b1; HEAD 7a99577 (inventory fix) is post-rewrite native. Tag v1.4.0 unchanged.
reason: zero references to tools/dotnet anywhere (grep across scripts/tests/src/docs/installer); the active toolchain lives in sdk/dotnet (RECOVERY §2). CLOUD-POLICY forbids runtimes/SDKs in the cloud.
evidence: tracked set 184.9 MB → 2.2 MB; .git 76 MB → 1.1 MB; git fsck clean post-rewrite; the 128 MB checkpoint was deleted from /backups/ and re-created at 1.5 MB; inventory JUNK=0 after also teaching inventory.py the nova-engine-*-win-x64.zip asset pattern.
tradeoff: all commit hashes after the restore-era commit changed again (second rewrite since D-010); the mapping above + backups supersede older hashes.
consequence: restore.py users must re-clone from the newest checkpoint; any bookmarked hashes older than this session are invalid.

---
D-012 · 2026-10-29 (T-021)
decision: The legacy parallel tree `nova/` (73 MB, its own engine/app/extensions/installer subtrees, "v0.1.0 vertical-slice" era) and `scripts/nova-sources.zip` were REMOVED from the repository; only the 4 narrative files survive in `docs/history/legacy-nova-tree/`. Concurrently, version authority was centralized: `version.json` is the single source of truth, `scripts/sync_versions.py --check` is the CI gate (audit job in version-audit.yml + windows-build.yml), and 9 stamped surfaces (Directory.Build.props, generated Nova.Domain/VersionInfo.cs, pubspec, generated build_version.dart, 2 extension manifests, installer.iss, extension MIN_ENGINE_VERSION constants in TS+JS) must always match it.
alternatives: keep nova/ as reference (grep-verified zero references; it described a superseded v0.1.0 line and made two histories compete); per-file manual version bumps (exactly the drift that left pubspec at 1.5.0, installer at 1.0.0, engine_status reporting "1.5.0" while the product was 1.6.0, and CI hardcoding 1.6.0 zip names).
reason: user directive "نسخه‌بندی یادت نره تا قاطی نشه نسخه‌ها باهم" (versioning so versions never get mixed) + UNIVERSAL-RULES §42 (single source of truth). Drift found on entry: pubspec 1.5.0+6 vs product 1.6.0; installer #define 1.0.0; Messages.EngineStatusResponse default "1.5.0"; UA literals 1.0–1.3 scattered in HttpProber/MediaPipeline; BT v-string "Nova DM 1.6.0" hardcoded in PeerWireClient; windows-build.yml referenced literal 1.6.0 zip names AND had a broken branch list (`ain, master]`); git tags for v1.5.0/v1.6.0 missing.
evidence: `python3 scripts/sync_versions.py --check` → 9/9 PASS + 3 cross-checks (DB schema vs NovaDatabase migrations; IPC vs Protocol.cs; no versioned artifact literals in workflows); boot fail-fast in EngineHostApp.LoadDb (schema mismatch refuses to start); VersionIntegrityTests asserts assembly version == VersionInfo.
tradeoff: version bumps now REQUIRE running sync_versions.py (CI blocks otherwise); generated files must never be hand-edited.
consequence: engine_status now reports the true engine version + channel + dbSchemaVersion + minEngineForExtension; extensions show a red banner when the engine is older than the stamped minimum; releases are tagged v<version> matching version.json exactly.

---
D-013 · 2026-10-29 (T-022)
decision: FREE-FOREVER is now a hard project invariant (PROJECT-RULES invariant 7, docs/FREE-FOREVER.md): NO part of Nova may require payment — no paid tiers/license keys/activation, no paid dependencies (free OSI licenses only), no paid signing/build/distribution services, no paywalled updates. Azure Trusted Signing (≈$9.99/month, researched T-021) is REJECTED; binary integrity stays with published SHA-256 sidecars (+ optional free self-signed certificates, recipe in docs/VERSIONING.md §8). Enforced by scripts/free_audit.py as a CI gate inside version-audit.yml (dependency allowlist = THIRD-PARTY-NOTICES.md; banned paid-service terms in code/active docs).
alternatives: adopt Trusted Signing (violates the directive — a paid service would become part of the build pipeline); OV/EV certificates from CA resellers ($200–600+/yr, same violation); unsigned-with-no-hashes (weaker integrity than the chosen path).
reason: user directive 2026-10-29 «برنامه نباید برای هیچ قسمتیش نیاز ب پرداخت هزینه باشه» (the program must not require payment for any part of it). Priority 2 (explicit user instruction) per UNIVERSAL-RULES 3.
evidence: python3 scripts/free_audit.py → PASS (13 recorded dependencies, all $0/free licenses incl. the SSH.NET row it caught missing on first run); grep audit before the change found paid references only in Trusted-Signing research/roadmap text (CHANGELOG/worklog/VERSIONING §8/KNOWN-LIMITATIONS #37/STATE/RESUME-POINT) — all rewritten or superseded, append-only history untouched.
tradeoff: Windows binaries keep the SmartScreen "unknown publisher" warning forever (documented KNOWN-LIMITATIONS #37); users verify integrity via .sha256 sidecars.
consequence: any future PR adding a paid dependency or paid service fails CI (free-audit step); distribution remains GitHub Releases (free); optional CWS store publishing is a developer convenience, never required to install/use Nova.

---
D-014 · 2026-10-30 (T-023)
decision: The parallel v0.1.0 tree was purged AGAIN (D-012 precedent, user approval «تأیید حذف nova/»). A 2026-10-29/30 session, working from a context-handoff summary that wrongly asserted "zero files ever created", rebuilt a full parallel product (engine + UI + extensions, 12/12 tests, nested git repo) inside `nova/` while the real product (v1.7.1, 242/242 tests) lived at the repo root. The gitlink entry `nova` (mode 160000, no .gitmodules) was removed from the index; the 149 MB working tree deleted; narrative preserved in docs/history/legacy-nova-tree/ (WORKLOG-2026-10-30-parallel-v0.1.0.md, KNOWN-LIMITATIONS-v0.1.0-parallel.md, README-v0.1.0-parallel.md). Cloud cleanup: the parallel session's uploads (/Releases/v0.1.0/nova-0.1.0-source.zip, /Worklog/2026-10-30-implementation-session.md) plus old-tree remnants (/nova/**) and stray root /builds v1.5.0 assets were deleted from Dropbox (surgical; user's own folders untouched). Root cause + lesson: session summaries are not truth; VERSIONS.md + git tags + the version-floor check are the anti-amnesia gate — run them FIRST. One genuine delta carried forward as a documented future item: PAC script evaluation (Jint, MIT) is NOT in v1.7.1 (docs/KNOWN-LIMITATIONS.md #18).
alternatives: merge the tree (rejected: audit showed a strict subset of v1.7.1 except PAC — 12 vs 242 tests, schema 1 vs 11, no Scheduler/NativeHost/DHT/preview/version-integrity); keep as reference (two competing histories — exactly what D-012 banned).
reason: user approval + D-012 precedent; SECURITY: nova/scripts/upload-dropbox.sh embedded the live Dropbox refresh token in plaintext (rule 79/95 violation) and was committed to the nested repo; its uploaded source zip carried the token into cloud storage (same account — marginal risk, but a live hygiene violation).
evidence: git ls-tree HEAD nova (160000 commit b048c86…); nested repo log (2 commits, "v0.1.0"); dropbox.py list /Releases /Worklog /nova /builds before/after (all present → all absent); local narrative files size-verified vs cloud copies before deletion (ROUND-12.md 4323 B, WORKLOG.md 1086 B exact); post-purge sync --check + free_audit PASS; single T-023 commit.
tradeoff: none material (narrative preserved; PAC future item recorded; ~48.6 MB cloud freed).
consequence: single history restored; `nova/` guard in .gitignore; the Dropbox namespace is now canonical-only (/Nova-Download-Manager/** + the user's own non-Nova folders).
