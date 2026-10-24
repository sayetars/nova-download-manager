# RECOVERY — Nova Download Manager

After ANY environment reset, follow this document top-to-bottom. Do not start
new development before convergence (UNIVERSAL-RULES 33).

## 0. First 60 seconds
```bash
cd /home/z/my-project
cat STATE.md          # START-HERE block
cat RESUME-POINT.md   # exact continuation point
cat worklog.md | tail -60
ls sdk/ 2>/dev/null   # toolchain survived?
```

## 1. Restore from Dropbox (if tree is missing/stale)
```bash
python3 tools/dropbox/restore.py list          # what exists remotely
python3 tools/dropbox/restore.py latest         # safe restore (version-gated)
python3 tools/dropbox/restore.py latest --force  # ONLY to discard newer local work
```
Restores: all git-tracked files + full .git history + memory docs + tooling.
If `tools/dropbox/.dropbox-credentials.json` is missing (total wipe), the user
must re-supply it once — see D-004 in DECISIONS.md.

## 2. Rebuild toolchain (no root required)
Sandbox facts: Debian 13, no sudo, ~10 GB disk, 2 CPU, 3.9 GB RAM.

### .NET 8 SDK → /home/z/my-project/sdk/dotnet
```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir /home/z/my-project/sdk/dotnet
export DOTNET_ROOT=/home/z/my-project/sdk/dotnet
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
```

### Flutter 3.47.7 (EXACT version — .metadata hash abaf9c5237…) → /home/z/my-project/sdk/flutter
```bash
# resumable download (~1 GB)
curl -C - -L -o /tmp/flutter.tar.xz \
  https://storage.googleapis.com/flutter_infra_release/releases/stable/linux/flutter_linux_3.47.7-stable.tar.xz
mkdir -p /home/z/my-project/sdk && tar -xJf /tmp/flutter.tar.xz -C /home/z/my-project/sdk
export PATH="/home/z/my-project/sdk/flutter/bin:$PATH"
flutter config --no-analytics
flutter doctor
```

### GTK3 dev (user-space, no root) → ~/tools/gtk3-dev
```bash
bash scripts/linux/setup_gtk_dev.sh   # apt-get download + dpkg -x; resumable via .done-* markers
export PKG_CONFIG_PATH="$HOME/tools/gtk3-dev/usr/lib/x86_64-linux-gnu/pkgconfig:$HOME/tools/gtk3-dev/usr/share/pkgconfig"
```

### cmake + ninja (python wheels, user-space)
```bash
pip install cmake ninja    # active venv; binaries land in its bin dir
```
clang: only if `flutter build linux` demands it — apt-get download clang-1X + libllvm-1X + dpkg -x into ~/tools/gtk3-dev/usr.

## 3. Health checks (verify before building)
```bash
dotnet --version                 # expect 8.0.x
flutter --version                # expect 3.47.7 / Dart 3.13.5
flutter doctor                   # GTK + cmake + ninja present
pkg-config --modversion gtk+-3.0 # e.g. 3.24.x
```

## 4. Build + verify (see BUILD-AND-RELEASE.md for full detail)
```bash
dotnet test Nova.sln                                   # 43/43
dotnet publish src/Nova.EngineHost -c Release -r linux-x64 /p:AssemblyName=NovaEngine
cd apps/nova_desktop && flutter pub get && flutter analyze && flutter test && flutter build linux --release
```

## 5. Dropbox hygiene after every meaningful step
```bash
python3 tools/dropbox/backup.py       # source checkpoint + state mirror (keep 10)
python3 tools/dropbox/dropbox.py upload <artifact> /Nova-Download-Manager/builds/<name>
```

## 6. Convergence checklist (rule 35)
- [ ] rules verified (UNIVERSAL-RULES.md present, hash matches ledger)
- [ ] version floor verified (VERSIONS.md local == Dropbox state mirror)
- [ ] worklog reconciled (no duplicate/reset artifacts)
- [ ] source verified (git status sane; tests green)
- [ ] environment healthy (tools above)
- [ ] ONLY THEN: continue from RESUME-POINT.md `next:`
