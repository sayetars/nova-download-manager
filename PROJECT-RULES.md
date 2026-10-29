# Nova DM — Project Rules (layer under UNIVERSAL-RULES.md)

Priority: UNIVERSAL-RULES.md > this file. Never weaken either silently.

## Identity
- Product: Nova Download Manager — IDM-grade downloader.
- Components: Flutter desktop UI (apps/nova_desktop), C#/.NET 8 engine (src/*), Chromium + Firefox extensions (extensions/*), BrowserHost native-messaging bridge.
- Source of truth for task state: SQLite ONLY (never UI memory).

## Invariants (non-negotiable)
1. IPC = named pipes / unix domain sockets only — NEVER TCP for local IPC. Linux path is the .NET CoreFxPipe mapping (<tempdir>/CoreFxPipe_<name>).
2. nipc/1 protocol framing: 4-byte LE length-prefixed JSON, versioned handshake, per-user token auth, 4 MiB frame cap — never bypass the handshake.
3. No DRM circumvention, ever. HLS/DASH parsers refuse DRM/live streams by design — keep that.
4. Path-traversal defense on every user/browser-supplied filename (FilenamePolicy + tar extraction filter).
5. Never log secrets/tokens (engine token, refresh tokens) — redact in logs.
6. Bilingual EN + FA (RTL + Persian digits) in UI strings; keep strings.dart the single authority.
7. Free-forever (user directive 2026-10-29): NO part of the program may require payment — no paid tiers/keys/activation, no paid dependencies (free OSI licenses only, audited by scripts/free_audit.py in CI), no paid signing/build/distribution services (Trusted Signing rejected — D-013), no paywalled updates. Full policy: docs/FREE-FOREVER.md.

## Versioning & history
- Semantic versioning; CHANGELOG.md is the human log; VERSIONS.md is the machine floor.
- Global worklog task numbering never restarts.
- Every meaningful substep: update RESUME-POINT.md before risky work.

## Persistence policy (user-mandated 2026-10-28 — full rules: CLOUD-POLICY.md)
- Dropbox root: /Nova-Download-Manager
  - backups/ — LEAN source checkpoints (tar.gz + manifest.json, keep 3; build outputs / tool-results / evidence NEVER enter them)
  - builds/ — current + previous release assets + .sha256 sidecars only
  - state/ — live mirror of memory + worklog docs (STATE, RESUME-POINT, worklog, VERSIONS, DECISIONS, RECOVERY, ledgers, CLOUD-POLICY, PROJECT-TREE)
- Forbidden in the cloud: publish/, bin/obj/build outputs, evidence/screenshots, tool-results/, SDKs, databases, logs, credentials.
- Audit gate before ending any upload session: `python3 tools/dropbox/inventory.py` → JUNK must be empty and the folder within budget (~150 MiB).
- Credentials: tools/dropbox/.dropbox-credentials.json (chmod 600, gitignored, NEVER inside archives or builds).
- After any environment reset: run restore.py latest → read RECOVERY.md → rebuild toolchain → continue.

## Environment notes (this sandbox)
- NO root, NO background process survival between tool calls → long operations must be chunked + resumable in foreground.
- SDKs install under /home/z/my-project/sdk/ (gitignored): dotnet 8.0 at sdk/dotnet, flutter 3.47.7 at sdk/flutter.
- GTK3 dev via scripts/linux/setup_gtk_dev.sh → ~/tools/gtk3-dev (user-space apt-get download + dpkg -x).
