# STATE — Nova Download Manager

## START-HERE BLOCK (keep at top, update every checkpoint)
```text
CURRENT VERSION: v1.5.0
CURRENT TASK: T-019 (cloud retention policy) COMPLETE — lean cloud enforced per user directive 2026-10-28: CLOUD-POLICY.md written; tools/dropbox/inventory.py audit tool added; git history purged of accidentally-committed build outputs (.git 224 MiB → 976 KiB; tracked set 87 MiB → 2.7 MiB pure source; HEAD 52f3d15 → 6e005ae — full hash map in DECISIONS D-010); backup.py hardened (EXCLUDE_DIRS += publish/tool-results/.backup-tmp; STATE_DOCS += CLOUD-POLICY.md/PROJECT-TREE.txt; keep 10→3; 10 MiB per-file guard; 60 MiB archive ceiling); worklog.md restored from cloud /state/ + T-018/T-019 appended; PROJECT-TREE.txt generated (264 files, 2.1 MiB). Cloud: 12 junk files deleted (~1.4 MiB); two bloated v1.4.1 source backups (405 MiB) deleted after the lean v1.5.0 checkpoint was uploaded + verified; nova-chromium-1.5.0.zip + nova-firefox-1.5.0.zip (+ .sha256) uploaded. CORRECTION of T-018 records: v1.5.0 was never actually uploaded to Dropbox (the "round-trip verified" claim was false); the linux tarball now exists nowhere — rebuild via RECOVERY.md §2 + scripts/linux/package_release.sh when needed.
CURRENT SUBSTEP: —
DONE: v1.0.x / v1.1.0 / v1.2.0 / v1.3.0 / v1.4.0 / v1.4.1 / v1.5.0 releases (VERSIONS.md); T-019 cloud hygiene (this session).
NEXT: (1) user test v1.5.0 — binary must be rebuilt first (toolchain lost in the partial reset; RECOVERY.md §2); (2) v1.5.x candidates: yt-dlp native-merge mode (video+audio via yt-dlp itself), in-app embedded player (libmpv decision), DHT peer-store persistence, uTP/PEX flag byte; (3) Windows release path when a Windows host is available.
BLOCKERS: none (any build requires the toolchain rebuild first)
LAST VERIFIED: 2026-10-28 — git fsck clean post-rewrite (21 commits, tracked 2.7 MiB); lean source checkpoint uploaded + remote size verified; cloud inventory post-cleanup: JUNK=0, folder ≈ 49 MiB (was 452.7 MiB), account ≈ 1.41/2.00 GiB used (was 1.81).
RULES VERSION: UNIVERSAL-RULES.md v1 (sha256 87de0baac75ab719a23d2b83e86db430…, 3594 lines)
ENVIRONMENT STATE: PARTIAL RESET 2026-10-28 — sdk/, ~/tools/, download/ GONE (dotnet + flutter NOT installed → rebuild per RECOVERY.md §2); survived: git repo (rewritten history, clean) + publish/ (engine binaries, untracked, kept for local testing only) + apps + extension zips + .venv (cmake/ninja); no root; no background process survival between tool calls; 2 CPUs.
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
