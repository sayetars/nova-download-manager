# Nova Download Manager

IDM-class download manager for Windows 11 — **Flutter/Dart UI** + **C#/.NET 8 engine** + Chrome/Brave/Firefox extensions (in progress).

## Repository layout

| Path | What it is |
|---|---|
| `engine/Nova.DownloadEngine` | .NET 8 engine: multi-segment HTTP Range downloads, pause/resume, crash recovery, SQLite persistence, versioned JSON IPC (Named Pipes on Windows / Unix sockets for self-test) |
| `app/` | Flutter UI (scaffold — full bilingual EN/FA+RTL UI lands in next rounds) |
| `scripts/` | Local build + self-test scripts |
| `.github/workflows/windows-build.yml` | Cloud Windows build on a free GitHub-hosted runner |
| `installer/` | Packaging assets |
| `worklog/` | Per-round engineering log |

## Build

### Full app for Windows — without a Windows machine (GitHub Actions)

1. Push/import this repo to a (public = free) GitHub repository.
2. **Actions → "Windows Build" → Run workflow**.
3. Download the `Nova-Windows-x64` artifact (portable zip).

### Full app for Windows — locally

Requires Windows 11 + .NET 8 SDK + Flutter stable + VS 2022 "Desktop development with C++":

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-windows.ps1
```

### Engine only (cross-compiles from Linux/macOS too)

```bash
dotnet publish engine/Nova.DownloadEngine -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/win-x64
```

### Self-tests (any OS)

```bash
python3 scripts/test_range_server.py 8798 &                 # fast range-capable test server
dotnet run --project engine/Nova.DownloadEngine -- download http://127.0.0.1:8798/test.bin --segments 4
python3 scripts/test_range_server.py 8799 slow &           # slow server for pause/resume test
dotnet run --project engine/Nova.DownloadEngine -- serve &  # engine service (UDS on Linux)
python3 scripts/ipc_flow.py                                # ping -> add -> pause -> resume -> sha256 check
```

## IPC protocol (v1)

Newline-delimited JSON over Named Pipe `nova-engine-v1` (Windows) or Unix socket `/tmp/nova-engine-v1.sock` (self-test):

```json
{"v":1,"type":"add-download","payload":{"url":"https://example.com/file.zip","segments":8}}
```

Reply: `{"ok":true,"data":{...}}`. Commands: `ping`, `add-download`, `list-downloads`, `pause-download`, `resume-download`, `cancel-download`.

## Status (end of round 12)

- Engine v0.1 — **done, self-tested** (multi-segment + pause/resume + recovery + IPC)
- Flutter UI — scaffold
- Video detection overlay, browser extensions, yt-dlp bridge, scheduler/queues, speed limiter — roadmap (see `worklog/`)
