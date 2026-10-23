# Build & Release (docs/BUILD-AND-RELEASE.md)

## Prerequisites

| Tool | Version | Used for |
|---|---|---|
| .NET SDK | 8.0 (LTS) | engine, host, tests |
| Flutter | stable (3.47.x verified) | desktop app |
| Node.js | 18+ | extension bundling (esbuild) |
| Visual Studio Build Tools 2022 (C++ workload) | — | Flutter Windows build |
| Inno Setup 6 | — | installer (Windows only) |
| ffmpeg (optional) | — | DASH audio+video muxing at runtime |

## Development loop (any OS with the SDKs)

```bash
scripts/build-dev.sh            # or, step by step:
dotnet test Nova.sln            # build + 43 tests
cd extensions && npm run package # build + zip both extensions
cd apps/nova_desktop && flutter analyze && flutter test
```

Engine binary after a build: `src/Nova.EngineHost/bin/Debug/net8.0/NovaEngine[.exe]`.

## Release build (Windows 11 host)

```powershell
git clean -xfd
scripts\build-release.ps1       # 1) dotnet publish (win-x64)
                                 # 2) flutter build windows --release
                                 # 3) extension packages copied
                                 # 4) host manifests staged
iscc installer\nova-installer.iss   # → dist\nova-setup-1.0.0.exe
```

The installer:

- installs to `Program Files\Nova` (one-time elevation for file copy only),
- registers the Chromium/Brave native-messaging host (HKCU) and stages the Firefox manifest,
- optionally registers engine autostart (HKCU `Run`, user level),
- bundles both extension ZIPs and unpacked trees,
- **does not** claim code signing (no certificate is present — sign with your own cert before distribution if required).

## Extension-only rebuild

```bash
cd extensions
npm install
npm run build      # → chromium/dist, firefox/dist
npm run package    # → + nova-chromium-1.0.0.zip, nova-firefox-1.0.0.zip
```

## Versioning

- `Directory.Build.props` holds the assembly version (1.0.0).
- Extension manifests carry the same version; bump all three together (kept in sync in `CHANGELOG.md`).
- Database schema migrations are forward-only (`NovaDatabase.Migrate()`); the engine refuses nothing on upgrade paths and adds columns additively (v4 example).

## CI recipe (suggested)

```yaml
- run: dotnet test Nova.sln -c Debug
- run: cd extensions && npm ci && npm run package
- run: cd apps/nova_desktop && flutter pub get && flutter analyze && flutter test
```

Windows release job additionally runs `build-release.ps1` + `iscc`.
