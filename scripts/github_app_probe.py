#!/usr/bin/env python3
"""Probe GitHub App auth + installations for the Nova push lane (T-025).

Validates that the local private key matches the configured App ID, lists
the app's installations with their granted permissions, and — for each
installation — mints a short-lived installation token and lists the
repositories it can access. Prints a readiness checklist for the push
(contents/workflows write perms + at least one repo with push access).

Never prints the private key or any token value.
"""
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


def app_jwt(app_id: int, private_key: bytes) -> str:
    now = int(time.time())
    payload = {"iat": now - 60, "exp": now + 300, "iss": str(app_id)}
    return jwt.encode(payload, private_key, algorithm="RS256")


def main() -> int:
    with open(CREDS_PATH, encoding="utf-8") as f:
        creds = json.load(f)
    app_id = int(creds.get("app_id", 0))
    key_path = os.path.join(REPO_ROOT, creds.get("key_path", "tools/github/app-private-key.pem"))
    if not os.path.exists(key_path):
        print(f"[probe] private key missing: {key_path}")
        return 1
    with open(key_path, "rb") as f:
        private_key = f.read()

    headers = {**HDRS, "Authorization": f"Bearer {app_jwt(app_id, private_key)}"}

    # 1) Key <-> App ID validation
    r = requests.get(f"{API}/app", headers=headers, timeout=30)
    if r.status_code == 401:
        print("[probe] JWT REJECTED (401) — private key does not belong to this App ID "
              f"({app_id}). Regenerate the key in the App settings and replace {key_path}.")
        return 2
    if r.status_code != 200:
        print(f"[probe] GET /app failed {r.status_code}: {r.text[:200]}")
        return 2
    app = r.json()
    print(f"[app   ] id={app['id']} slug={app['slug']} name={app['name']!r} "
          f"owner={app['owner']['login']}")
    print(f"[app   ] url={app['html_url']}")

    # 2) Installations
    r = requests.get(f"{API}/app/installations", headers=headers, timeout=30)
    r.raise_for_status()
    installs = r.json()
    print(f"[inst  ] {len(installs)} installation(s)")
    if not installs:
        print("[probe] NOT INSTALLED ANYWHERE yet — open the App page → Install App → "
              "select the target repository.")
        print("[probe] required Repository permissions: Contents=Read and write, "
              "Workflows=Read and write.")
        return 3

    ready_repos: list[str] = []
    for inst in installs:
        perms = inst.get("permissions", {})
        print(f"[inst  ] id={inst['id']} account={inst['account']['login']} "
              f"selection={inst.get('repository_selection')}")
        for p in sorted(perms):
            print(f"         perm {p:<12} = {perms[p]}")
        missing = [p for p, need in (("contents", "write"), ("workflows", "write"))
                   if perms.get(p) != need]
        if missing:
            print(f"         MISSING permissions: {', '.join(missing)} — fix in App "
                  "settings → Repository permissions (set to 'Read and write'), then "
                  "save (the installation picks this up automatically).")

        # 3) Repositories reachable through this installation
        r2 = requests.post(f"{API}/app/installations/{inst['id']}/access_tokens",
                           headers=headers, timeout=30)
        r2.raise_for_status()
        itoken = r2.json()["token"]
        ih = {**HDRS, "Authorization": f"Bearer {itoken}"}
        r3 = requests.get(f"{API}/installation/repositories?per_page=100", headers=ih, timeout=30)
        r3.raise_for_status()
        repos = r3.json().get("repositories", [])
        print(f"         {len(repos)} repos accessible:")
        for repo in repos:
            rp = repo.get("permissions", {})
            push = rp.get("push", False)
            print(f"           - {repo['full_name']:<50} push={push}")
            if push and not missing:
                ready_repos.append(repo["full_name"])

    print("-" * 60)
    if ready_repos:
        print(f"[ready ] push-able now: {', '.join(ready_repos)}")
        print("[next  ] set \"repo\" in tools/github/.github-credentials.json, then "
              "run: python3 scripts/github_release_push.py plan")
        return 0
    print("[ready ] NO repo ready yet — create the repo (github.com/new), install the "
          "App on it (or add it to the existing installation), ensure Contents+Workflows "
          "Read and write.")
    return 3


if __name__ == "__main__":
    sys.exit(main())
