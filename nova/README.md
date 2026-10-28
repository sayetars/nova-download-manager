# Nova Download Manager

Windows 11 professional download manager — full IDM-class feature set, open
architecture, bilingual (English / فارسی with complete RTL).

## Architecture

```
┌──────────────────┐   Named Pipes (Win) / UDS (Linux dev)
│   Flutter UI     │◄──────────────────────────────┐
│   (Dart)         │                                │
└──────────────────┘                                │
                                                    │
┌──────────────────┐   stdio: Native Messaging      │
│ Browser ext.      │◄──────────┐                   │
│ Chrome MV3/FF     │           │                   │
└──────────────────┘   ┌────────┴─────────┐         │
                       │ nova-host        ├─────────┘
                       │ (C# .NET 8 LTS)  │
                       │  • engine daemon  │
                       │  • native host    │
                       │  • CLI            │
                       └───────┬──────────┘
                               │ SQLite = single source of truth
                        ┌──────┴──────────┐
                        │  nova.db         │
                        └─────────────────┘
```

- **engine/** — C#/.NET 8 (LTS) download engine. Multi-segment HTTP(S)
  with dynamic work stealing, resume with ETag validation, crash recovery
  from SQLite, FTP/FTPS/SFTP, HLS (AES-128) + DASH, yt-dlp bridge,
  debrid unrestricting, DLC/RSDF/CCF containers, Site Grabber,
  queues + scheduler (Gregorian AND Jalali), speed governor, checksums.
- **app/** — Flutter Windows UI: task list, speed graph, video preview
  while downloading (media_kit on .novapart), bilingual EN/FA with RTL.
- **extensions/chrome** — MV3 for Chrome/Brave/Edge: download
  interception, video sniffer (m3u8/mpd/DOM/size), quality panel.
- **extensions/firefox** — same feature set, Firefox manifest.
- **installer/** — Inno Setup script (HKCU native-host registration,
  novadl:// protocol, bilingual installer).
- **.github/workflows/windows-release.yml** — CI on `windows-latest`:
  tests the engine, publishes self-contained exe, builds Flutter UI,
  packages the installer, cuts a GitHub release on tags.

## Build

### Engine (Linux self-test)
```bash
cd engine
dotnet test Nova.sln                      # 23 unit tests
dotnet run --project scripts/IntegrationCheck   # end-to-end over UDS
```

### Engine → Windows binary (cross-compile from Linux)
```bash
cd engine
dotnet publish src/Nova.Host/Nova.Host.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -o publish/win-x64
```

### Full Windows build (CI)
Push a tag `v*` — or run the workflow manually — and
`.github/workflows/windows-release.yml` produces
`NovaDownloadManager-Windows-x64.zip` + installer.

## CLI (IDM gap — quick terminal control)
```
nova-host cli add https://example.com/file.zip -o D:\Downloads
nova-host cli list
nova-host cli pause <taskId> | resume <id> | cancel <id>
nova-host cli speed 2048        # KB/s; 0 = unlimited
nova-host cli grab https://site.com -d 2 -f "*.jpg;*.png"
```

## Native messaging setup (dev)
```
nova-host install-chrome-host --exe <path to nova-host.exe>
reg add "HKCU\Software\Google\Chrome\NativeMessagingHosts\com.nova.downloadmanager" ^
  /ve /t REG_SZ /d "%LOCALAPPDATA%\Nova\com.nova.downloadmanager.chrome.json" /f
```

## Status

See `worklog/SESSIONS.md` for the full build diary and
`worklog/KNOWN-LIMITATIONS.md` for what is not done yet.
