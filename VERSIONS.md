# VERSIONS — Nova Download Manager

| Version | Date | Status | Notes |
|---------|------|--------|-------|
| v1.0.0 | 2026-10-23 | RELEASED (Windows path) | Engine + Flutter UI + extensions + installer + docs. 43/43 C# tests ×2, 8/8 Flutter tests, 0 analyze errors. Commits up to b06e865 + checkpoint c0dfa89. |
| v1.0.1 | 2026-10-23 | RELEASED (Linux x64) | First Linux build. 43/43 + 8/8 + 0 errors re-verified on Linux; release-bundle smoke suite PASS; self-contained no-runtime boot PASS; tar.gz + sha256 on Dropbox (round-trip verified). |
| v1.0.2 | — | NOT STARTED | Reserved for user-test feedback fixes. |

## Version floor logic
- Trusted sources: this file + git HEAD + Dropbox /Nova-Download-Manager/state/VERSIONS.md + CHANGELOG.md.
- Floor = highest trusted version present across sources. Working below floor is forbidden.
- After any reset: compare local VERSIONS.md vs the Dropbox mirror; converge to the newest, then continue.

## Builds published to Dropbox (/Nova-Download-Manager/builds/)
| Artifact | Version | Status |
|----------|---------|--------|
| nova-dm-1.0.1-linux-x64.tar.gz (70,586,472 B, sha256 e0199b86…c603e) | v1.0.1 | PUBLISHED 2026-10-23, round-trip verified |
| evidence-nova-linux-ui.png / evidence-nova-linux-ui-download.png | v1.0.1 | PUBLISHED (visual QA evidence) |
