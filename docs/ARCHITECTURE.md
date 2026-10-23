# Architecture

Nova is a four-part product with **one authority**: the C# engine.

```
┌────────────────────┐        Named Pipe (nipc/1)         ┌─────────────────────────────┐
│  Flutter Desktop   │ ◀───────────────────────────────▶  │  NovaEngine.exe             │
│  (Riverpod, EN/FA) │                                    │  Nova.EngineHost process    │
└────────────────────┘                                    │  ├─ Nova.Engine (runners)   │
                                                          │  ├─ Nova.Scheduler         │
┌────────────────────┐   Native Messaging (stdio)         │  ├─ Nova.Media (HLS/DASH)  │
│  Browser extension │ ──▶ NovaBrowserHost.exe ──pipe──▶   │  ├─ Nova.Persistence(SQLite)│
│  (MV3 Chromium /   │                                    │  └─ Nova.Ipc server        │
│   Firefox)         │                                    └────────────┬────────────────┘
└────────────────────┘                                                 │ HTTP(S)
                                                                       ▼
                                                              Real files on disk
```

## Invariants

1. **Single downloader.** Only `Nova.Engine` performs network transfers and writes files. Flutter and extensions never download.
2. **Single database owner.** Only the engine process opens `nova.db`. Every other component goes through IPC.
3. **UI renders engine state.** Task states come from engine events (`task_updated`, `task_progress`); the UI never guesses them.
4. **Extensions never talk to the network directly** except fetching manifests through the ENGINE's `media_probe` for verification.

## Projects and dependencies

| Project | Depends on | Responsibility |
|---|---|---|
| `Nova.Domain` | — | Entities, `DownloadState` machine, `NovaError`, filename sanitization |
| `Nova.Persistence` | Domain | SQLite (WAL), migrations, repositories |
| `Nova.Ipc` | Domain | nipc/1 envelope + framing, pipe server with user-ACL, client |
| `Nova.Engine` | Domain, Persistence | Probe → plan → segment workers → verify → finalize; retry/backoff; `IMediaPipeline` contract |
| `Nova.Media` | Domain, Engine | HLS/DASH parsing, media download pipeline, candidates service |
| `Nova.Scheduler` | Domain, Persistence, Engine | When queued tasks may start (concurrency, windows, battery, aging) |
| `Nova.EngineHost` | all of the above | Process host: DB, engine, scheduler, IPC server, auth token |
| `Nova.BrowserHost` | Domain, Ipc | Native messaging bridge with extension allow-list |
| `Nova.Windows` | Scheduler | Windows-only APIs (build-gated; battery, autostart, power actions) |

The dependency direction is strictly downward: `Nova.Engine` defines `IMediaPipeline`; `Nova.Media` implements it; `EngineHost` wires them — this keeps media code out of the engine core without circular references.

## Process model

- `NovaEngine.exe` is a **user-level background process** (no admin). It owns the pipe `\\.\pipe\nova-engine-v1` (ACL: current user), the SQLite DB, and the scheduler.
- The Flutter app **starts it on demand** (binary adjacent to the app) and reconnects automatically (every 3 s) after crashes or reboots.
- `NovaBrowserHost.exe` is spawned **by the browser** per extension connection; it is a dumb, hardened bridge: validates origin + message shape, forwards to the engine, relays answers. `stdout` carries ONLY native-messaging frames.
- Single instance: named mutex on Windows, lockfile on POSIX.

## Task lifecycle

```
Created → Analyzing → Queued → Downloading ⇄ (Pausing → Paused)
                 ↘ Failed ← Retrying ←┘            ↓
                            ↓ (user)           Verifying → Completed
                       Cancelled (temp deleted)   ↘ HashMismatch → Failed
```

Transitions are executed by the engine and broadcast as events; crash recovery maps any non-terminal state to `Interrupted` and (per setting) re-queues.

## Data flow for a browser video download

1. Content script detects players and sources (DOM, performance entries).
2. Background aggregates per-tab network observations (webRequest).
3. The panel asks the ENGINE to `media_probe` each candidate — the engine fetches and parses the manifests server-side, returning only verified variants (size/quality/bitrate or explicit notes; DRM/live → refused).
4. User picks a quality → `add_download` with `media_selection` → engine runs the HLS/DASH pipeline (segment fetch, assembly, optional ffmpeg mux) → file finalized → events update every UI.
