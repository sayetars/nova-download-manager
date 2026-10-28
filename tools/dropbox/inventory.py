#!/usr/bin/env python3
"""Nova DM — Dropbox inventory + cloud-retention policy checker.

Answers: "what is in the cloud, how much space does it use, and what does the
retention policy say about each file?"

Policy (user directive 2026-10-28 — see CLOUD-POLICY.md):
  KEEP  sources         /backups/nova-dm-src-*.tar.gz (+ .manifest.json)
  KEEP  resume docs     /state/<STATE_DOCS>            (worklog tree + memory)
  KEEP  version assets  /builds/nova-dm-<v>-linux-x64.tar.gz, extension zips, .sha256
  JUNK  everything else (evidence screenshots, debug dumps, stray files)

Usage:
  python3 inventory.py                     # human report
  python3 inventory.py --json              # machine-readable
  python3 inventory.py --folders A,B       # only some folders
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from dropbox import NovaDropbox  # noqa: E402

ROOT = "/Nova-Download-Manager"

STATE_DOCS = {
    "STATE.md", "RESUME-POINT.md", "worklog.md", "VERSIONS.md",
    "DECISIONS.md", "RECOVERY.md", "RULES-READ-LEDGER.md",
    "PROJECT-RULES.md", "UNIVERSAL-RULES.md", "PROJECT-TREE.txt",
    "CLOUD-POLICY.md",
}


def classify(path: str) -> tuple[str, str]:
    """Return (verdict, reason) for a remote path."""
    p = path[len(ROOT):] if path.startswith(ROOT) else path
    parts = [s for s in p.split("/") if s]
    if not parts:
        return ("keep", "root")
    top = parts[0]
    name = parts[-1]
    if top == "backups":
        if name.startswith("nova-dm-src-") and (
            name.endswith(".tar.gz") or name.endswith(".manifest.json")
        ):
            return ("keep", "source checkpoint")
        return ("junk", "backups/: not a source checkpoint")
    if top == "state":
        if name in STATE_DOCS:
            return ("keep", "resume/worklog doc")
        return ("junk", "state/: not a known resume doc")
    if top == "builds":
        ok = (
            (name.startswith("nova-dm-") and name.endswith("-linux-x64.tar.gz"))
            or (name.startswith("nova-engine-") and name.endswith("-win-x64.zip"))
            or (name.startswith("nova-chromium-") and name.endswith(".zip"))
            or (name.startswith("nova-firefox-") and name.endswith(".zip"))
            or name.endswith(".sha256")
        )
        return ("keep" if ok else "junk",
                "version release asset" if ok else "builds/: not a release asset")
    return ("junk", f"{top}/: outside the policy layout")


def human(n: float) -> str:
    for unit in ("B", "KiB", "MiB", "GiB"):
        if n < 1024 or unit == "GiB":
            return f"{n:,.1f} {unit}" if unit != "B" else f"{int(n):,} B"
        n /= 1024
    return f"{n:,.1f} GiB"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--root", default=ROOT)
    ap.add_argument("--json", action="store_true", help="machine-readable output")
    ap.add_argument("--folders", default="", help="comma-separated top folders only")
    args = ap.parse_args()

    dbx = NovaDropbox()
    entries = dbx.list_folder(args.root, recursive=True)
    files = [e for e in entries if e.get(".tag") == "file"]
    folders = [e for e in entries if e.get(".tag") == "folder"]

    only = {f.strip().strip("/") for f in args.folders.split(",") if f.strip()}
    if only:
        files = [e for e in files
                 if e["path_display"][len(args.root):].lstrip("/").split("/")[0] in only]

    for e in files:
        v, why = classify(e["path_display"])
        e["_verdict"] = v
        e["_why"] = why

    total = sum(e.get("size", 0) for e in files)
    junk = [e for e in files if e["_verdict"] == "junk"]
    junk_bytes = sum(e.get("size", 0) for e in junk)

    try:
        usage = json.loads(dbx._request(
            "https://api.dropboxapi.com/2/users/get_space_usage"))
        used = usage.get("used", 0)
        alloc = (usage.get("allocation") or {}).get("individual", {}).get("allocated", 0)
        space = {"used": used, "allocated": alloc,
                 "free": alloc - used if alloc else None}
    except Exception as exc:  # noqa: BLE001
        space = {"error": str(exc)}

    if args.json:
        print(json.dumps({
            "root": args.root,
            "file_count": len(files),
            "folder_count": len(folders),
            "total_bytes": total,
            "junk_bytes": junk_bytes,
            "account_space": space,
            "files": [{
                "path": e["path_display"],
                "size": e.get("size", 0),
                "verdict": e["_verdict"],
                "why": e["_why"],
                "modified": e.get("server_modified", ""),
            } for e in sorted(files, key=lambda x: x["path_display"])],
        }, indent=2))
        return 0

    print(f"== Dropbox inventory: {args.root} ==")
    print(f"files: {len(files)}   folders: {len(folders)}   total: {human(total)}")
    if space.get("allocated"):
        print(f"account: {human(space['used'])} used of {human(space['allocated'])}"
              f"   (free {human(space['free'])})")
    print()
    by_folder: dict[str, list] = {}
    for e in files:
        rel = e["path_display"][len(args.root):].lstrip("/")
        top = rel.split("/")[0] if "/" in rel else "(root)"
        by_folder.setdefault(top, []).append(e)
    for top in sorted(by_folder):
        es = by_folder[top]
        sz = sum(e.get("size", 0) for e in es)
        print(f"[{top}]  {len(es)} files, {human(sz)}")
        for e in sorted(es, key=lambda x: -x.get("size", 0)):
            mark = "KEEP" if e["_verdict"] == "keep" else "JUNK"
            print(f"   {mark:<4} {human(e.get('size', 0)):>12}  {e['path_display']}"
                  f"   ({e['_why']})")
        print()
    print(f"== junk candidates: {len(junk)} files, {human(junk_bytes)} reclaimable ==")
    for e in sorted(junk, key=lambda x: -x.get("size", 0)):
        print(f"   DEL? {human(e.get('size', 0)):>12}  {e['path_display']}"
              f"   ({e['_why']})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
