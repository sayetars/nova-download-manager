#!/usr/bin/env python3
"""Nova DM — Dropbox client library (stdlib only).

Purpose: upload program builds / source checkpoints and recover them after an
environment reset, per the project's persistence policy (UNIVERSAL-RULES Parts III/VI).

Security rules honoured here (UNIVERSAL-RULES 79/95):
  - Credentials are read from .dropbox-credentials.json (chmod 600) — never logged,
    never embedded into archives.
  - Every request has timeout + bounded retries with exponential backoff + jitter.
  - Remote paths are validated against traversal.

Usage (library):
    from dropbox import NovaDropbox
    dbx = NovaDropbox()
    dbx.upload_file("local.tar.gz", "/builds/local.tar.gz")

Usage (CLI):
    python3 dropbox.py account
    python3 dropbox.py list /builds
    python3 dropbox.py upload <local> <remote>
    python3 dropbox.py download <remote> <local>
    python3 dropbox.py delete <remote>
    python3 dropbox.py meta <remote>
"""
from __future__ import annotations

import json
import os
import random
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

CREDS_PATH = Path(__file__).resolve().parent / ".dropbox-credentials.json"
TOKEN_CACHE = Path(__file__).resolve().parent / ".access-token.json"

API = "https://api.dropboxapi.com/2"
CONTENT_API = "https://content.dropboxapi.com/2"
UPLOAD_SESSION_CHUNK = 32 * 1024 * 1024  # 32 MiB
MAX_RETRIES = 4
REQUEST_TIMEOUT = 120


class DropboxError(RuntimeError):
    def __init__(self, status: int, summary: str, body: str = ""):
        super().__init__(f"Dropbox HTTP {status}: {summary} {body[:200]}")
        self.status = status
        self.summary = summary
        self.body = body


def _load_creds() -> dict:
    if not CREDS_PATH.exists():
        raise SystemExit(
            f"credentials missing: {CREDS_PATH}\n"
            "Provide it (JSON with app_key/app_secret/refresh_token) and retry."
        )
    data = json.loads(CREDS_PATH.read_text(encoding="utf-8"))
    for k in ("app_key", "app_secret", "refresh_token"):
        if not data.get(k):
            raise SystemExit(f"credentials file missing '{k}'")
    return data


def valid_remote_path(path: str) -> str:
    """Normalise + validate a remote path. Raises on traversal."""
    if not path.startswith("/"):
        path = "/" + path
    parts = []
    for seg in path.split("/"):
        if seg in ("", "."):
            continue
        if seg == "..":
            raise ValueError(f"remote path traversal rejected: {path!r}")
        parts.append(seg)
    if not parts:
        return ""
    return "/" + "/".join(parts)


class NovaDropbox:
    def __init__(self, creds: dict | None = None):
        self.creds = creds or _load_creds()
        self._access_token: str | None = None

    # ---------------- auth ----------------
    def access_token(self, force_refresh: bool = False) -> str:
        if self._access_token and not force_refresh:
            return self._access_token
        now = time.time()
        if not force_refresh and TOKEN_CACHE.exists():
            try:
                cached = json.loads(TOKEN_CACHE.read_text(encoding="utf-8"))
                if cached.get("token") and cached.get("expires_at", 0) > now + 120:
                    self._access_token = cached["token"]
                    return self._access_token
            except (json.JSONDecodeError, OSError):
                pass  # fall through to refresh
        data = urllib.parse.urlencode(
            {
                "grant_type": "refresh_token",
                "refresh_token": self.creds["refresh_token"],
            }
        ).encode()
        req = urllib.request.Request(
            "https://api.dropboxapi.com/oauth2/token",
            data=data,
            method="POST",
            headers={
                "Authorization": "Basic "
                + _b64(f"{self.creds['app_key']}:{self.creds['app_secret']}".encode()),
                "Content-Type": "application/x-www-form-urlencoded",
            },
        )
        with urllib.request.urlopen(req, timeout=REQUEST_TIMEOUT) as resp:
            payload = json.loads(resp.read().decode("utf-8"))
        token = payload["access_token"]
        expires = int(payload.get("expires_in", 14400))
        self._access_token = token
        try:
            TOKEN_CACHE.write_text(
                json.dumps({"token": token, "expires_at": now + expires}),
                encoding="utf-8",
            )
            TOKEN_CACHE.chmod(0o600)
        except OSError:
            pass  # cache is best-effort; token stays in memory
        return token

    def _revoke_cache(self):
        self._access_token = None
        try:
            TOKEN_CACHE.unlink()
        except OSError:
            pass

    # ---------------- core request ----------------
    def _request(self, url: str, *, method: str = "POST", headers: dict | None = None,
                 body: bytes | None = None, api_arg: dict | None = None,
                 stream_to: Path | None = None):
        """Single request with bounded retry/backoff; returns bytes (or writes stream_to)."""
        attempt = 0
        while True:
            token = self.access_token()
            hdrs = {"Authorization": f"Bearer {token}"}
            if api_arg is not None:
                hdrs["Dropbox-API-Arg"] = json.dumps(api_arg)
            if headers:
                hdrs.update(headers)
            req = urllib.request.Request(url, data=body, method=method, headers=hdrs)
            try:
                with urllib.request.urlopen(req, timeout=REQUEST_TIMEOUT) as resp:
                    if stream_to is not None:
                        with open(stream_to, "wb") as fh:
                            while True:
                                chunk = resp.read(1024 * 1024)
                                if not chunk:
                                    break
                                fh.write(chunk)
                        return b""
                    return resp.read()
            except urllib.error.HTTPError as e:
                raw = e.read() or b""
                text = raw.decode("utf-8", "replace")
                summary = _err_summary(text)
                if e.code == 401 and attempt == 0:
                    self._revoke_cache()
                    continue  # one silent refresh-retry
                if e.code == 429 and attempt < MAX_RETRIES:
                    wait = _retry_after(e.headers, attempt)
                    time.sleep(wait)
                    attempt += 1
                    continue
                if e.code >= 500 and attempt < MAX_RETRIES:
                    time.sleep(_backoff(attempt))
                    attempt += 1
                    continue
                raise DropboxError(e.code, summary, text) from e
            except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
                if attempt >= MAX_RETRIES:
                    raise DropboxError(0, f"network failure: {e}") from e
                time.sleep(_backoff(attempt))
                attempt += 1

    # ---------------- files ----------------
    def account_info(self) -> dict:
        return json.loads(self._request(f"{API}/users/get_current_account"))

    def list_folder(self, path: str = "", recursive: bool = False) -> list[dict]:
        path = valid_remote_path(path) or ""
        out, cursor, first = [], None, True
        while True:
            if first:
                payload = self._request(
                    f"{API}/files/list_folder",
                    headers={"Content-Type": "application/json"},
                    body=json.dumps({"path": path,
                                     "recursive": recursive,
                                     "limit": 500}).encode(),
                )
                data = json.loads(payload)
                first = False
            else:
                payload = self._request(
                    f"{API}/files/list_folder/continue",
                    headers={"Content-Type": "application/json"},
                    body=json.dumps({"cursor": cursor}).encode(),
                )
                data = json.loads(payload)
            out.extend(data.get("entries", []))
            cursor = data.get("cursor")
            if not data.get("has_more"):
                break
        return out

    def get_metadata(self, remote_path: str) -> dict | None:
        try:
            payload = self._request(
                f"{API}/files/get_metadata",
                headers={"Content-Type": "application/json"},
                body=json.dumps({"path": valid_remote_path(remote_path),
                                 "include_deleted": False}).encode(),
            )
            return json.loads(payload)
        except DropboxError as e:
            if e.status == 409 and "not_found" in e.summary:
                return None
            raise

    def upload_file(self, local: str | Path, remote_path: str,
                    mode: str = "overwrite") -> dict:
        local = Path(local)
        remote_path = valid_remote_path(remote_path)
        size = local.stat().st_size
        if size <= UPLOAD_SESSION_CHUNK:
            with open(local, "rb") as fh:
                body = fh.read()
            payload = self._request(
                f"{CONTENT_API}/files/upload",
                headers={"Content-Type": "application/octet-stream"},
                body=body,
                api_arg={"path": remote_path, "mode": mode, "mute": True},
            )
            return json.loads(payload)
        # session upload for large files — keep the handle open for all chunks
        with open(local, "rb") as fh:
            first = fh.read(UPLOAD_SESSION_CHUNK)
            payload = self._request(
                f"{CONTENT_API}/files/upload_session/start",
                headers={"Content-Type": "application/octet-stream"},
                body=first,
                api_arg={"close": False},
            )
            session_id = json.loads(payload)["session_id"]
            offset = len(first)
            if offset >= size:  # size grew past chunk boundary between checks
                payload = self._request(
                    f"{CONTENT_API}/files/upload_session/finish",
                    headers={"Content-Type": "application/octet-stream"},
                    body=b"",
                    api_arg={
                        "cursor": {"session_id": session_id, "offset": offset},
                        "commit": {"path": remote_path, "mode": mode, "mute": True},
                    },
                )
                return json.loads(payload)
            while True:
                chunk = fh.read(UPLOAD_SESSION_CHUNK)
                if not chunk:
                    raise RuntimeError(
                        f"upload stream ended early at {offset}/{size}")
                if offset + len(chunk) >= size:
                    payload = self._request(
                        f"{CONTENT_API}/files/upload_session/finish",
                        headers={"Content-Type": "application/octet-stream"},
                        body=chunk,
                        api_arg={
                            "cursor": {"session_id": session_id, "offset": offset},
                            "commit": {"path": remote_path, "mode": mode,
                                       "mute": True},
                        },
                    )
                    return json.loads(payload)
                self._request(
                    f"{CONTENT_API}/files/upload_session/append_v2",
                    headers={"Content-Type": "application/octet-stream"},
                    body=chunk,
                    api_arg={"cursor": {"session_id": session_id,
                                        "offset": offset}},
                )
                offset += len(chunk)

    def download_file(self, remote_path: str, local: str | Path) -> Path:
        local = Path(local)
        local.parent.mkdir(parents=True, exist_ok=True)
        tmp = local.with_name(local.name + ".part")
        self._request(
            f"{CONTENT_API}/files/download",
            stream_to=tmp,
            api_arg={"path": valid_remote_path(remote_path)},
        )
        tmp.replace(local)  # atomic move (rule 29)
        return local

    def delete(self, remote_path: str) -> dict:
        return json.loads(
            self._request(
                f"{API}/files/delete_v2",
                headers={"Content-Type": "application/json"},
                body=json.dumps({"path": valid_remote_path(remote_path)}).encode(),
            )
        )

    def ensure_folder(self, remote_path: str) -> None:
        try:
            self._request(
                f"{API}/files/create_folder_v2",
                headers={"Content-Type": "application/json"},
                body=json.dumps({"path": valid_remote_path(remote_path)}).encode(),
            )
        except DropboxError as e:
            if e.status == 409 and "conflict" in (e.summary or ""):
                return  # already exists
            raise


# ---------------- helpers ----------------
def _b64(data: bytes) -> str:
    import base64
    return base64.b64encode(data).decode("ascii")


def _backoff(attempt: int) -> float:
    return min(60.0, 0.5 * (2 ** attempt)) + random.uniform(0, 0.5)  # jitter


def _retry_after(headers, attempt: int) -> float:
    try:
        ra = float(headers.get("Retry-After", "0"))
        if ra > 0:
            return ra + random.uniform(0, 0.5)
    except (TypeError, ValueError):
        pass
    return _backoff(attempt)


def _err_summary(text: str) -> str:
    try:
        return str(json.loads(text).get("error_summary", text[:120]))
    except json.JSONDecodeError:
        return text[:120] or "no body"


def _cli():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    cmd = sys.argv[1]
    dbx = NovaDropbox()
    if cmd == "account":
        info = dbx.account_info()
        print(json.dumps({
            "account_id": info.get("account_id"),
            "email": info.get("email"),
            "display_name": (info.get("name") or {}).get("display_name"),
            "country": info.get("country"),
            "account_type": (info.get("account_type") or {}).get(".tag"),
        }, indent=2))
    elif cmd == "list":
        entries = dbx.list_folder(sys.argv[2] if len(sys.argv) > 2 else "")
        for e in entries:
            tag = e.get(".tag")
            name = e.get("name", "?")
            extra = f"  {e['size']:,}B" if tag == "file" else ""
            print(f"{tag:<6} {name}{extra}")
        print(f"-- {len(entries)} entries --")
    elif cmd == "upload":
        remote = dbx.upload_file(sys.argv[2], sys.argv[3])
        print(f"uploaded -> {remote.get('path_display')} "
              f"({remote.get('size', '?')} bytes, id {remote.get('id')})")
    elif cmd == "download":
        p = dbx.download_file(sys.argv[2], sys.argv[3])
        print(f"downloaded <- {p} ({p.stat().st_size:,} bytes)")
    elif cmd == "delete":
        print(json.dumps(dbx.delete(sys.argv[2])))
    elif cmd == "meta":
        print(json.dumps(dbx.get_metadata(sys.argv[2]), indent=2))
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(_cli())
