#!/usr/bin/env python3
"""T-014: upload v1.2.0 release artifacts to Dropbox with round-trip SHA-256 verification."""
import hashlib
import sys
from pathlib import Path

sys.path.insert(0, '/home/z/my-project/tools/dropbox')
from dropbox import NovaDropbox  # type: ignore


def sha256(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    root = Path('/home/z/my-project')
    uploads = [
        (root / 'download/nova-dm-1.2.0-linux-x64.tar.gz',
         '/Nova-Download-Manager/builds/nova-dm-1.2.0-linux-x64.tar.gz'),
        (root / 'extensions/chromium/nova-chromium-1.2.0.zip',
         '/Nova-Download-Manager/extensions/nova-chromium-1.2.0.zip'),
        (root / 'extensions/firefox/nova-firefox-1.2.0.zip',
         '/Nova-Download-Manager/extensions/nova-firefox-1.2.0.zip'),
        (root / 'download/nova-mini-window-crop.png',
         '/Nova-Download-Manager/evidence/v1.2.0/nova-mini-window.png'),
        (root / 'download/nova-linux-ui-download.png',
         '/Nova-Download-Manager/evidence/v1.2.0/nova-linux-ui-download.png'),
        (root / 'download/evidence-v1.2.0-vlm.json',
         '/Nova-Download-Manager/evidence/v1.2.0/evidence-ui-vlm.json'),
        (root / 'download/evidence-mini-vlm.json',
         '/Nova-Download-Manager/evidence/v1.2.0/evidence-mini-vlm.json'),
    ]

    client = NovaDropbox()
    failures = 0
    for local, remote in uploads:
        if not local.exists():
            print(f'MISSING LOCAL: {local}')
            failures += 1
            continue
        client.upload_file(local, remote)
        # round-trip verification: download to temp and compare hashes
        tmp = local.with_name(local.name + '.rtcheck')
        client.download_file(remote, tmp)
        ok = sha256(local) == sha256(tmp)
        tmp.unlink()
        size = local.stat().st_size
        print(f'{"OK " if ok else "FAIL"} {remote} ({size} B, sha256 {sha256(local)[:12]}…)')
        if not ok:
            failures += 1
    print('RESULT:', 'ALL VERIFIED' if failures == 0 else f'{failures} FAILED')
    return 1 if failures else 0


if __name__ == '__main__':
    raise SystemExit(main())
