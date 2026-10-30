# THIRD-PARTY NOTICES — Nova Download Manager

License and cost audit of every dependency and external tool, per the
free-forever policy (docs/FREE-FOREVER.md — user directive 2026-10-29:
«برنامه نباید برای هیچ قسمتیش نیاز ب پرداخت هزینه باشه»).

**Rule: every dependency must have a column in the table below, and its license
must be a free, OSI-approved one with zero mandatory cost.** `scripts/free_audit.py`
parses this file as the allowlist — an unlisted dependency fails CI.

## Direct dependencies

| Package | Component | License | Cost |
|---|---|---|---|
| AngleSharp | C# engine (HTML parsing, Site Grabber) | MIT | $0 |
| BouncyCastle.Cryptography | C# engine (Metalink PGP verification) | MIT | $0 |
| FluentFTP | C# engine (FTP/FTPS) | MIT | $0 |
| Jint | C# engine (PAC proxy auto-config script evaluation, v1.8.0) | MIT | $0 |
| SSH.NET | C# engine (SFTP) | MIT | $0 |
| Microsoft.Data.Sqlite | C# engine (SQLite storage; bundles SQLite) | MIT (SQLite: public domain) | $0 |
| Microsoft.NET.Test.Sdk | tests only | MIT | $0 |
| xunit | tests only | Apache-2.0 | $0 |
| xunit.runner.visualstudio | tests only | MIT | $0 |
| cupertino_icons | Flutter app | MIT | $0 |
| flutter_riverpod | Flutter app | MIT | $0 |
| intl | Flutter app | BSD-3-Clause | $0 |
| flutter_lints | Flutter app (dev only) | BSD-3-Clause | $0 |
| esbuild | extensions build (dev only) | MIT | $0 |

## Platforms, runtimes and tools (not distributed inside Nova binaries unless noted)

| Tool | Role | License | Cost |
|---|---|---|---|
| .NET 8 SDK/runtime | engine platform | MIT | $0 |
| Flutter / Dart SDK | UI platform | BSD-3-Clause | $0 |
| Node.js | extension build only | MIT | $0 |
| Inno Setup | installer | Inno Setup License (free use + redistribution) | $0 |
| yt-dlp | optional external tool, user-provided, never bundled | Unlicense | $0 |
| FFmpeg | optional external tool, user-provided, never bundled | LGPL-2.1+ (GPL builds optional) | $0 |
| Git | VCS | GPL-2.0 | $0 |
| GitHub Actions (public repos) | CI | free tier | $0 |
| GitHub Releases | distribution + update manifest host | free | $0 |
| Dropbox API (free tier) | developer-side backup only — NOT a program requirement | free tier | $0 |

## Explicitly banned (policy, never a dependency)

| Item | Status |
|---|---|
| Azure Trusted Signing (~$9.99/mo) | REJECTED — docs/FREE-FOREVER.md §4, DECISIONS D-013 |
| Commercial UI/toolkit libraries (DevExpress, ComponentOne, …) | Banned |
| Premium hoster/decrypter services (Real-Debrid, Premiumize, AllDebrid, …) | Banned (also FEATURE-GAP G19: out of scope) |
| Paid proxy / VPN services | Banned (Nova only uses the user's own proxies, free of charge) |
| Paid code-signing certificates (OV/EV Authenticode resellers) | Not required — self-signed + SHA-256 sidecars instead |
| Ad SDKs / analytics-for-monetization | Banned |

## Adding a dependency (checklist)

1. Verify the license is free and OSI-approved (MIT, Apache-2.0, BSD, ISC,
   MPL-2.0, LGPL, GPL, Unlicense, CC0/public domain).
2. Add a row to the table above with the license and $0 cost.
3. Run `python3 scripts/free_audit.py` — it must pass.
4. If the license is anything else, or the component charges money for use,
   distribution, signing, or updates: **do not add it.**
