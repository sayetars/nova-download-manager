#!/usr/bin/env python3
"""T-019 one-off: enforce CLOUD-POLICY.md on the existing Dropbox content.

Order of operations (safety):
  1. upload the v1.5.0 release assets (current version) — done ONLY after the
     lean v1.5.0 source checkpoint was uploaded + verified (precondition);
  2. delete junk files + stray folders (evidence/, reports/, docs/, extensions/);
  3. delete the two bloated v1.4.1 source backups + manifests (−405 MiB),
     now fully replaced by the lean v1.5.0 checkpoint.

Idempotent: 404/not_found deletions are treated as success.
"""
from __future__ import annotations

import hashlib
import sys
from pathlib import Path

sys.path.insert(0, "/home/z/my-project/tools/dropbox")
from dropbox import DropboxError, NovaDropbox  # noqa: E402

ROOT = "/Nova-Download-Manager"
PROJECT = Path("/home/z/my-project")

UPLOADS = [
    (PROJECT / "extensions/chromium/nova-chromium-1.5.0.zip",
     f"{ROOT}/builds/nova-chromium-1.5.0.zip"),
    (PROJECT / "extensions/firefox/nova-firefox-1.5.0.zip",
     f"{ROOT}/builds/nova-firefox-1.5.0.zip"),
]

JUNK_FILES = [
    f"{ROOT}/docs/IDM-PARITY.md",
    f"{ROOT}/evidence/v1.2.0/nova-linux-ui-download.png",
    f"{ROOT}/evidence/v1.2.0/nova-mini-window.png",
    f"{ROOT}/evidence/v1.2.0/evidence-ui-vlm.json",
    f"{ROOT}/evidence/v1.2.0/evidence-mini-vlm.json",
    f"{ROOT}/extensions/nova-firefox-1.2.0.zip",
    f"{ROOT}/extensions/nova-chromium-1.2.0.zip",
    f"{ROOT}/reports/Nova-IDM-Parity-Gap-Analysis-FA.pdf",
    f"{ROOT}/reports/Nova-IDM-Parity-Cover-Preview.png",
    f"{ROOT}/reports/Nova-IDM-Parity-Gap-Analysis-FA.html",
    f"{ROOT}/builds/evidence-nova-linux-ui-download.png",
    f"{ROOT}/builds/evidence-nova-linux-ui.png",
]

JUNK_FOLDERS = [
    f"{ROOT}/evidence",
    f"{ROOT}/reports",
    f"{ROOT}/extensions",
    f"{ROOT}/docs",
]

OLD_BLOATED_BACKUPS = [
    f"{ROOT}/backups/nova-dm-src-v1.4.1-20261024-210533.tar.gz",
    f"{ROOT}/backups/nova-dm-src-v1.4.1-20261024-210533.manifest.json",
    f"{ROOT}/backups/nova-dm-src-v1.4.1-20261024-193726.tar.gz",
    f"{ROOT}/backups/nova-dm-src-v1.4.1-20261024-193726.manifest.json",
]


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def safe_delete(dbx: NovaDropbox, remote: str) -> None:
    try:
        dbx.delete(remote)
        print(f"  deleted  {remote}")
    except DropboxError as e:
        if e.status == 409 and "not_found" in (e.summary or ""):
            print(f"  already gone  {remote}")
        else:
            raise


def main() -> int:
    dbx = NovaDropbox()

    print("[1] verify precondition: lean v1.5.0 checkpoint present")
    checkpoints = [e for e in dbx.list_folder(f"{ROOT}/backups")
                   if e.get("name", "").startswith("nova-dm-src-v1.5.0")]
    if not any(e["name"].endswith(".tar.gz") for e in checkpoints):
        print("ERROR: lean v1.5.0 checkpoint missing — aborting deletions")
        return 3
    print(f"  ok: {[e['name'] for e in checkpoints]}")

    print("[2] upload v1.5.0 release assets (+ .sha256 sidecars)")
    for local, remote in UPLOADS:
        if not local.exists():
            print(f"ERROR: local asset missing: {local}")
            return 4
        dbx.upload_file(local, remote)
        digest = sha256_of(local)
        side_local = local.parent / (local.name + ".sha256")
        side_local.write_text(f"{digest}  {local.name}\n", encoding="utf-8")
        dbx.upload_file(side_local, remote + ".sha256")
        print(f"  uploaded {remote} (sha256 {digest[:16]}…) + .sha256")

    print("[3] delete junk files")
    for p in JUNK_FILES:
        safe_delete(dbx, p)

    print("[4] delete now-empty stray folders")
    for d in JUNK_FOLDERS:
        safe_delete(dbx, d)

    print("[5] delete bloated v1.4.1 source backups (replaced by lean checkpoint)")
    for p in OLD_BLOATED_BACKUPS:
        safe_delete(dbx, p)

    print("DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
