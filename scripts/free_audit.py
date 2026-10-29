#!/usr/bin/env python3
"""Nova DM — free-forever audit gate (policy: docs/FREE-FOREVER.md).

User directive 2026-10-29 (Persian): «برنامه نباید برای هیچ قسمتیش نیاز ب
پرداخت هزینه باشه» — the program must not require payment for any part.

What this enforces (exit 1 on any violation):

1. DEPENDENCY GATE — every direct dependency (NuGet PackageReference, pub
   dependencies/dev_dependencies, npm dependencies/devDependencies) must be
   listed in THIRD-PARTY-NOTICES.md's "Direct dependencies" table.
   Adding a dependency without recording it (with license + $0 cost) fails CI.

2. PAID-SERVICE TERM GATE — known paid services/products must not appear in
   product code (src/, apps/, extensions/, installer/, scripts/, tools/,
   .github/) or in active docs, except inside an explicit negation/rejection
   context ("REJECTED", "banned", "no …", "without …", "free …", …).
   Append-only historical records (CHANGELOG, worklog, VERSIONS, DECISIONS,
   STATE, RESUME-POINT, ledger, docs/history/) are exempt — history is never
   rewritten.

Usage:  python3 scripts/free_audit.py [--verbose]
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NOTICES = ROOT / "THIRD-PARTY-NOTICES.md"
VERBOSE = "--verbose" in sys.argv

# ---- scanned vs exempt -------------------------------------------------------
CODE_DIRS = ["src", "apps", "extensions", "installer", "scripts", "tools", ".github"]
CODE_SUFFIXES = {".cs", ".dart", ".ts", ".js", ".mjs", ".html", ".css", ".json",
                 ".csproj", ".props", ".iss", ".ps1", ".sh", ".py", ".yml", ".yaml",
                 ".md", ".txt", ".in", ".iss", ".sql"}
DOC_FILES = ["README.md", "LICENSE", "README-WINDOWS.md"]
DOC_DIRS = ["docs"]
HISTORICAL_FILES = {"CHANGELOG.md", "worklog.md", "VERSIONS.md", "DECISIONS.md",
                    "STATE.md", "RESUME-POINT.md", "RULES-READ-LEDGER.md",
                    "PROJECT-TREE.txt", "CLOUD-POLICY.md", "RECOVERY.md",
                    "PROJECT-RULES.md", "UNIVERSAL-RULES.md"}
HISTORICAL_DIRS = {"docs/history", "publish", "download", "sdk", "skills",
                   "node_modules", "dist", "build", ".git", "__pycache__", "bin", "obj"}
SELF = Path(__file__).resolve()

NEGATION = re.compile(
    r"no[\s“\"-]|not[\s“\"-]|never|without|free|reject|banned?|exclud|forbidden|"
    r"policy|r[eé]jected|بدون|رایگان|رد شد", re.IGNORECASE)

# Paid services / products that must never be required by any part of Nova.
BANNED_TERMS = [
    "Trusted Signing",           # Azure Trusted Signing (~$9.99/mo) — rejected, D-013
    "TrustedSigningClient",
    "Real-Debrid", "RealDebrid",
    "Premiumize", "premiumize.me",
    "AllDebrid",
    "purchase a license", "buy a license", "license key required",
    "paid subscription",
]
ALLOWED_LICENSES = re.compile(
    r"MIT|Apache-2\.0|BSD|ISC|MPL-2\.0|LGPL|GPL|Unlicense|CC0|public domain|"
    r"Inno Setup License", re.IGNORECASE)

errors: list[str] = []
notes: list[str] = []


def err(msg: str) -> None:
    errors.append(msg)


def note(msg: str) -> None:
    notes.append(msg)
    if VERBOSE:
        print(f"  [note] {msg}")


# ---- 1) dependency allowlist from THIRD-PARTY-NOTICES.md ----------------------
def load_allowed_packages() -> set[str]:
    if not NOTICES.exists():
        err("THIRD-PARTY-NOTICES.md is missing — the free-forever audit has no allowlist.")
        return set()
    table = []
    inside = False
    for line in NOTICES.read_text(encoding="utf-8").splitlines():
        if line.startswith("## ") and "Direct dependencies" in line:
            inside = True
            continue
        if inside and line.startswith("## "):
            break
        if inside and line.startswith("|"):
            table.append(line)
    pkgs = set()
    for row in table:
        cells = [c.strip() for c in row.strip().strip("|").split("|")]
        if len(cells) >= 3 and cells[0] and cells[0] != "Package" and not set(cells[0]) <= set("- "):
            pkgs.add(cells[0].lower())
            if not ALLOWED_LICENSES.search(cells[2]):
                err(f"THIRD-PARTY-NOTICES.md: '{cells[0]}' declares non-free license '{cells[2]}'")
            if cells[3].strip() != "$0":
                err(f"THIRD-PARTY-NOTICES.md: '{cells[0]}' declares cost '{cells[3]}' (must be $0)")
    if not pkgs:
        err("THIRD-PARTY-NOTICES.md: no 'Direct dependencies' table rows found.")
    return pkgs


def audit_nuget(allowed: set[str]) -> None:
    for csproj in ROOT.glob("**/*.csproj"):
        rel = csproj.relative_to(ROOT)
        if any(str(rel).startswith(d) for d in ("sdk/", "publish/")):
            continue
        for m in re.finditer(r'<PackageReference\s+Include="([^"]+)"', csproj.read_text(encoding="utf-8")):
            if m.group(1).lower() not in allowed:
                err(f"{rel}: NuGet dependency '{m.group(1)}' is not recorded in THIRD-PARTY-NOTICES.md — "
                    "verify it is free (OSI license, $0) and add it, or remove it.")


def audit_pubspec(allowed: set[str]) -> None:
    pubspec = ROOT / "apps/nova_desktop/pubspec.yaml"
    if not pubspec.exists():
        return
    lines = pubspec.read_text(encoding="utf-8").splitlines()
    section = None
    for idx, line in enumerate(lines):
        m = re.match(r"^(\w[\w_]*):\s*$", line)
        if m:
            section = m.group(1)
            continue
        if section in ("dependencies", "dev_dependencies"):
            m = re.match(r"^\s{2}([\w_]+):\s*(.*)$", line)
            if m:
                name, spec = m.group(1), m.group(2).strip()
                if "sdk:" in spec:
                    continue  # Flutter SDK — platform, in the platforms table
                if not spec and idx + 1 < len(lines) and re.match(r"^\s{4}sdk:", lines[idx + 1]):
                    continue  # multiline "sdk: flutter" entry — platform, not a package
                if name.lower() not in allowed:
                    err(f"apps/nova_desktop/pubspec.yaml [{section}]: '{name}' is not recorded in "
                        "THIRD-PARTY-NOTICES.md — verify it is free (OSI license, $0) and add it, or remove it.")


def audit_npm(allowed: set[str]) -> None:
    pkg = ROOT / "extensions/package.json"
    if not pkg.exists():
        return
    data = json.loads(pkg.read_text(encoding="utf-8"))
    for section in ("dependencies", "devDependencies"):
        for name in (data.get(section) or {}):
            if name.lower() not in allowed:
                err(f"extensions/package.json [{section}]: '{name}' is not recorded in "
                    "THIRD-PARTY-NOTICES.md — verify it is free (OSI license, $0) and add it, or remove it.")


# ---- 2) banned paid-service terms -------------------------------------------
def rel_is_historical(rel: str) -> bool:
    name = Path(rel).name
    if name in HISTORICAL_FILES and "/" not in rel.replace("\\", "/"):
        return True
    rp = rel.replace("\\", "/")
    return any(rp == d or rp.startswith(d + "/") for d in HISTORICAL_DIRS)


def audit_terms() -> None:
    candidates: list[Path] = []
    for d in CODE_DIRS:
        p = ROOT / d
        if p.exists():
            candidates.extend(x for x in p.rglob("*") if x.is_file())
    for name in DOC_FILES:
        if (ROOT / name).exists():
            candidates.append(ROOT / name)
    for d in DOC_DIRS:
        p = ROOT / d
        if p.exists():
            candidates.extend(x for x in p.rglob("*") if x.is_file())

    for f in sorted(set(candidates)):
        if f.resolve() == SELF.resolve():
            continue
        rel = f.relative_to(ROOT)
        if rel_is_historical(str(rel)):
            continue
        if f.suffix.lower() not in CODE_SUFFIXES:
            continue
        try:
            text = f.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        in_docs = str(rel).replace("\\", "/") in [d + "/" for d in DOC_DIRS] or \
            str(rel) in DOC_FILES or str(rel).startswith("docs/")
        doc_lines = text.splitlines()
        for i, line in enumerate(doc_lines, 1):
            for term in BANNED_TERMS:
                if term.lower() in line.lower():
                    # wrapped text often puts the negation on the previous/next line
                    context = " ".join(doc_lines[max(0, i - 2):min(len(doc_lines), i + 1)])
                    if in_docs and NEGATION.search(context):
                        note(f"{rel}:{i}: policy-context mention of '{term}' (allowed: negation/rejection line)")
                    else:
                        err(f"{rel}:{i}: paid-service reference '{term}' — forbidden by the free-forever "
                            "policy (docs/FREE-FOREVER.md). If this is historical narrative, move it to a "
                            "historical file; if this is a dependency, remove it.")


def main() -> int:
    print("free-forever audit — docs/FREE-FOREVER.md (user directive 2026-10-29)")
    allowed = load_allowed_packages()
    audit_nuget(allowed)
    audit_pubspec(allowed)
    audit_npm(allowed)
    audit_terms()

    print(f"  dependency allowlist : {len(allowed)} recorded packages (all must be $0 + free license)")
    print(f"  dependency audit     : {'PASS' if not any('NOTICES' not in e and 'recorded' in e for e in errors) else 'FAIL'}")
    print(f"  paid-term audit      : {'PASS' if not any('paid-service' in e or 'non-free license' in e for e in errors) else 'FAIL'}")
    if notes:
        print(f"  policy-context notes : {len(notes)} (informational)")
    if errors:
        print(f"\nFAIL — {len(errors)} violation(s):")
        for e in errors:
            print(f"  - {e}")
        return 1
    print("\nPASS — no paid dependencies, no paid services, no paid features.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
