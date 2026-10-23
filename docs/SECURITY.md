# Security Model (docs/SECURITY.md)

## Trust boundaries

1. **Local user ⇄ engine** — named pipe, per-user secret token, user-ACL (Windows SID rule).
2. **Extension ⇄ browser host** — browser-enforced native-messaging spawning + our origin allow-list.
3. **Engine ⇄ internet** — HTTP(S) servers, untrusted by definition.

An attacker with the **same OS account** is out of scope (they can read the token file anyway). A different local account cannot connect: Windows pipe ACL + the random 64-hex token.

## Threat model and mitigations

| Threat | Mitigation |
|---|---|
| Malicious page feeds fake media URLs | Engine re-probes server-side; only real, parseable manifests become candidates |
| Path traversal via filename | Sanitizer keeps only the last path component; forbidden chars mapped; full-path containment checks on destinations |
| Overwrite of user files | Unique-final-name policy (`(2)` suffix); incomplete files never surfaced as complete |
| Credential leak on redirect | Custom redirect handler strips Authorization/Referer/Cookie cross-origin, ≤20 hops |
| Pipe hijack / unauthorized connect | Per-user ACL + 128-bit token; oversized/garbage frames drop the connection, never crash the engine |
| Compromised/renamed extension invoking the host | Host validates origin argv against `allowed_extension_ids`; unknown → exit(2), nothing spoken |
| Command injection through messages | The host implements a **fixed message allow-list**; it never executes shell commands or spawns arbitrary processes (only the engine binary at a fixed configured path, user level) |
| Resource exhaustion | 4 MiB frame cap, 16 pipe connections max, request timeouts, retry backoff caps |
| Sensitive data in logs | Tokens/cookies never logged; `stdout` of the host is protocol-only; logs (stderr) carry messages without headers |
| Runaway disk usage | Pre-start disk-space gate with headroom; per-task segment tracking |
| Auto-execution of downloaded files | Never — the engine only opens/launches nothing; the UI's "open file" action is an explicit user command |
| TLS interception | Standard .NET TLS validation; no downgrade options exposed |

## DRM & access-control stance

Nova **refuses** protected content: HLS with `EXT-X-KEY != NONE`, DASH with `ContentProtection`, EME `encrypted` events in the page. There is no bypass path in the codebase and none will be added.

## Privacy

- No account, no telemetry, no external services.
- All data stays in `<data dir>` (SQLite + logs). Referers are kept only for retry correctness; cookies/tokens are never persisted or logged.
- History retention is user-configurable (including "don't save"); purge is exposed in UI and IPC.

## Reporting

Security issues: see the repository's issue tracker. Please do not open public issues for vulnerabilities if the repository becomes public — contact the maintainers directly.

## Dependency posture

- Runtime: .NET 8 (LTS), Microsoft.Data.Sqlite — permissive licenses.
- Flutter app: riverpod, flutter_localizations, intl — BSD-3-style.
- Optional external binary: **ffmpeg** (detected on PATH only, never bundled) for DASH muxing — its LGPL/GPL implications are the reason it stays optional and unbundled.
