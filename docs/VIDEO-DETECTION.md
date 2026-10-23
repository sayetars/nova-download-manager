# Video Detection & On-Player Button

Nova's flagship browser feature: a download button on the video player itself, with a quality panel that shows only *verified* candidates.

## Detection (content script, `extensions/shared/video-detect.ts`)

No single method is trusted alone; layers merge:

1. **DOM scan** — visible `<video>` elements (≥120×80, not hidden) with `src`/`currentSrc` and `<source>` children. `blob:`/MSE sources are not fetched directly — they are resolved through network observation instead.
2. **Performance API** — same-context resource entries filtered for media types (`.mp4/.webm/.ts/.m3u8/.mpd`, video MIME).
3. **Network observation** — the background service worker watches `webRequest` per tab and records media-looking URLs (m3u8/mpd manifests, video MIME types, media extensions with sizes from `content-length`), kept for 30 minutes.
4. **DRM detection** — `encrypted` media events (EME) or attached `MediaKeys` mark a player as protected. The overlay then shows a "protected" state and refuses to download; **Nova never attempts to circumvent DRM or authentication**.

Pages without videos pay nearly nothing: DOM scanning is event-driven (MutationObserver + periodic 2.5 s refresh only when at least one player was seen).

## The overlay (`extensions/shared/overlay.ts`)

- A single host element per page with a **closed shadow DOM** — zero CSS collisions with the page.
- The button pins to the player's corner using logical (`inset-inline-end`) properties — RTL-aware by construction; zoom, scroll, and resize are followed via a requestAnimationFrame loop.
- Fullscreen: the overlay re-parents into the fullscreen element so it remains visible.
- States: normal / disabled (no sources yet) / protected (DRM). No duplicate buttons (WeakMap keyed by video element; entries are dropped when videos disconnect).
- Position avoids native controls (top corner) and is readable on both light and dark video backgrounds (solid branded background + shadow).

## Quality panel

Opening the panel triggers the real pipeline:

1. Content script sends each detected URL to the background.
2. Background asks the ENGINE (`media_probe`) — the engine itself fetches and parses:
   - direct files → exact `Content-Length`, container;
   - HLS master playlists → variant list with resolution/bitrate, codecs; media playlists → segment count, container; live/DRM → explicit refusal notes;
   - DASH MPDs → video representations (+ separate audio tracks), sizes marked **estimated** (bitrate × duration) — only shown as estimates, never as facts.
3. The panel renders rows: quality label, container, bitrate, size (with `~` when estimated), audio/video track markers, and disabled rows with the reason (e.g. "Live stream — not supported").
4. **Download** → `add_download` with `media_selection` (variant URL / representation id) → engine pipeline assembles the file → progress visible in the app like any other task.

### What we never do

- No fabricated qualities, sizes, or links.
- No DRM circumvention, no session-token extraction beyond passing the page URL as referer, no sending cookies/tokens to any external service (they never leave the machine).
- No quality promise for sites that were not tested.

## Settings

`extensions/shared/protocol.ts` defines: master enable, show-on-hover vs always, preferred quality (`ask`/`best`/height), allowlist/blocklist (host patterns), and download interception. Video-detection toggles are mirrored in the Flutter Settings page (`video.detect`, `video.show_button_hover`) — one settings store, kept in sync by the engine.

## Acceptance status

- HLS (VOD, multi-variant) → engine download + byte-exact assembly: **tested** (automated).
- DRM refusal (AES-128 key, DASH ContentProtection): **tested** (automated).
- Live refusal: **tested** (automated).
- End-to-end add-download from a native-messaging client: **tested** (automated, real processes).
- Manual testing in Chrome/Brave/Firefox on Windows: **not yet executed** (see KNOWN-LIMITATIONS) — load `extensions/<browser>/dist` in dev mode and verify.
