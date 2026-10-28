# Known Limitations (as of 2026-10-28)

Honest engineering inventory — what works, what is partial, what is not done.

## Verified working (Linux self-test, 23 unit tests + integration check)
- IPC framing round-trip, UDS transport, envelope serialization
- SQLite schema + migrations + task repository (insert/update/load/active filter)
- Download state machine transitions
- Native messaging protocol encode/decode
- HLS AES-128 decrypt helpers (round-trip tested)
- CCF/RSDF container parsing (basic)
- Scheduler day/time matching incl. Jalali parse
- **End-to-end**: engine daemon + IPC client + real HTTP download
  (httpbin.org 100KB, analyzed -> downloaded -> completed -> file on disk)
- **Cross-compile**: nova-host.exe PE32+ win-x64 self-contained builds from Linux

## Partial / needs real-Windows validation
- Named Pipes transport: compiles, logic mirrors UDS path, but only the
  UDS path runs in this Linux sandbox. Windows CI will exercise it.
- Native messaging host mode: protocol implemented + installer writes
  manifests and registry commands, but browser round-trip only testable
  on a real Windows install.
- Flutter UI: complete source written (task list, add-URL dialog, scheduler
  page with Jalali option, settings, preview page stub, l10n EN/FA ARB files,
  IPC client) — NOT compiled here (no Flutter SDK in sandbox; disk limits).
  The CI workflow generates the Windows runner and builds it.
- HLS/DASH engines: parsers + decrypt + segment fetch written; ffmpeg remux
  only when a binary is present; not exercised against a live stream in this
  session (sandbox network blocks most CDNs).

## Not implemented yet (roadmap)
1. BitTorrent / magnet worker (planned as aria2-compatible sidecar)
2. Metalink full XML parsing (currently URL list only)
3. TLS client certificates (mTLS) — model fields exist, SslStream wiring pending
4. SOCKS4/5 + PAC proxy evaluation in HttpClient pipeline
5. NTLM/Kerberos auth (basic/digest/bearer done)
6. Antivirus post-scan hook (setting exists, executor not wired)
7. Duplicate detection by hash for same-URL re-adds
8. Watch folder + RSS feed monitoring
9. JDownloader-style link decryption service for real DLC files
10. HTTP/3 (QUIC) — .NET 8 support is behind a flag; left for 1.1
11. Media keyserver fallbacks for DRM-marked streams (we refuse DRM downloads)
12. Incomplete-video preview player: engine side (sequential TS writes) done;
    player page uses media_kit but is untested on Windows
13. Tray/badge integration in Flutter (window_manager/tray_manager in pubspec,
    code not written)
14. Clipboard monitoring daemon-side (UI-side hook not wired)
15. Installer: Inno Setup script written; `uninstall-host` subcommand pending

## Environment constraints that shaped this build
- Build host is Linux: all Windows-only behaviors were implemented blind
  to spec (Named Pipes, registry, Inno Setup) and must be validated by the
  GitHub Actions windows-latest workflow included in the repo.
- Sandbox networking blocks most CDNs (speed.hetzner.de unreachable);
  integration test uses httpbin.org which is reachable.
