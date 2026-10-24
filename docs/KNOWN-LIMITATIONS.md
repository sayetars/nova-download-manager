# Known Limitations (docs/KNOWN-LIMITATIONS.md)

Honest, current-state constraints. Anything marked *blocked by environment* was not faked in any way.

## Environment-verified vs. machine-limited

This repository was **built and tested on Linux** (.NET 8.0.426 + Flutter 3.47.7 stable + Node 24). That means:

| Area | Status |
|---|---|
| Engine, scheduler, media, persistence, IPC | **Built + 43/43 automated tests passing on this machine** (real HTTP, real files, real processes) |
| Flutter app | `flutter analyze` clean + 8/8 tests passing; **Windows binary NOT compiled here** (needs a Windows host with VS C++ toolchain) |
| C++ pipe bridge (`nova/pipe`) | Compiles only with the Windows runner — **not compiled here** (no MSVC on Linux) |
| Windows installer (Inno Setup) | Script complete; **not compiled here** (iscc is Windows-only) |
| Extension load in Chrome/Brave/Firefox | Built and packaged; **not manually tested in real browsers on this machine** (no GUI/browsers in the sandbox) |

## Product limitations (by design or pending)

1. **DASH audio+video muxing requires ffmpeg on PATH.** Without it, both tracks are saved as separate, clearly-named files. ffmpeg is deliberately not bundled for licensing clarity (L/GPL).
2. **Live streams are refused** (HLS without `EXT-X-ENDLIST`). Recording is out of scope for v1.
3. **DRM content is refused, always** — including AES-128 HLS, DASH with ContentProtection, and EME-flagged players.
4. **Quality/size values are only shown when provable** — DASH sizes are labeled "estimated"; direct-file sizes are exact. Nothing is invented.
5. **`blob:`/MSE-only players** depend on network observation of the underlying segments; sites with fully encrypted pipelines may yield no candidates (the panel says so honestly).
6. **HTTP Basic/Digest auth** is not stored by the engine; 401/403 surfaces as an auth error for the user to resolve (no credential prompt yet).
7. **BitTorrent (v1.4.0) — honest scope**: implemented and loopback-verified (bencode, HTTP/UDP trackers, DHT query path, peer wire, ut_metadata, rarest-first/sequential pickers, resume bitfield, seeding, multi-file split). Not yet implemented: PEX (BEP-11), LPD, DHT peer-store serving (our DHT node answers get_peers with tokens but does not host peer lists), super-seeding, per-file selection inside multi-file torrents, and UDP tracker scrape. Public-swarm behavior depends on real-world tracker/DHT reachability from the user's network and is not verifiable from this sandbox.
8. **Flutter tray/minimize-to-tray** was not shipped with a package dependency (the chosen tray packages required GTK dev headers at test time on the sandbox's Linux); the Windows runner integration is the planned home for tray support — currently closing the window exits the UI while the engine keeps downloading.
9. **Windows mini-window runner code (v1.2.0)** — `windows/runner/main.cpp` detects `--mini` and applies `HWND_TOPMOST` + `WS_EX_TOOLWINDOW`; as with the v1.1.0 DnD bridge, this is structurally complete but **not compiled here** (no MSVC). The Linux equivalent is compiled, smoke-tested and VLM-verified.
10. **Extension special keys are modifier-only** (alt/ctrl/shift) — modifiers are directly observable on click events; a configurable non-modifier key (e.g. Insert, IDM's default) would require stricter page key-capture permissions and was not taken.
9. **Single-user machine trust model**: any process of the *same* OS account can connect to the engine (same-account compromise is out of scope; different accounts are blocked by ACL + token).
10. **Video-detection coverage** is verified for MP4/HLS/DASH test fixtures; the general claim is limited to "sites whose players expose standard media elements or network-visible segments" — no 100% detection promise.
11. **Extension store publishing** is not done (requires developer accounts + signed packages). Dev-mode loading is fully supported and documented.
12. **Numbers in the installer are unsigned** — no digital signature is applied or claimed.

## Testing gaps to close on a Windows machine

- `flutter build windows --release` + run.
- Load unpacked extensions in the three browsers and walk `docs/TESTING.md`'s manual checklist.
- `iscc` installer build + install/uninstall verification (incl. native-messaging registration).
- Native pipe bridge runtime verification (the C++ code follows the standard MethodChannel pattern but has not run).
