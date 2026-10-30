#!/usr/bin/env python3
"""Nova Download Manager — GitHub push/status helper (T-025).

One-command push of main + all tags to the user's GitHub repository and
workflow-run status polling for the windows-build.yml CI.

Two auth modes, selected by tools/github/.github-credentials.json
(gitignored, chmod 600, never uploaded, never printed):

  {"mode": "app", "repo": "owner/name", "app_id": 5545710,
   "key_path": "tools/github/app-private-key.pem"}
      -> JWT (RS256) signed with the GitHub App private key, exchanged
         for a 1-hour installation token scoped to the repo.

  {"mode": "pat", "repo": "owner/name", "token": "ghp_..."}
      -> classic PAT with repo + workflow scopes (or fine-grained
         Contents+Workflows RW).

Actions:
  plan                 show local/remote state + what push would do (no changes)
  push   [--force]     push main + all tags (refuses non-fast-forward unless --force)
  status [--watch]     list the latest workflow runs (or poll one to completion)

The push uses `git -c http.extraheader=AUTHORIZATION: Basic …` so the token
is never written into .git/config, and it runs a tracked-tree secrets scan
first (aborts on ghp_/gho_/github_pat_/PEM/long-base64 hits).

Exit codes: 0 OK, 1 usage, 2 auth/permission problem, 3 remote-state
problem (needs --force or user action), 4 secrets found, 5 git push failed.
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import re
import subprocess
import sys
import time

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CREDS_PATH = os.path.join(REPO_ROOT, "tools", "github", ".github-credentials.json")
GITHUB_API = "https://api.github.com"

# ---------------------------------------------------------------- credentials


def load_creds() -> dict:
    if not os.path.exists(CREDS_PATH):
        sys.exit(
            "[gh] credentials file missing: tools/github/.github-credentials.json\n"
            "[gh] create it as:\n"
            '      {"mode": "app", "repo": "OWNER/NAME", "app_id": 5545710,\n'
            '       "key_path": "tools/github/app-private-key.pem"}\n'
            "    or\n"
            '      {"mode": "pat", "repo": "OWNER/NAME", "token": "ghp_..."}'
        )
    with open(CREDS_PATH, "r", encoding="utf-8") as f:
        creds = json.load(f)
    for key in ("mode", "repo"):
        if key not in creds:
            sys.exit(f"[gh] credentials file: missing '{key}'")
    if creds["mode"] not in ("app", "pat"):
        sys.exit("[gh] credentials file: mode must be 'app' or 'pat'")
    if not re.fullmatch(r"[\w.-]+/[\w.-]+", creds["repo"]):
        sys.exit(f"[gh] credentials file: repo must look like OWNER/NAME, got {creds['repo']!r}")
    return creds


# ------------------------------------------------------------------- app auth


def app_installation_token(creds: dict) -> tuple[str, dict]:
    """Sign a JWT with the app private key, exchange it for an installation
    token valid for the configured repo. Returns (token, response_json)."""
    import jwt  # PyJWT (RS256 needs the cryptography package)

    key_path = os.path.join(REPO_ROOT, creds.get("key_path", "tools/github/app-private-key.pem"))
    if not os.path.exists(key_path):
        sys.exit(
            f"[gh] app private key not found: {key_path}\n"
            "[gh] GitHub App page -> General -> Private keys -> Generate a private key\n"
            "[gh] save the downloaded .pem there and put its path in key_path."
        )
    with open(key_path, "rb") as f:
        private_key = f.read()

    now = int(time.time())
    payload = {"iat": now - 60, "exp": now + 300, "iss": str(creds["app_id"])}
    jwt_token = jwt.encode(payload, private_key, algorithm="RS256")

    import requests

    headers = {"Authorization": f"Bearer {jwt_token}", "Accept": "application/vnd.github+json",
               "X-GitHub-Api-Version": "2022-11-28"}
    r = requests.get(f"{GITHUB_API}/app", headers=headers, timeout=30)
    if r.status_code != 200:
        sys.exit(f"[gh] app JWT rejected ({r.status_code}): {r.text[:200]}\n"
                 "[gh] check app_id and that key_path points to THIS app's private key.")

    r = requests.get(f"{GITHUB_API}/repos/{creds['repo']}/installation",
                     headers=headers, timeout=30)
    if r.status_code == 404:
        sys.exit(
            f"[gh] the GitHub App is not installed on {creds['repo']}.\n"
            "[gh] fix: App page -> Install App -> select the repository.\n"
            "[gh] required Repository permissions: Contents=Read and write,\n"
            "[gh] Workflows=Read and write (Metadata is automatic)."
        )
    r.raise_for_status()
    installation_id = r.json()["id"]

    r = requests.post(f"{GITHUB_API}/app/installations/{installation_id}/access_tokens",
                      headers=headers, timeout=30)
    r.raise_for_status()
    data = r.json()
    token = data["token"]

    perms = data.get("permissions", {})
    missing = [p for p, need in (("contents", "write"), ("workflows", "write"))
               if perms.get(p) != need]
    if missing:
        sys.exit(
            f"[gh] installation token lacks permission(s): {', '.join(missing)}\n"
            "[gh] fix: App settings -> Repository permissions -> set the missing\n"
            "[gh] entries to 'Read and write', then (if needed) reinstall/sync."
        )
    return token, data


def get_token(creds: dict) -> str:
    if creds["mode"] == "pat":
        return creds["token"]
    token, _ = app_installation_token(creds)
    return token


# ------------------------------------------------------------------ git / api


def run_git(args: list[str], check: bool = True) -> subprocess.CompletedProcess:
    proc = subprocess.run(["git", "-C", REPO_ROOT] + args, capture_output=True, text=True)
    if check and proc.returncode != 0:
        sys.exit(f"[gh] git {' '.join(args)} failed:\n{proc.stderr.strip()[:400]}")
    return proc


def api(method: str, path: str, token: str, **kw) -> "requests.Response":
    import requests

    headers = {"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
               "X-GitHub-Api-Version": "2022-11-28"}
    return requests.request(method, f"{GITHUB_API}{path}", headers=headers, timeout=30, **kw)


def git_push_url_token_header(token: str) -> list[str]:
    """git push authorized via http.extraheader — token never lands in .git/config."""
    basic = base64.b64encode(f"x-access-token:{token}".encode()).decode()
    return ["-c", f"http.extraheader=AUTHORIZATION: Basic {basic}"]


# ------------------------------------------------------------ secrets scanner

SECRET_PATTERNS = (
    re.compile(r"ghp_[A-Za-z0-9]{30,}"),
    re.compile(r"gho_[A-Za-z0-9]{30,}"),
    re.compile(r"ghu_[A-Za-z0-9]{30,}"),
    re.compile(r"ghs_[A-Za-z0-9]{30,}"),
    re.compile(r"github_pat_[A-Za-z0-9_]{30,}"),
    re.compile(r"-----BEGIN (RSA |EC )?PRIVATE KEY-----"),
    re.compile(r"xox[bparso]-[A-Za-z0-9-]{10,}"),  # slack tokens
    re.compile(r"xapp-[0-9]+-[A-Za-z0-9-]{10,}"),
)


def secrets_audit() -> None:
    proc = run_git(["ls-files"])
    hits = []
    for rel in proc.stdout.splitlines():
        path = os.path.join(REPO_ROOT, rel)
        if not os.path.isfile(path):
            continue
        try:
            with open(path, "r", encoding="utf-8", errors="ignore") as f:
                text = f.read()
        except OSError:
            continue
        for pat in SECRET_PATTERNS:
            m = pat.search(text)
            if m:
                hits.append(f"  {rel}: {m.group(0)[:14]}…")
                break
    if hits:
        sys.exit("[gh] ABORT — secret-looking strings in tracked files:\n"
                 + "\n".join(hits) +
                 "\n[gh] remove them and purge history (git filter-repo) before pushing.")


# -------------------------------------------------------------------- actions


def local_state() -> dict:
    head = run_git(["rev-parse", "--short", "HEAD"]).stdout.strip()
    branch = run_git(["branch", "--show-current"]).stdout.strip()
    tags = run_git(["tag"]).stdout.split()
    with open(os.path.join(REPO_ROOT, "version.json"), encoding="utf-8") as f:
        version = json.load(f)["version"]
    dirty = run_git(["status", "--porcelain"], check=False).stdout.strip()
    return {"head": head, "branch": branch, "tags": tags, "version": version,
            "dirty": dirty or None}


def cmd_plan(creds: dict, token: str) -> int:
    st = local_state()
    print(f"[local ] branch={st['branch']} head={st['head']} version=v{st['version']}")
    print(f"[local ] tags: {' '.join(st['tags'])}")
    if st["dirty"]:
        print(f"[local ] WARNING — uncommitted changes:\n{st['dirty']}")

    r = api("GET", f"/repos/{creds['repo']}", token)
    if r.status_code == 404:
        print(f"[remote] {creds['repo']} NOT FOUND — create it at "
              f"https://github.com/new (empty, no README) first.")
        return 3
    r.raise_for_status()
    repo = r.json()
    print(f"[remote] {repo['full_name']} private={repo['private']} "
          f"default_branch={repo.get('default_branch') or '(empty)'} size={repo['size']} KB")
    print(f"[plan  ] push {st['branch']} + {len(st['tags'])} tags -> "
          f"triggers version-audit.yml; a v* tag push also triggers windows-build.yml "
          f"+ auto GitHub Release.")
    return 0


def cmd_push(creds: dict, token: str, force: bool) -> int:
    secrets_audit()
    st = local_state()
    if st["dirty"]:
        sys.exit("[gh] working tree is dirty — commit or stash first (release gate).")

    # Remote state: refuse non-fast-forward unless --force.
    proc = run_git(git_push_url_token_header(token)
                   + ["ls-remote", f"https://github.com/{creds['repo']}.git",
                      f"refs/heads/{st['branch']}"], check=False)
    if proc.returncode != 0:
        print(f"[gh] ls-remote failed (repo missing or no access):\n{proc.stderr.strip()[:300]}")
        return 3
    remote_line = proc.stdout.strip().splitlines()
    if remote_line and remote_line[0].strip():
        remote_sha = remote_line[0].split()[0]
        anc = run_git(["merge-base", "--is-ancestor", remote_sha, "HEAD"], check=False)
        if anc.returncode != 0 and not force:
            sys.exit(f"[gh] remote {st['branch']} has commits we lack ({remote_sha[:8]}).\n"
                     "[gh] inspect `git fetch` history or rerun with --force if the remote\n"
                     "[gh] repo is disposable (e.g. only an auto-generated README).")
        print(f"[gh] remote branch exists at {remote_sha[:8]} — fast-forward OK"
              if anc.returncode == 0 else "[gh] --force: overwriting remote history")

    refspecs = [f"refs/heads/{st['branch']}:refs/heads/{st['branch']}"] + \
               [f"refs/tags/{t}:refs/tags/{t}" for t in st["tags"]]
    push_args = (git_push_url_token_header(token)
                 + ["push", f"https://github.com/{creds['repo']}.git"]
                 + (["--force"] if force else []) + refspecs)
    proc = run_git(push_args, check=False)
    if proc.returncode != 0:
        print(f"[gh] PUSH FAILED:\n{proc.stderr.strip()[:600]}")
        return 5
    print(proc.stdout.strip() or proc.stderr.strip())
    print(f"[gh] pushed {st['branch']} + tags to {creds['repo']}")

    run_git(["remote", "remove", "origin"], check=False)
    run_git(["remote", "add", "origin", f"https://github.com/{creds['repo']}.git"])
    print("[gh] clean remote 'origin' set (no credentials stored).")
    print("[gh] next: python3 scripts/github_release_push.py status --watch")
    return 0


def cmd_status(creds: dict, token: str, watch: bool) -> int:
    r = api("GET", f"/repos/{creds['repo']}/actions/runs?per_page=8", token)
    if r.status_code == 404:
        print(f"[gh] no actions access/runs for {creds['repo']} yet.")
        return 0
    r.raise_for_status()
    runs = r.json().get("workflow_runs", [])
    if not runs:
        print("[gh] no workflow runs yet (push, or check the Actions tab).")
        return 0
    for run in runs:
        print(f"#{run['id']:>12}  {run['name']:<22} {run['event']:<7} "
              f"{run['status']:<10} {str(run.get('conclusion')):<12} {run['created_at']}")

    if watch:
        target = runs[0]
        print(f"\n[gh] watching run #{target['id']} ({target['name']})…")
        while True:
            rr = api("GET", f"/repos/{creds['repo']}/actions/runs/{target['id']}", token)
            rr.raise_for_status()
            data = rr.json()
            if data["status"] == "completed":
                print(f"[gh] completed: {data['conclusion'].upper()}")
                print(f"[gh] run page:  {data['html_url']}")
                art = api("GET", f"/repos/{creds['repo']}/actions/runs/{target['id']}/artifacts", token)
                for a in art.json().get("artifacts", []):
                    print(f"[gh] artifact:  {a['name']} ({a['size_in_bytes'] / 1e6:.1f} MB)")
                return 0 if data["conclusion"] == "success" else 5
            print(f"[gh] {time.strftime('%H:%M:%S')} status={data['status']} "
                  f"(step: {data.get('display_title', '')[:60]}) — waiting 20 s…")
            time.sleep(20)
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Nova GitHub push/status helper")
    ap.add_argument("action", choices=["plan", "push", "status"])
    ap.add_argument("--force", action="store_true", help="allow non-fast-forward push")
    ap.add_argument("--watch", action="store_true", help="poll the newest run to completion")
    args = ap.parse_args()

    creds = load_creds()
    token = get_token(creds)
    if args.action == "plan":
        return cmd_plan(creds, token)
    if args.action == "push":
        return cmd_push(creds, token, args.force)
    return cmd_status(creds, token, args.watch)


if __name__ == "__main__":
    sys.exit(main())
