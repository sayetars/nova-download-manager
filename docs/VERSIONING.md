# VERSIONING — Nova Download Manager

> **منبع واحد حقیقت (Single Source of Truth): فایل `version.json` در ریشهٔ مخزن.**
> هیچ نسخه‌ای جایی دستی نوشته نمی‌شود؛ همه‌چیز از این فایل مهر می‌خورد و CI روی انحراف خطا می‌دهد.

---

## 1. Why this document exists

On 2026-10-29 (T-021) the repository contained **five different versions at once** while
claiming to be one product:

| Surface | Said | Reality |
|---|---|---|
| `Directory.Build.props` | 1.6.0 | product version |
| `apps/nova_desktop/pubspec.yaml` | 1.5.0+6 | two releases stale |
| `installer/nova-installer.iss` | **1.0.0** | five releases stale |
| `engine_status` RPC (`Messages.cs` default) | **1.5.0** | hardcoded literal |
| CI workflow zip names | 1.6.0 literals (+ a broken branch list) | drift-by-hand |

Plus a 73 MB abandoned parallel tree (`nova/`, "v0.1.0 era") competing with the real tree,
and git tags that stopped at v1.4.0. That is exactly the "نسخه‌ها با هم قاطی نشن" failure
mode. This document and the tooling below make it structurally impossible to repeat.

## 2. The single source of truth — `version.json`

```jsonc
{
  "product": "Nova Download Manager",
  "version": "1.7.0",        // SemVer — engine, app, extensions released in LOCKSTEP
  "channel": "stable",       // dev | beta | stable
  "build": 7,                // monotonic; becomes pubspec "+7"
  "components": { "engine": "…", "app": "…", "extensionChromium": "…", "extensionFirefox": "…" },
  "protocol":  { "ipc": 1, "extension": 1 },   // wire protocols — independent cadence
  "dbSchema": 11,                                // must equal NovaDatabase's latest migration
  "minimums":  { "engineForExtension": "1.7.0" } // runtime compat floor
}
```

Three different clocks, on purpose:

* **Product version** — moves together for every component (one installer ships them all;
  users never mix parts from different releases).
* **Protocol versions** (`protocol.*`) — additive within a MAJOR; bumped only when the wire
  format changes. Clients negotiate via the `hello` handshake.
* **DB schema** — forward-only integer, one migration each. Never reset.

## 3. Stamped surfaces (the 9 + 3 invariants)

`scripts/sync_versions.py` reads `version.json` and stamps:

| # | Surface | Becomes |
|---|---|---|
| 1 | `Directory.Build.props` | `<Version>` → assembly version of every C# project |
| 2 | `src/Nova.Domain/VersionInfo.cs` | GENERATED constants (`VersionInfo.Version`, `UserAgent`, `PeerWireClient`, `MinEngineForExtension`, `DbSchema`, `IpcProtocol`) |
| 3 | `apps/nova_desktop/pubspec.yaml` | `version: X.Y.Z+build` |
| 4 | `apps/nova_desktop/lib/core/build_version.dart` | GENERATED Dart constants |
| 5 | `extensions/chromium/manifest.json` | `"version"` |
| 6 | `extensions/firefox/manifest.json` | `"version"` |
| 7 | `installer/nova-installer.iss` | `#define MyAppVersion` |
| 8 | `extensions/shared/protocol.ts` | `MIN_ENGINE_VERSION` export |
| 9 | `extensions/shared/ui/options.js` | `MIN_ENGINE_VERSION` constant |

Cross-checks that FAIL the audit (and the build):

* `dbSchema` == highest `RunMigration(n, …)` in `src/Nova.Persistence/NovaDatabase.cs`
* `protocol.ipc` == `Protocol.Version` in `src/Nova.Ipc/Protocol.cs`
* no `nova-*-1.2.3` versioned artifact literals anywhere in `.github/workflows/`
  (wildcards only, so the version can never leak into CI YAML)

Every C# site that needs a version reads `Nova.Domain.VersionInfo` — the literals in
`HttpProber`, `MediaPipeline` (User-Agent), `PeerWireClient` (BT `v` string),
`EngineStatusResponse` (RPC defaults) were all replaced in v1.7.0. Hand-editing a version
anywhere else is a bug; `--check` in CI is the tripwire.

## 4. How to release (the only workflow)

```bash
# 1. bump ONE file:
$EDITOR version.json            # version / build / dbSchema-if-migrated / minimums-if-needed

# 2. stamp everything:
python3 scripts/sync_versions.py

# 3. tests must stay green (the audit is also a CI job):
dotnet test Nova.sln            # includes VersionIntegrityTests (assembly == VersionInfo)
(cd apps/nova_desktop && flutter test)

# 4. build assets, commit, tag — the tag MUST equal version.json (CI verifies):
git add -A && git commit -m "v1.7.0: …"
git tag -a v1.7.0 -m "v1.7.0"
git push && git push --tags
```

CI hard gates:

* `version-audit.yml` — every push/PR runs `sync_versions.py --check`; on `v*` tags it also
  asserts `tag == version.json.version` (a release tag may not lie about its version).
* `windows-build.yml` — its Windows job `needs: audit`; a drifted tree cannot produce
  binaries at all.

Runtime gates:

* **Boot fail-fast** — `EngineHostApp.LoadDb` refuses to start if the live DB schema ≠
  `VersionInfo.DbSchema` ("run sync_versions.py and rebuild" is the error).
* **`engine_status` RPC** now reports the true `engine_version`, `channel`,
  `db_schema_version`, `protocol_version` and `min_engine_for_extension`.
* **Extension options page** shows all versions EN/FA and raises a red banner when the
  running engine is older than the stamped minimum ("update Nova so versions stay in
  lockstep") — green when in sync, gray when the engine is down.
* **Settings → Versions** (Flutter) shows app + engine + protocol + schema + minimum in
  one place.

## 5. SemVer discipline

* `MAJOR` — incompatible change (IPC protocol MAJOR bump, DB that cannot migrate forward,
  config/queue format break). Never shipped casually.
* `MINOR` — new user-visible feature; may extend IPC additively (protocol MINOR + 1).
* `PATCH` — fixes only. v1.0.2-style hotfix lane lives here.
* `0.x` rules do not apply — the product passed 1.0.0 on 2026-10-23.
* Components are **lockstep**: extension `1.7.0` requires engine `1.7.0` (floor =
  `minimums.engineForExtension`). The only sanctioned exception: an extension-store
  review rejection may bump an extension PATCH alone — the store listing shows the product
  version in the description; the engine floor never rises in that case.
* SQLite migrations are **forward-only**; a newer engine opening an older DB migrates it,
  an older engine meeting a newer schema refuses to start (boot gate above).

## 6. Channels, tags, artifacts

* Channels: `dev` (local builds), `stable` (tags). A `beta` channel is reserved but unused
  today. The channel is stamped into `VersionInfo.Channel` / `BuildVersion.channel`.
* Tags: `v<version>` exactly (`v1.7.0`); annotated; must match `version.json` (CI checks).
  Windows binaries + installer + GitHub Releases are only produced from tags.
* Artifact naming: `nova-engine-<v>-win-x64.zip`, `nova-dm-<v>-linux-x64.tar.gz`,
  `nova-chromium-<v>.zip`, `nova-firefox-<v>.zip` (+ `.sha256`). The version appears exactly
  once per filename; CI copies artifacts by **wildcard**, never by literal name.
* Cloud retention (CLOUD-POLICY.md): current + previous release assets only.

## 7. Update checks (from v1.7.0)

`check_update` accepts a JSON manifest at the configured `update.url`:

```json
{ "version": "1.8.0", "channel": "stable", "url": "https://…/nova-setup-1.8.0.exe", "notes": "…" }
```

The engine compares it against `VersionInfo.Version` with `Nova.Domain.SemVersion`
(SemVer 2.0.0 subset, pre-release aware) and returns
`update_available / current_version / latest_version / url / notes`. Non-JSON endpoints
keep working (legacy raw-body passthrough). Recommended hosting: a GitHub Release asset
or raw gist URL pinned in Settings.

## 8. Code signing (researched 2026-10-29, next step for Windows)

Unsigned installers trigger SmartScreen ("unknown publisher"). The cheapest legitimate
2025/2026 path for an individual developer is **Azure Trusted Signing** (≈ **$9.99/month**,
identity validated once with a government ID + Microsoft Authenticator; certificates renew
every 24 h; no USB token). CA resellers charge ~$200–600/yr for OV code-signing certs and
more for EV.

To adopt later: create the Trusted Signing account → identity validation → certificate
profile → in CI, `winget install Microsoft.Azure.TrustedSigningClientTools` + the
`Microsoft.Trusted.Signing.Client` NuGet, then `signtool sign /dtr /du …` with an endpoint
JSON (`Endpoint/CodeSigningAccountName/CertificateProfileName`) for `NovaDesktop.exe`,
`NovaEngine.exe`, `NovaBrowserHost.exe`, `nova.exe` and the installer. The workflow
(`windows-build.yml`) is the natural place; no code changes needed. Until then,
"unknown publisher" is documented in README-WINDOWS.md.

## 9. Version visibility map

| Where | Shows |
|---|---|
| Settings → Versions (Flutter) | app, engine, protocol, DB schema, extension floor |
| Extension options → About (EN/FA) | extension, engine, protocol, floor + OK/outdated/offline banner |
| `nova --version` (CLI) | engine version |
| `engine_status` RPC | everything above, machine-readable |
| HTTP User-Agent (all engine requests) | `NovaDownloadManager/<version>` |
| BitTorrent handshake `v` (BEP-10) | `Nova DM <version>` |
| DB `schema_info` row | schema integer |
| `docs/VERSIONING.md` + git tags + releases | the public record |

---

## خلاصهٔ فارسی — قواعد نسخه‌بندی

* **یک فایل، یک حقیقت:** همهٔ نسخه‌ها فقط از `version.json` در ریشهٔ مخزن می‌آیند.
  تغییر دستی نسخه در هر فایل دیگر خطاست و CI آن را رد می‌کند.
* **هم‌گام اجباری:** موتور، برنامه و هر دو افزونه همیشه هم‌نسخه منتشر می‌شوند؛ نصاب
  همه را با هم نصب می‌کند تا هرگز نسخه‌های ناهمخوان روی سیستم کاربر کنار هم قرار نگیرند.
* **سه ساعت مستقل:** نسخهٔ محصول، نسخهٔ پروتکل IPC و شمارهٔ اسکیمای دیتابیس جدا
  شمرده می‌شوند و هر کدام قانون خودشان را دارند.
* **دروازه‌های خودکار:** `sync_versions.py --check` در CI روی هر push اجرا می‌شود؛
  بیلد ویندوز بدون قبولی این ممیزی شروع نمی‌شود؛ تگ انتشار باید دقیقاً با
  `version.json` یکی باشد؛ موتور هنگام بوت اگر اسکیما ناهمخوان باشد بالا نمی‌آید؛
  افزونه اگر موتور قدیمی‌تر از حداقل لازم ببیند، بنر قرمز «به‌روزرسانی لازم» نشان می‌دهد
  (فارسی/انگلیسی) و صفحهٔ تنظیمات برنامه همهٔ نسخه‌ها را یک‌جا نمایش می‌دهد.
* **مسیر انتشار:** ویرایش `version.json` → اجرای اسکریپت → تست سبز → کامیت → تگ
  `v<نسخه>` → CI بیلد و ریلیس. به‌روزرسانی‌ها با مانیفست JSON و مقایسهٔ SemVer در خود
  موتور انجام می‌شود.
