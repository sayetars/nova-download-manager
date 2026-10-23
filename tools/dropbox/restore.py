#!/usr/bin/env python3
"""Nova DM — restore source/state from Dropbox after an environment reset.

Safe-restore contract (UNIVERSAL-RULES 34):
  - If the local tree is NEWER than the remote backup (higher version or
    newer git head), the restore refuses unless --force is given.
  - Archives are verified against the manifest SHA-256 before extraction.
  - Extraction uses tarfile filter='data' — blocks path traversal, absolute
    paths, device nodes and dangling links (rule 82).

Usage:
  restore.py list                     # show remote backups + state docs
  restore.py latest [--target DIR] [--force]
  restore.py state [--target DIR]     # only refresh state documents
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import sys
import tarfile
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from dropbox import NovaDropbox  # noqa: E402

PROJECT = Path("/home/z/my-project")
ROOT_REMOTE = "/Nova-Download-Manager"
STATE_DOCS = ["STATE.md", "RESUME-POINT.md", "worklog.md", "VERSIONS.md",
              "DECISIONS.md", "RECOVERY.md", "RULES-READ-LEDGER.md",
              "PROJECT-RULES.md", "UNIVERSAL-RULES.md"]

VER_RE = re.compile(r"v?(\d+)\.(\d+)\.(\d+)")


def ver_tuple(s: str) -> tuple[int, int, int]:
    m = VER_RE.search(s or "")
    if not m:
        return (0, 0, 0)
    return tuple(int(g) for g in m.groups())


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def remote_backups(dbx: NovaDropbox) -> list[dict]:
    entries = [e for e in dbx.list_folder(f"{ROOT_REMOTE}/backups")
               if e.get(".tag") == "file"]
    tars = {e["name"]: e for e in entries if e["name"].endswith(".tar.gz")}
    mans = {e["name"].replace(".manifest.json", ""): e
            for e in entries if e["name"].endswith(".manifest.json")}
    out = []
    for name, e in tars.items():
        base = name.replace(".tar.gz", "")
        out.append({
            "name": name,
            "size": e.get("size", 0),
            "modified": e.get("server_modified", ""),
            "has_manifest": base in mans,
        })
    out.sort(key=lambda x: x["modified"], reverse=True)
    return out


def fetch_manifest(dbx: NovaDropbox, tar_name: str, tmp: Path) -> dict | None:
    man_name = tar_name.replace(".tar.gz", ".manifest.json")
    try:
        p = dbx.download_file(f"{ROOT_REMOTE}/backups/{man_name}", tmp / man_name)
        return json.loads(p.read_text(encoding="utf-8"))
    except Exception as e:  # noqa: BLE001 — manifest is optional in legacy backups
        print(f"[restore] WARNING: no manifest for {tar_name}: {e}")
        return None


def local_version() -> tuple[str, str]:
    st = PROJECT / "STATE.md"
    ver = "unknown"
    if st.exists():
        for line in st.read_text(encoding="utf-8").splitlines():
            if line.upper().startswith("CURRENT VERSION:"):
                ver = line.split(":", 1)[1].strip()
                break
    head = ""
    git_head = PROJECT / ".git" / "HEAD"
    if git_head.exists():
        import subprocess
        try:
            head = subprocess.run(
                ["git", "-C", str(PROJECT), "rev-parse", "HEAD"],
                capture_output=True, text=True).stdout.strip()
        except Exception:  # noqa: BLE001
            pass
    return ver, head


def cmd_list(dbx: NovaDropbox) -> int:
    print("== remote source backups (newest first) ==")
    for b in remote_backups(dbx):
        print(f"  {b['modified']}  {b['size']:>10,}B  manifest={'yes' if b['has_manifest'] else 'no '}  {b['name']}")
    print("\n== remote state docs ==")
    for e in dbx.list_folder(f"{ROOT_REMOTE}/state"):
        if e.get(".tag") == "file":
            print(f"  {e.get('server_modified','')}  {e['name']}")
    print("\n== remote builds ==")
    try:
        for e in dbx.list_folder(f"{ROOT_REMOTE}/builds"):
            if e.get(".tag") == "file":
                print(f"  {e.get('server_modified','')}  {e.get('size',0):>12,}B  {e['name']}")
    except Exception:  # noqa: BLE001
        print("  (none)")
    return 0


def cmd_state(dbx: NovaDropbox, target: Path) -> int:
    target.mkdir(parents=True, exist_ok=True)
    got = 0
    for doc in STATE_DOCS:
        try:
            dbx.download_file(f"{ROOT_REMOTE}/state/{doc}", target / doc)
            got += 1
        except Exception:  # noqa: BLE001
            pass
    print(f"[restore] {got} state documents refreshed into {target}")
    return 0 if got else 4


def cmd_latest(dbx: NovaDropbox, target: Path, force: bool) -> int:
    backups = remote_backups(dbx)
    if not backups:
        print("[restore] ERROR: no remote backups found")
        return 3
    newest = backups[0]
    print(f"[restore] newest backup: {newest['name']} ({newest['size']:,}B, {newest['modified']})")

    # ---- safe-restore gate ----
    tmp = Path(tempfile.mkdtemp(prefix="nova-restore-"))
    manifest = fetch_manifest(dbx, newest["name"], tmp)
    lver, lhead = local_version()
    rver = manifest.get("version", "unknown") if manifest else "unknown"
    rhead = manifest.get("git_head", "") if manifest else ""
    if not force and target.exists() and (PROJECT / "STATE.md").exists():
        if ver_tuple(lver) > ver_tuple(rver):
            print(f"[restore] REFUSED: local version {lver} is NEWER than remote {rver}.")
            print("          Use --force to overwrite local work (destructive!).")
            return 5
        if lhead and rhead and lhead != rhead:
            print(f"[restore] WARNING: local git head {lhead[:12]} differs from remote {rhead[:12]};")
            print("           local work may not be backed up. Refusing without --force.")
            return 5

    # ---- download + verify + extract ----
    arch = dbx.download_file(f"{ROOT_REMOTE}/backups/{newest['name']}", tmp / newest["name"])
    if manifest:
        actual = sha256_of(arch)
        if actual != manifest.get("sha256"):
            print(f"[restore] ERROR: sha256 mismatch\n  expected {manifest.get('sha256')}\n  actual   {actual}")
            return 6
        print(f"[restore] sha256 verified ({actual[:16]}...)")

    target.mkdir(parents=True, exist_ok=True)
    with tarfile.open(arch, "r:gz") as tar:
        tar.extractall(target, filter="data")  # path-traversal safe
    print(f"[restore] extracted into {target}")

    # refresh state docs too
    cmd_state(dbx, target)

    if manifest:
        print(f"[restore] version={rver} git_head={rhead[:12]} "
              f"ts={manifest.get('timestamp_utc')}")
    print("[restore] NEXT: read STATE.md + RESUME-POINT.md, rebuild toolchain "
          "(see RECOVERY.md), then continue.")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("command", choices=["list", "latest", "state"])
    ap.add_argument("--target", default=str(PROJECT))
    ap.add_argument("--force", action="store_true",
                    help="overwrite newer local state (destructive)")
    args = ap.parse_args()
    dbx = NovaDropbox()
    if args.command == "list":
        return cmd_list(dbx)
    target = Path(args.target)
    if args.command == "state":
        return cmd_state(dbx, target)
    return cmd_latest(dbx, target, args.force)


if __name__ == "__main__":
    sys.exit(main())
