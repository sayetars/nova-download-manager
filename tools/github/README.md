# tools/github — local GitHub credentials (T-025)

This folder holds the **local-only** credentials used by
`scripts/github_release_push.py` to push the repository and poll CI runs.
Nothing here is ever committed (`.gitignore`) or archived to Dropbox
(`backup.py` EXCLUDE_NAMES / EXCLUDE_SUFFIXES).

## `.github-credentials.json` (chmod 600, never committed)

Pick ONE mode:

```json
{
  "mode": "app",
  "repo": "OWNER/NAME",
  "app_id": 5545710,
  "key_path": "tools/github/app-private-key.pem"
}
```

```json
{
  "mode": "pat",
  "repo": "OWNER/NAME",
  "token": "ghp_..."
}
```

- `app` mode: `key_path` points at the GitHub App private key
  (App settings → General → Private keys → *Generate a private key*).
  The App must be **installed on the repo** with Repository permissions
  `Contents: Read and write` and `Workflows: Read and write`.
- `pat` mode: classic PAT with `repo` + `workflow` scopes, or a
  fine-grained PAT with Contents+Workflows read/write on the repo.
- The push passes the token via `git -c http.extraheader=…`, so it is
  never written into `.git/config`.

## Commands

```bash
python3 scripts/github_release_push.py plan           # state + what push would do
python3 scripts/github_release_push.py push [--force] # main + all tags -> CI
python3 scripts/github_release_push.py status --watch # poll the newest run
```

`push` runs a tracked-tree secrets scan first and aborts on token/PEM-like
strings (lesson from the 2026-10-31 Dropbox-credentials-in-history incident,
purged via `git filter-repo` before the first GitHub push).
