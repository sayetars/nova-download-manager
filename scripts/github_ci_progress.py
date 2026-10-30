#!/usr/bin/env python3
"""Show per-job/per-step progress of the newest windows-build run."""
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


def installation_token() -> str:
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
    inst = r.json()[0]
    r = requests.post(f"{API}/app/installations/{inst['id']}/access_tokens",
                      headers=ah, timeout=30)
    r.raise_for_status()
    return r.json()["token"]


def main() -> int:
    with open(CREDS_PATH, encoding="utf-8") as f:
        creds = json.load(f)
    repo = creds["repo"]
    headers = {**HDRS, "Authorization": f"Bearer {installation_token()}"}

    r = requests.get(f"{API}/repos/{repo}/actions/runs?per_page=1", headers=headers, timeout=30)
    r.raise_for_status()
    run = r.json()["workflow_runs"][0]
    print(f"run #{run['id']} {run['name']} — {run['status']} / {run.get('conclusion')}")

    r = requests.get(f"{API}/repos/{repo}/actions/runs/{run['id']}/jobs", headers=headers, timeout=30)
    r.raise_for_status()
    for job in r.json()["jobs"]:
        print(f"  job {job['name']:<28} {job['status']:<12} {job.get('conclusion')}")
        for step in job.get("steps", []):
            mark = {"completed": {"success": "✅", "failure": "❌", "cancelled": "🚫",
                                  "skipped": "⏭️"}.get(step.get("conclusion"), ""),
                    "in_progress": "🔄", "queued": "⏳"}.get(step["status"], "")
            print(f"    {mark} {step['name']:<44} {step['status']:<12} {step.get('conclusion') or ''}")
    print(f"\n{run['html_url']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
