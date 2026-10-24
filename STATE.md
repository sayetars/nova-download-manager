# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.5.0
CURRENT TASK: T-018 (v1.5.0 G-track) COMPLETE — G17 partial preview (PreviewServer: loopback token-auth HTTP over the growing .novadl file, GET/HEAD/Range/416/suffix, unranged GETs clamped to the contiguous downloaded prefix, live or persisted segment maps; Preview dialog with live availability + copy URL + system-player handoff), G28 yt-dlp bridge (user-supplied binary; ytdlp_status/analyze(-J)/download(-g → normal engine pipeline, sequential forced for preview); DRM/live refused; Add-dialog quality panel EN/FA), G27 server profiles (EWMA speed α=.25 / TTFB / ok-fail per host fed by probe+slice+drain hooks, SQLite server_profile, fastest-first mirror ordering within metalink priority buckets, Settings table + reset), G25 TLS client certs (client_cert table, PFX/PEM/store, LocalCertificateSelectionCallback on both engine HttpClients, 10s cache, most-specific host match, mTLS-verified), G26 Metalink PGP (BouncyCastle detached verify, <signature type=pgp> + .sig/.asc sidecars, policy off|warn|require with pgp_failed/pgp_required/pgp_no_key, pgp_keyring table + Settings manager, task.pgp_signature/pgp_status columns); migration v10; TaskSnapshot +pgp fields; extensions 1.5.0.
CURRENT SUBSTEP: —
DONE: v1.5.0 tarball download/nova-dm-1.5.0-linux-x64.tar.gz (sha256 14562b5549c85d7061ada91b2ecc02768a6fb36e5fb233faaf57ea359cccfbec, 48397691 B); round-trip verified on Dropbox; extension zips 1.5.0 uploaded; 195/195 dotnet (+26 in V150Tests); flutter 13 passed + 1 skipped + 0 analyze errors; smoke PASS; parity 12/12 PASS; tarball clean-env PASS; git commit ef6a00f.
NEXT: (1) user test v1.5.0 tarball; (2) candidates: yt-dlp native-merge mode (video+audio via yt-dlp itself with progress IPC), in-app embedded player (media_kit — needs libmpv decision), DHT peer-store persistence across restarts, PEX flag-byte/uTP preference; (3) Windows build path when a Windows host is available.
BLOCKERS: none
LAST VERIFIED: 2026-10-25 — dotnet test 195/195; flutter analyze 0 errors; flutter test 13 passed + 1 skipped; smoke_test PASS; parity_smoke_test 12/12; tarball_cleanenv_test PASS (v1.5.0); Dropbox round-trip sha256 14562b55…ccfbec verified.
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain live at sdk/{dotnet,flutter} + ~/tools/{gtk3-dev,llvm-19}; venv cmake+ninja at ~/.venv; source scripts/env.sh; no root; no background process survival between tool calls; clang-19 symlinks present; 2 CPUs — some pre-existing engine tests flake under full-suite parallel load (pass in isolation; also seen at v1.4.1 baseline).
```

## DONE (high level)
- v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.0 / v1.4.1 releases (see VERSIONS.md).
- v1.5.0 (this session): G-track complete — partial preview / yt-dlp bridge / server profiles / client certs / metalink PGP; engine 169→195 tests; extensions 1.5.0; tarball + clean-env + parity green; Dropbox uploaded + round-trip verified.

## NEXT
1. USER: test v1.5.0 tarball → feedback.
2. v1.5.x candidates: yt-dlp native mode (merge formats, progress events), embedded player decision, DHT store persistence, uTP/PEX flag byte.
3. Windows release path when a Windows host is available (KNOWN-LIMITATIONS lists the gates).

## LAST VERIFIED STATE
- Evidence: download/nova-dm-1.5.0-linux-x64.tar.gz sha256 14562b5549c85d7061ada91b2ecc02768a6fb36e5fb233faaf57ea359cccfbec
- Tests: 195/195 engine, 13+1 flutter, smoke + parity 12/12 + clean-env PASS (2026-10-25).

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md.
