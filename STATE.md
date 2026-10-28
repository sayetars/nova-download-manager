# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.6.0
CURRENT TASK: T-020 (completeness round + Windows deliverable) COMPLETE — v1.6.0 shipped: yt-dlp native-merge wired end-to-end (ytdlp_download native flag + UI merge-format selection + badge), DHT peer-store persistence (migration v11, DhtPeerStoreDb + registry, warm-start/flush), honest handshake flag bits (BEP-5 DHT advertised, peer flags telemetry), extensions 1.6.0, build.mjs version de-hardcoded; FIRST WINDOWS DELIVERABLE: nova-engine-1.6.0-win-x64.zip (101 MiB, cross-compiled from Linux — NovaEngine.exe + NovaBrowserHost.exe + nova.exe self-contained + extensions + register-nativehost.ps1 + README-WINDOWS.md) + .github/workflows/windows-build.yml (full Windows CI: engine tests ON Windows + flutter build windows + extensions + Inno Setup installer + artifacts + release-on-tag). Toolchain rebuilt this session per RECOVERY.md §2 (dotnet 8, flutter 3.47.7, GTK3-dev, cmake/ninja venv, clang-19 user-space — clang symlink fix applied). Tests: C# 208 (207/208, Validator_Change_Restarts_Download = documented timing flake of the 2-CPU sandbox, isolated re-runs green), Flutter 13 + 0 analyze errors. Linux tarball nova-dm-1.6.0-linux-x64.tar.gz (47 MiB) rebuilt. Cloud: v1.6.0 assets uploaded (win zip + linux tarball + ext zips + .sha256); v1.4.1 tarball + zips pruned per keep-2 policy.
CURRENT SUBSTEP: —
DONE: v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.0 / v1.4.1 / v1.5.0 / v1.6.0 releases (VERSIONS.md); T-020 (this session).
NEXT: (1) USER: download nova-engine-1.6.0-win-x64.zip from Dropbox builds/ — run engine + extensions on Windows, and/or push the repo to GitHub and let windows-build.yml produce the full installer; (2) Windows runtime feedback → v1.0.2-style fixes; (3) future candidates: embedded player (needs libmpv decision on Windows), uTP transport itself, tray on Windows, extension store publishing.
BLOCKERS: none (Windows GUI build still needs GH Actions or any Windows host — engine side is delivered)
LAST VERIFIED: 2026-10-29 — dotnet test 207/208 (1 flake documented); flutter analyze 0 errors / 13 tests; win-x64 cross-compile + packaging verified (binaries produced; execution needs a Windows machine); linux tarball packaged.
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: toolchain REBUILT 2026-10-29 — sdk/dotnet (8.0.427), sdk/flutter (3.47.7), ~/tools/gtk3-dev + ~/tools/llvm-19 (clang symlink workaround), ~/.venv (cmake/ninja); no root; no background process survival between tool calls; 2 CPUs. publish/ + download/ present (untracked, local testing only).
```

## DONE (high level)
- v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.0 / v1.4.1 / v1.5.0 releases (see VERSIONS.md).
- v1.5.0 (T-018): G-track complete — partial preview / yt-dlp bridge / server profiles / client certs / metalink PGP; engine 169→195 tests; extensions 1.5.0.
- T-019 (this session): cloud retention policy enforced — see START-HERE BLOCK.

## NEXT
1. USER: rebuild + test v1.5.0 (toolchain per RECOVERY.md §2, then scripts/linux/package_release.sh) → feedback.
2. v1.5.x candidates: yt-dlp native mode (merge formats, progress events), embedded player decision, DHT store persistence, uTP/PEX flag byte.
3. Windows release path when a Windows host is available (KNOWN-LIMITATIONS lists the gates).

## LAST VERIFIED STATE
- Evidence: git fsck clean; PROJECT-TREE.txt (264 files, 2.1 MiB uncompressed backup set); inventory.py post-cleanup output (JUNK=0).
- v1.5.0 product tests (2026-10-25, pre-reset): 195/195 engine, 13+1 flutter, 0 analyze; smoke + parity 12/12 + clean-env PASS — re-run after the toolchain rebuild.

## RULE VERSION
- UNIVERSAL-RULES.md — see START-HERE BLOCK. Ledger: RULES-READ-LEDGER.md.
