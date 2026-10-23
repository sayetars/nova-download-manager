# Testing (docs/TESTING.md)

Run everything: `dotnet test Nova.sln -c Debug` — 43 automated tests, all passing at commit time.

## Engine (`tests/Nova.Engine.Tests`)

A programmable `HttpListener` test server covers the scenario matrix of the original spec (§18.1):

**Download core**
- small file; 8 MB multi-part (4 segments, byte-exact); server without ranges → single connection fallback
- pause → resume (throttled server so the window is deterministic); engine restart recovery (re-open the same DB); cancel mid-write (temp deleted)
- unknown content length (chunked → EOF); redirect chain (effective URL honored); query-string URLs
- Persian filename from URL; filename collision → unique `(2)` name

**Failures & validation**
- 404 → `http_404/not_found`; 403 → `http_403/forbidden` (no pointless retries)
- transient 500 → retry with backoff → success
- hash mismatch → `hash_mismatch` + file abandoned; correct hash → `validation: passed`
- validator change (ETag swap) → full restart, no mixed-version files
- duplicate URL detection; three concurrent downloads (byte-exact)

**Media**
- HLS master playlist → best variant → segment download → byte-exact assembly (`SegmentsDone == total`)
- AES-128 HLS → `drm_protected` refusal; live stream → `live_stream` refusal
- DASH MPD parsing (SegmentTemplate, representations, audio/video, ISO duration); ContentProtection → DRM flag
- master/m3u8 parser units (variants, relative URLs, keys, endlist)

**IPC & persistence**
- framing round-trip, snake_case wire format, oversized frame rejection, partial-frame streaming
- pipe server: handshake round-trip, bad token rejected, hostile oversized frame does not kill the server, 4 concurrent clients × 20 requests
- scheduler window rules (day filter, overnight)
- SQLite migrations idempotent + data survives reopen

**Browser path (end-to-end, real processes)**
- `BrowserPathTests`: spawns the real `NovaEngine` process + real `NovaBrowserHost` process; hello handshake; `add_download` through native messaging → real HTTP download → real file on disk → status polling back through the same chain; unknown message types rejected; malformed JSON → error frame without crash.

## Flutter (`apps/nova_desktop/test`)

- `protocol_test.dart` — framing round-trip, snake_case compatibility, 4 MiB cap, partial frames, hello envelope.
- `models_test.dart` — TaskUi parsing, progress math (bytes + segments), TaskTile widget rendering (progress bar, speed, tap → details).

## Manual test checklist (needs a Windows machine with browsers)

1. `flutter run -d windows` → app boots, connects (engine auto-started), task list reflects engine.
2. Pause/resume/cancel from the UI; close app mid-download → engine continues (check downloads dir).
3. Load unpacked extensions in Chrome/Brave/Firefox → context menu on a link → download appears in Nova.
4. Play a non-DRM MP4/HLS page → Nova button appears on the player → panel lists verified qualities → download completes and progress is live in the app.
5. Tray/minimize behavior, RTL layout with Persian selected, dark/light themes.
6. Installer: install → browser registration works → uninstall → registry keys and manifests removed, user data kept.

## Performance smoke (not a benchmark)

- 3 × 1 MB concurrent downloads complete in the suite in <2 s on the CI container (2 vCPU).
- Memory: engine idles <100 MB RSS in tests; the UI renders 200+ rows without jank in widget tests (release profiling still pending — see KNOWN-LIMITATIONS).
