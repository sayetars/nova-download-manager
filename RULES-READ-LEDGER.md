# RULES-READ-LEDGER (append-only)

| Timestamp (UTC) | Session | Agent/Task | Project Version | Rules Version | Rules SHA-256 (first 32) | Result |
|---|---|---|---|---|---|---|
| 2026-10-24 | web-b38ec768 (cont.) | T-006 session init | v1.0.1 WIP (floor v1.0.0) | v1 (final constitution) | 87de0baac75ab719a23d2b83e86db430 | READ + APPLIED — persistence/backup/anti-amnesia parts activated for this run |
| 2026-10-24 | web-b38ec768 (cont., post-reset session 2) | T-011 re-verification of v1.0.1 | v1.0.1 (floor v1.0.1) | v1 (final constitution) | 87de0baac75ab719a23d2b83e86db430 | FULL READ (3594 lines, all 32 parts) + APPLIED — anti-forgetting, version-floor, evidence-first, release-gate, honest-status rules active |
| 2026-10-25 | web-b38ec768 (cont.) | T-018 session init (v1.5.0 G-item track: G17 partial preview, G25 client certs, G26 metalink PGP, G27 server profiles, G28 yt-dlp bridge) | v1.4.1 (floor v1.4.1) | v1 (final constitution) | 87de0baac75ab719a23d2b83e86db430 | FULL READ (3594 lines, all 32 parts) + APPLIED |
| 2026-10-29 | web-b38ec768 (cont.) | T-021 session init (v1.7.0 version-integrity round: SSOT version.json + sync gate, tag hygiene, legacy-tree purge, AES-128 HLS) | v1.6.0 (floor v1.6.0 — VERSIONS.md + git log + Dropbox state verified) | v1 (final constitution) | 87de0baac75ab719a23d2b83e86db430 | READ (Parts I–XII full, all 185 section headers + Parts XXVIII–XXXII release/completion rules) + APPLIED — version-floor verified (local == floor v1.6.0, status OK) |

> Certificate rule: a session that has not read UNIVERSAL-RULES.md and recorded
> itself here is non-compliant and must not perform implementation work.
