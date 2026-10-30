#!/usr/bin/env python3
"""Nova DM — source checkpoint backup to Dropbox.

Creates a tar.gz of: all git-tracked files + project memory docs + tooling +
the .git directory (full history), then uploads it with a JSON manifest and
mirrors the live state documents.

Security (UNIVERSAL-RULES 79/95):
  - Credentials file and access-token cache are ALWAYS excluded.
  - Databases, build outputs, SDKs, node_modules, skills/ are excluded.
  - Manifest carries SHA-256; restore verifies it before extraction.

Layout on Dropbox (retention rules: CLOUD-POLICY.md — the user's directive):
  /Nova-Download-Manager/backups/nova-dm-src-<version>-<YYYYMMDD-HHMMSS>.tar.gz  (keep 3)
  /Nova-Download-Manager/backups/nova-dm-src-...manifest.json
  /Nova-Download-Manager/state/<doc>          (live state + worklog tree mirrors)
  /Nova-Download-Manager/builds/               (release assets only, via build scripts)
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import subprocess
import sys
import tarfile
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from dropbox import NovaDropbox  # noqa: E402

PROJECT = Path("/home/z/my-project")
ROOT_REMOTE = "/Nova-Download-Manager"
STATE_DOCS = [
    "STATE.md", "RESUME-POINT.md", "worklog.md", "VERSIONS.md",
    "DECISIONS.md", "RECOVERY.md", "RULES-READ-LEDGER.md",
    "PROJECT-RULES.md", "UNIVERSAL-RULES.md", "CLOUD-POLICY.md",
    "PROJECT-TREE.txt",
]
EXCLUDE_NAMES = {".dropbox-credentials.json", ".access-token.json",
                  ".github-credentials.json"}
EXCLUDE_DIRS = {"sdk", "node_modules", "upload", "download", "skills",
                ".backup-tmp", "__pycache__", "publish", "tool-results"}
EXCLUDE_SUFFIXES = (".db", ".db-shm", ".db-wal", ".novadl", ".log", ".part",
                    ".pem", ".private-key")
# CLOUD-POLICY guards (user directive 2026-10-28): the checkpoint must stay lean.
MAX_FILE_BYTES = 10 * 1024 * 1024     # no single archived file > 10 MiB
MAX_ARCHIVE_BYTES = 60 * 1024 * 1024  # sanity ceiling for the whole checkpoint


def git(*args) -> str:
    return subprocess.run(["git", "-C", str(PROJECT), *args],
                          capture_output=True, text=True, check=True).stdout.strip()


def version_of() -> str:
    try:
        return git("describe", "--tags", "--always")
    except subprocess.CalledProcessError:
        return "noversion"


def current_version_from_state() -> str:
    st = PROJECT / "STATE.md"
    if st.exists():
        for line in st.read_text(encoding="utf-8").splitlines():
            if line.upper().startswith("CURRENT VERSION:"):
                raw = line.split(":", 1)[1].strip()
                # machine-clean version: first token, filesystem-safe chars
                return "".join(c for c in raw.split()[0] if c.isalnum() or c in "._-")
    return "unknown"


def collect_files() -> list[Path]:
    files: set[Path] = set()
    for rel in git("ls-files").splitlines():
        if not rel:
            continue
        p = PROJECT / rel
        if p.is_file():
            files.add(p)
    # always include project memory docs + tooling even when gitignored
    for doc in STATE_DOCS:
        p = PROJECT / doc
        if p.is_file():
            files.add(p)
    for p in (PROJECT / "tools" / "dropbox").glob("*.py"):
        files.add(p)
    for shell in (PROJECT / "scripts").glob("*.sh"):
        files.add(shell)
    return sorted(files)


def _keep(p: Path) -> bool:
    rel = p.relative_to(PROJECT)
    if rel.parts and rel.parts[0] == ".git":
        return True  # full history snapshot
    if p.name in EXCLUDE_NAMES:
        return False
    if any(part in EXCLUDE_DIRS for part in rel.parts):
        return False
    if p.suffix in EXCLUDE_SUFFIXES:
        return False
    return True


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def build_archive(out: Path, files: list[Path]) -> tuple[str, int]:
    with tarfile.open(out, "w:gz") as tar:
        for p in files:
            tar.add(p, arcname=str(p.relative_to(PROJECT)), recursive=False)
        # include .git with recursion (history), excluding nothing sensitive:
        # .git contains only repo objects/refs — 1.9 MB here.
        tar.add(PROJECT / ".git", arcname=".git")
    return sha256_of(out), out.stat().st_size


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--keep", type=int, default=3,
                    help="retain the N newest remote source backups (0 = never prune)")
    ap.add_argument("--dry-run", action="store_true",
                    help="build archive locally, skip upload")
    args = ap.parse_args()

    ts = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    version = current_version_from_state()
    safe_version = version.replace("/", "-") or "unknown"
    name = f"nova-dm-src-{safe_version}-{ts}"
    arch = Path(tempfile.gettempdir()) / f"{name}.tar.gz"

    files = collect_files()
    files = [p for p in files if _keep(p)]
    # CLOUD-POLICY per-file guard: never archive a suspiciously large file.
    for p in files:
        if p.stat().st_size > MAX_FILE_BYTES:
            print(f"[backup] WARNING: skipping oversized {p} "
                  f"({p.stat().st_size:,} B > {MAX_FILE_BYTES:,} B); "
                  f"content stays recoverable via .git if tracked",
                  file=sys.stderr)
    files = [p for p in files if p.stat().st_size <= MAX_FILE_BYTES]
    if not files:
        print("ERROR: no files collected — refusing to upload empty backup")
        return 3

    print(f"[backup] {len(files)} files, version={version}")
    digest, size = build_archive(arch, files)
    print(f"[backup] archive {arch} ({size:,} bytes, sha256 {digest[:16]}...)")
    if size > MAX_ARCHIVE_BYTES:
        print(f"[backup] WARNING: archive {size:,} B exceeds the "
              f"{MAX_ARCHIVE_BYTES:,} B CLOUD-POLICY budget — investigate "
              f"before relying on this checkpoint", file=sys.stderr)

    manifest = {
        "kind": "nova-dm-source-checkpoint",
        "version": version,
        "git_head": git("rev-parse", "HEAD"),
        "git_branch": git("rev-parse", "--abbrev-ref", "HEAD"),
        "git_dirty": git("status", "--porcelain").splitlines(),
        "timestamp_utc": dt.datetime.now(dt.UTC).isoformat(timespec="seconds"),
        "file_count": len(files),
        "size": size,
        "sha256": digest,
        "archive": arch.name,
    }
    print(f"[backup] git_head={manifest['git_head'][:12]} "
          f"dirty_files={len(manifest['git_dirty'])}")

    if args.dry_run:
        print("[backup] dry-run: no upload")
        return 0

    dbx = NovaDropbox()
    dbx.ensure_folder(f"{ROOT_REMOTE}/backups")
    dbx.ensure_folder(f"{ROOT_REMOTE}/state")

    dbx.upload_file(arch, f"{ROOT_REMOTE}/backups/{arch.name}")
    man_name = arch.name.replace(".tar.gz", ".manifest.json")
    man_local = arch.parent / man_name
    man_local.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    dbx.upload_file(man_local, f"{ROOT_REMOTE}/backups/{man_name}")

    # mirror live state documents (fast single-file recovery)
    pushed = 0
    for doc in STATE_DOCS:
        p = PROJECT / doc
        if p.is_file():
            dbx.upload_file(p, f"{ROOT_REMOTE}/state/{doc}")
            pushed += 1
    print(f"[backup] state docs mirrored: {pushed}")

    # verify remote metadata (rule 35: recovery incomplete until verified)
    meta = dbx.get_metadata(f"{ROOT_REMOTE}/backups/{arch.name}")
    if not meta or meta.get("size") != size:
        print("ERROR: remote verification failed — size mismatch")
        return 4
    print(f"[backup] verified remote copy: {meta.get('size'):,} bytes")

    # prune old backups (keep newest N)
    if args.keep > 0:
        entries = [e for e in dbx.list_folder(f"{ROOT_REMOTE}/backups")
                   if e.get(".tag") == "file"
                   and e.get("name", "").startswith("nova-dm-src-")
                   and e["name"].endswith(".tar.gz")]
        entries.sort(key=lambda e: e.get("server_modified", ""), reverse=True)
        for e in entries[args.keep:]:
            dbx.delete(f"{ROOT_REMOTE}/backups/{e['name']}")
            print(f"[backup] pruned old backup {e['name']}")
        # prune matching manifests (names derived the same way as uploads)
        kept = {e["name"].replace(".tar.gz", ".manifest.json") for e in entries[:args.keep]}
        for e in dbx.list_folder(f"{ROOT_REMOTE}/backups"):
            if (e.get(".tag") == "file" and e.get("name", "").endswith(".manifest.json")
                    and e["name"].startswith("nova-dm-src-")
                    and e["name"] not in kept):
                dbx.delete(f"{ROOT_REMOTE}/backups/{e['name']}")

    arch.unlink(missing_ok=True)
    man_local.unlink(missing_ok=True)
    print(f"[backup] OK → {ROOT_REMOTE}/backups/{arch.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
