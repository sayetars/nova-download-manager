# IPC Protocol (nipc/1)

One protocol, three implementations in lockstep:
`src/Nova.Ipc/Protocol.cs` (C#) · `apps/nova_desktop/lib/core/protocol.dart` (Dart) · `extensions/shared/protocol.ts` (TS).

## Transport

- **Flutter ↔ Engine**: Windows named pipe `\\.\pipe\nova-engine-v1` (bridged for Flutter through the C++ runner plugin `nova/pipe`).
- **BrowserHost ↔ Engine**: same named pipe.
- **Browser ↔ BrowserHost**: Native Messaging (stdin/stdout of the host process, spawned by the browser).

## Framing

Every message is one frame:

```
[ 4-byte little-endian payload length ][ UTF-8 JSON payload ]
```

- Frame size cap: **4 MiB**. Larger frames are rejected and the connection is dropped (never crashes the server).
- The cap protects against malicious/garbage peers; a length prefix outside `0..4194304` aborts the connection.

## Envelope

```jsonc
{
  "v": 1,                       // protocol version (this document: 1)
  "id": "ui17",                 // request id; responses echo it; events omit it
  "type": "add_download",      // message type (see catalog)
  "payload": { },              // type-specific object (nullable)
  "token": "…",                // ONLY on the first "hello"
  "ts": 1698000000000           // unix ms
}
```

Responses use `type: "ok"` or `type: "error"` with `payload: { code, category, message, details? }`.
Events use their own type names (`task_updated`, `task_progress`, `task_completed`, `task_failed`, `engine_status`).

JSON conventions: **snake_case** keys, enum values as lowercase strings.

## Handshake (mandatory, 8 s deadline)

```jsonc
→ { "v":1, "type":"hello", "token":"<per-user secret>", "payload":{ "client":"flutter-ui", "protocol_version":1 } }
← { "type":"ok", "payload":{ "server_version":"1.0.0", "protocol_version":1 } }
```

- The token lives in `<data dir>/ipc.token` (user profile; 0640-equivalent). Wrong token → `auth_failed`; clients with `protocol_version > 1` → `version_unsupported`.
- On Windows the pipe ACL additionally restricts access to the current user's SID.
- Heartbeat: `ping` → `ok` with timestamp. Farewell: `bye`.

## Message catalog

### Requests (client → engine)

| Type | Payload (essentials) | Response payload |
|---|---|---|
| `add_download` | `url, destination_dir?, file_name?, queue_name?, category?, referer?, expected_sha256?, max_connections?, priority?, kind? (file\|hls\|dash), media_selection?, auto_start` | `task_key, task_id, duplicate` |
| `add_downloads` | `{ items: [add_download…] }` | `{ tasks: [...] }` |
| `list_tasks` | `filter (all\|active\|queued\|completed\|failed\|paused\|media), search?, limit` | `{ tasks: [TaskSnapshot] }` |
| `get_task` | `task_key` | `TaskSnapshot` |
| `pause_task` / `resume_task` / `cancel_task` / `retry_task` | `task_key` | `ok` |
| `remove_task` | `task_key, delete_file` | `ok` |
| `move_task` | `task_key, queue_name?, priority?, category?` | `ok` |
| `set_destination` | `task_key, destination_dir` | `ok` (running tasks must be paused first) |
| `pause_all` / `resume_all` / `cancel_all` / `clear_completed` | — | `ok` |
| `purge_history` | `older_than_days, only_completed` | `ok` |
| `list_queues` / `save_queue` / `delete_queue` | queue fields | queue list |
| `get_settings` / `set_settings` | `values` map | settings map |
| `get_schedules` / `save_schedule` / `delete_schedule` | rule fields | rules |
| `media_probe` | `url, referer?` | `{ page_url, candidates[], drm_detected }` |
| `engine_status` | — | `{ active_tasks, queued_tasks, global_speed, uptime_seconds, platform, data_dir, protocol_version }` |
| `open_file` / `open_folder` | `task_key` | `{ path }` (UI performs the OS open) |

### Events (engine → all connected clients)

| Type | Payload |
|---|---|
| `task_updated` | full `TaskSnapshot` (below) |
| `task_progress` | `{ task_key, received_bytes, total_bytes, speed, eta_seconds, segments_done, segments_total, state }` — throttled ≈0.5–1 s |
| `task_completed` | `{ task_key, final_path, task }` |
| `task_failed` | `{ task_key, error{code,category,message}, retryable, task }` |
| `engine_status` | same as `engine_status` response |

### TaskSnapshot

`task_key, task_id, state, kind, file_name, url, effective_url, destination_dir, queue_name, category, total_bytes (-1 unknown), received_bytes, segment_count, segments_done, segments_total, error_code?, error_message?, error_category?, validation, priority, retry_count, created_at_ms, started_at_ms?, completed_at_ms?, bt_info_hash? (v1.4.0), seed_after (v1.4.0)`

v1.4.0 additions: `kind: "torrent"` (magnet: URIs and local `.torrent` paths are accepted
by `add_download` directly), optional `seed_after: true` on `add_download` keeps the
BitTorrent session seeding after completion.

## Error model

`{ code, category, message }` — `code` is a stable machine token (`http_403`, `hash_mismatch`, `dns_failure`, `disk_full`, `validator_changed`, …); `category` maps to UI presentation (`auth`, `rate_limited`, `disk_full`, …). Raw exceptions never leave the engine.

## Native messaging subset

`NovaBrowserHost` accepts exactly: `hello`, `ping`, `add_download`, `add_downloads`, `media_probe`, `engine_status`, `task_status`. Everything else → structured error, host stays alive. Responses are wrapped as `{ ok: true, data }` / `{ ok: false, error: { code, message } }` for the extension.

## Versioning

- Additive changes (new optional fields, new message types) keep `v:1` — unknown types must be answered with `unknown_type`, not disconnected.
- A breaking change bumps the version; the server refuses higher client versions during handshake (`version_unsupported`).
