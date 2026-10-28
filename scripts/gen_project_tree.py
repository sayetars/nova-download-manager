#!/usr/bin/env python3
"""Nova DM — generate PROJECT-TREE.txt (the worklog-tree companion).

Renders the exact file-set that tools/dropbox/backup.py archives (git-tracked
files + memory docs + tooling), so the cloud always carries an accurate,
human-readable snapshot of the project structure. Output: PROJECT-TREE.txt at
repo root; mirrored to Dropbox /state/ by backup.py (STATE_DOCS).

Usage: python3 scripts/gen_project_tree.py
"""
from __future__ import annotations

import datetime as dt
import subprocess
import sys
from pathlib import Path

PROJECT = Path("/home/z/my-project")
sys.path.insert(0, str(PROJECT / "tools" / "dropbox"))
from backup import STATE_DOCS, _keep, collect_files, current_version_from_state  # noqa: E402


def render(files: list[Path]) -> str:
    rels = sorted(p.relative_to(PROJECT).as_posix() for p in files)
    lines: list[str] = []
    stack: list[str] = []  # dir parts of the last emitted path

    def depth_of(parts: list[str]) -> int:
        for i, (a, b) in enumerate(zip(stack, parts)):
            if a != b:
                return i
        return min(len(stack), len(parts))

    for rel in rels:
        parts = rel.split("/")
        d = depth_of(parts[:-1])
        while len(stack) > d:
            stack.pop()
        for seg in parts[d:-1]:
            stack.append(seg)
            lines.append("  " * (len(stack) - 1) + seg + "/")
        lines.append("  " * len(stack) + parts[-1])
    return "\n".join(lines) + "\n"


def main() -> int:
    version = current_version_from_state()
    files = [p for p in collect_files() if _keep(p)]  # match backup.py exactly
    head = subprocess.run(
        ["git", "-C", str(PROJECT), "rev-parse", "--short", "HEAD"],
        capture_output=True, text=True, check=True).stdout.strip()
    total = sum(p.stat().st_size for p in files)
    out = PROJECT / "PROJECT-TREE.txt"
    out.write_text(
        "# PROJECT-TREE — Nova Download Manager\n"
        f"# version {version} · git {head} · {dt.datetime.now().isoformat(timespec='seconds')}\n"
        f"# exact file-set carried by the Dropbox source checkpoint ({len(files)} files, "
        f"{total:,} bytes uncompressed)\n"
        "# (regenerate: python3 scripts/gen_project_tree.py)\n\n"
        + render(files),
        encoding="utf-8")
    print(f"PROJECT-TREE.txt written: {len(files)} entries, {total:,} bytes, "
          f"{out.stat().st_size:,} bytes file")
    return 0


if __name__ == "__main__":
    sys.exit(main())
