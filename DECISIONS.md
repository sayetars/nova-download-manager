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
D-008 · 2026-10-24 (T-010)
decision: Ship dual engine variants with a smart launcher (RUN-LINUX.sh) that swaps the self-contained NovaEngine into the bundle when no .NET 8 runtime is detected.
alternatives: self-contained only (150 MB+ tar, slower start); FDD only (blocks runtime-less testers).
reason: user asked for a build "to test" — friction must be near-zero on any Linux box.
evidence: clean-env (env -i, no dotnet) boot test — socket, ipc.token, nova.db all created; swap logic verified by size heuristic (apphost <1 MB vs single-file ~67 MB).
tradeoff: tarball grows to ~71 MB; swap mutates the extracted tree (documented).
consequence: one package serves both runtime and runtime-less machines.
