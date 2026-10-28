# CLOUD-POLICY — what may live in Dropbox (/Nova-Download-Manager)

User directive (2026-10-28, verbatim):

> «توی ابر فقط سورس ها و asset های هر نسخه و فایل های ورک لاگ و ورکلاگ تری و
> هرچیزی که نیازه برای از سر گیری پروژه رو آپلود کن — فقط؛ چون فضای ابر محدوده
> هرچیزی که دم دستت میاد رو آپلود نکن.»

Translation: in the cloud keep ONLY (1) sources, (2) per-version release assets,
(3) worklog files + the worklog/doc tree, (4) whatever is needed to resume the
project. Nothing else — cloud space is limited.

Account budget (measured 2026-10-28): 2.00 GiB total, 1.81 GiB used before
cleanup (~341 MiB free). Target: keep `/Nova-Download-Manager` under ~150 MiB.

## ALLOWED — and nothing else

| Remote path | Content | Retention |
|---|---|---|
| `/backups/nova-dm-src-<ver>-<ts>.tar.gz` (+ `.manifest.json`) | git-tracked sources + full `.git` history + memory docs + tooling | keep newest **3** |
| `/builds/nova-dm-<ver>-linux-x64.tar.gz` (+ `.sha256`) | released binary asset, per version | **current + previous** version |
| `/builds/nova-{chromium,firefox}-<ver>.zip` (+ `.sha256`) | released extension assets, per version | **current + previous** version |
| `/state/<doc>` | live mirror of: STATE.md, RESUME-POINT.md, worklog.md, VERSIONS.md, DECISIONS.md, RECOVERY.md, RULES-READ-LEDGER.md, PROJECT-RULES.md, UNIVERSAL-RULES.md, CLOUD-POLICY.md, PROJECT-TREE.txt | latest only (overwrite) |

## FORBIDDEN — never upload

- **Build outputs**: `publish/`, `bin/`, `obj/`, `build/`, `.dart_tool/`, `*.pdb`,
  self-contained engine binaries, `*.deps.json` copies of builds.
- **QA/evidence scratch**: screenshots, `evidence*/`, VLM JSON, one-off
  `reports/` deliverables (the user already has them locally).
- **Tool scratch**: `tool-results/`, `*.log`, `debug dumps`, `.backup-tmp/`.
- **Runtime data**: `*.db`, `*.db-shm`, `*.db-wal`, `*.novadl`, `*.part`.
- **Toolchains**: `sdk/`, `node_modules/`, GTK/LLVM extracts — re-fetchable
  via RECOVERY.md §2.
- **Secrets**: `.dropbox-credentials.json`, access-token caches (D-004).

## Enforcement (machine-checked)

1. `tools/dropbox/backup.py` — the ONLY source-checkpoint uploader:
   - collects `git ls-files` + STATE_DOCS + `tools/dropbox/*.py` + `scripts/*.sh`;
   - `EXCLUDE_DIRS`/`EXCLUDE_SUFFIXES` hard-exclude everything forbidden above;
   - per-file guard: any file > 10 MiB is skipped with a loud warning (content
     stays recoverable through `.git`);
   - archive sanity: warns if the checkpoint exceeds 60 MiB;
   - prunes to the newest 3 checkpoints (+ matching manifests).
2. `tools/dropbox/inventory.py` — audit gate: recursive listing + policy
   classifier + account usage. Before ending any session that uploaded,
   run `python3 tools/dropbox/inventory.py` and confirm the `JUNK` list is
   empty and the total fits the budget.
3. Release scripts upload exactly one tarball + two extension zips + their
   `.sha256` sidecars — nothing else.
4. Never upload anything to Dropbox outside `/Nova-Download-Manager`.

## History

- 2026-10-24 (T-007..T-017): original layout (keep-10, builds grew per release).
- 2026-10-28 (T-019): policy mandated by the user after the account hit
  1.81/2.00 GiB; two 405 MiB bloated v1.4.1 "source" backups (caused by
  accidentally-committed `publish/` binaries) deleted after a lean
  replacement was uploaded and verified; git history purged of build outputs
  (see DECISIONS.md D-010).
