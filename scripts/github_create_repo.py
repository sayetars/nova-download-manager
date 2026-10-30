#!/usr/bin/env python3
"""Create the Nova repo on the user's account via the App installation token
(repository_creation=write), then verify push access. Idempotent: if the repo
already exists, just verifies. Never prints token values."""
from __future__ import annotations

import json
import os
import sys
import time

import jwt
import requests

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CREDS_PATH = os.path.join(REPO_ROOT, "tools", "github", ".github-credentials.json")
API = "https://api.github.com"
HDRS = {"Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28"}
REPO_NAME = os.environ.get("NOVA_REPO_NAME", "nova-download-manager")
DESCRIPTION = ("Nova Download Manager — free forever (EN/FA, RTL) download manager: "
               "Flutter UI + .NET 8 engine + Chrome/Firefox extensions. "
               "IDM-parity feature set. 100% free, no paid tiers.")


def installation_token() -> tuple[str, dict]:
    with open(CREDS_PATH, encoding="utf-8") as f:
        creds = json.load(f)
    with open(os.path.join(REPO_ROOT, creds["key_path"]), "rb") as f:
        key = f.read()
    now = int(time.time())
    tok = jwt.encode({"iat": now - 60, "exp": now + 300, "iss": str(creds["app_id"])},
                     key, algorithm="RS256")
    ah = {**HDRS, "Authorization": f"Bearer {tok}"}
    r = requests.get(f"{API}/app/installations", headers=ah, timeout=30)
    r.raise_for_status()
    installs = r.json()
    if not installs:
        sys.exit("[create] no installation found")
    inst = installs[0]
    r = requests.post(f"{API}/app/installations/{inst['id']}/access_tokens",
                      headers=ah, timeout=30)
    r.raise_for_status()
    return r.json()["token"], inst


def main() -> int:
    token, inst = installation_token()
    headers = {**HDRS, "Authorization": f"Bearer {token}"}
    account = inst["account"]["login"]

    # Does it already exist?
    full = f"{account}/{REPO_NAME}"
    r = requests.get(f"{API}/repos/{full}", headers=headers, timeout=30)
    if r.status_code == 200:
        print(f"[create] {full} already exists — verifying access only")
    else:
        r2 = requests.post(f"{API}/user/repos", headers=headers, timeout=30, json={
            "name": REPO_NAME,
            "description": DESCRIPTION,
            "private": False,
            "auto_init": False,
        })
        if r2.status_code == 201:
            print(f"[create] CREATED {full} (public, empty)")
        else:
            print(f"[create] creation failed {r2.status_code}: {r2.text[:300]}")
            return 2

    r = requests.get(f"{API}/repos/{full}", headers=headers, timeout=30)
    r.raise_for_status()
    repo = r.json()
    print(f"[create] repo: {repo['full_name']} private={repo['private']} "
          f"default_branch={repo.get('default_branch') or '(empty)'} "
          f"size={repo['size']}")
    perms = repo.get("permissions", {})
    print(f"[create] viewer perms: push={perms.get('push')} admin={perms.get('admin')}")

    # Update the credentials file with the concrete repo name.
    with open(CREDS_PATH, encoding="utf-8") as f:
        creds = json.load(f)
    creds["repo"] = full
    creds.pop("_comment", None)
    with open(CREDS_PATH, "w", encoding="utf-8") as f:
        json.dump(creds, f, indent=2)
        f.write("\n")
    print(f"[create] credentials file updated: repo={full}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
