# Browser Integration

Supported: **Google Chrome**, **Brave**, **Mozilla Firefox** (packages in `extensions/chromium` and `extensions/firefox`, built from `extensions/shared`).

## Extension capabilities

| Feature | Where |
|---|---|
| Toolbar button (engine status, click to reconnect) | background |
| Context menu on links ("Download with Nova") | background |
| Context menu on videos ("Download video with Nova") | background |
| Context menu "Download page videos" (all detected media on the tab) | background |
| On-player overlay + quality panel | content script |
| Optional browser-download interception (pause → Nova → cancel) | background, off by default |
| Allowlist / blocklist per site | both |
| Auto-hand-off toggle | extension + app settings |

## Native messaging chain

```
extension ⇄ (browser) ⇄ NovaBrowserHost.exe ⇄ named pipe ⇄ NovaEngine.exe
```

- The browser launches `NovaBrowserHost.exe` **per connection** with the calling extension's origin as argv.
- The host validates the origin against `browserhost.config.json` (`allowed_extension_ids`) and rejects unknown callers silently.
- stdout carries **only** 4-byte-length-prefixed JSON frames (logs go to stderr) — required by the native-messaging spec.
- The host can start the engine at user level (never admin) if it isn't running.

## Registration (installed product)

The Inno Setup installer handles:

- **Chrome/Brave**: writes `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.nova.downloadmanager` (and the Brave equivalent) pointing at a generated manifest in `%LOCALAPPDATA%\Nova`, whose `path` points to the installed `NovaBrowserHost.exe` and whose `allowed_origins` list the installed extension IDs.
- **Firefox**: copies the manifest to `%APPDATA%\mozilla\NativeMessagingHosts\` with `allowed_extensions: ["nova@novadm.dev"]`.

Uninstall removes the registry keys and manifest files. User settings/history are kept by default.

## Manual registration (development)

Templates: `extensions/com.nova.downloadmanager.chromium.template.json` / `.firefox.template.json`.

1. Put a copy in the right place and set `"path"` to your built `NovaBrowserHost(.exe)`.
2. Chrome/Brave: point the registry value at the manifest file. Firefox: place the manifest in the `NativeMessagingHosts` dir of the profile's app-data path.
3. Replace `allowed_origins` / `allowed_extensions` with your development IDs (shown at `chrome://extensions`).

## Loading the extension (development)

- Chrome/Brave: `chrome://extensions` → enable Developer mode → *Load unpacked* → `extensions/chromium/dist`.
- Firefox: `about:debugging#/runtime/this-firefox` → *Load Temporary Add-on* → `extensions/firefox/dist/manifest.json`.

## Permissions rationale

- `nativeMessaging` — talk to the host.
- `webRequest` + host permissions — observe media requests to offer real sources (observational only, no blocking/modification).
- `downloads` — the optional intercept feature (pause/cancel only after successfully handing the URL to Nova).
- `storage` — sync settings.
- `contextMenus` — right-click entries.

## Store publishing status

Not yet submitted (needs signed store listings and final extension IDs; manifests must then be regenerated with the published IDs). Until then, use the unpacked/dev builds above — this is the documented, fully-functional path.
