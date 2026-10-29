# Known Limitations (v0.1.0)

Honest, dated inventory. Everything here is a trade-off of the Linux-assisted
development model targeting Windows; none of these break the free-forever promise.

## Platform
- **Flutter Windows build is CI-only**: the dev machine is Linux, so the UI
  compiles via GitHub Actions (`windows-latest`). Engine is cross-compiled and
  locally self-tested on Linux.
- **Clipboard watcher** uses Win32 APIs (Windows only). On Linux it is a no-op.
- **Power actions** on Linux use `systemctl`; Windows uses `shutdown.exe`.
- **Windows integrated auth (NTLM/Kerberos)** piggybacks on
  `CredentialCache.DefaultCredentials`; explicit NTLM handshake not implemented.

## Engine
- **HLS live streams** are not supported (VOD with `#EXT-X-ENDLIST` only).
- **AES-128 only** for HLS; SAMPLE-AES / CENC not implemented.
- **HTTP/3** is best-effort (`RequestVersionOrHigher`); real QUIC depends on OS
  support in .NET 8 (experimental).
- **Site Grabber** does not evaluate JavaScript-rendered links (static DOM only)
  and does not fetch `robots.txt`.
- **PAC** evaluation has a 2s JS timeout per script and no WPAD/DHCP discovery.
- **SFTP resume** uses seek-based restart; server-side chunk verification absent.
- **Metalink PGP** verification shells out to `gpg` when present; otherwise logs
  and continues.
- **Retry policy** is per-download, not per-queue; backoff caps at 60 s.
- **Progress persistence**: in-memory counters are journaled into SQLite at state
  changes, so a crash can lose up to the last state snapshot (parts themselves
  survive; resume re-measures part file lengths).

## UI
- Persian strings use a first-party l10n table; full `flutter_localizations`
  (date/number formatting) lands with the CI build.
- No tray icon / global hotkeys yet (planned 0.2).
- Single window; no dark/light accent customization.

## Extensions
- **Firefox download takeover** depends on `downloads.onCreated` timing; very
  small files (<100 KB by default) are left to the browser.
- **Chrome takeover** requires the native host manifest registration
  (`scripts/install-native-host.ps1`) — not auto-installed.
- Video detection covers `<video>/<source>` + popular embed iframes; arbitrary
  DRM/EME players are out of scope.

## Distribution
- Code signing is configured in CI but needs a certificate from the project
  owner; unsigned builds show SmartScreen warnings on first run.
- Inno Setup / WiX installers are not yet generated (artifacts are portable
  zips in v0.1.0).
- `nova://` protocol registration and Explorer context-menu entries must be
  created by the (future) installer.
