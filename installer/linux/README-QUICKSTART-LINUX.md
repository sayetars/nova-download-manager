# Nova Download Manager 1.0.1 — Linux x64 Quickstart / راهنمای سریع

## English

**Nova** is a professional download manager: a Flutter desktop UI on top of a
standalone C#/.NET 8 engine (multi-part downloads, resume, scheduling, HLS/DASH
media pipeline) with Chromium/Firefox browser integration.

### Requirements
- Linux x86-64 with GTK3 (Ubuntu 20.04+/Debian 11+/Fedora 36+ desktop — already present on normal desktop installs)
- **Either** the .NET 8 runtime (`https://dotnet.microsoft.com/download/dotnet/8.0`)
  **or** nothing extra — the package ships a self-contained engine and the
  launcher switches to it automatically when no runtime is detected.

### Run
```bash
tar -xzf nova-dm-1.0.1-linux-x64.tar.gz
cd nova-dm-1.0.1-linux-x64
./RUN-LINUX.sh
```
The UI automatically starts the engine in the background and connects over a
local Unix-domain socket (no network ports are opened). Data lives in
`~/.local/share/nova` (SQLite task database, IPC token, downloads).

### Browser integration (optional)
1. `./register-browser-host.sh` — registers the native-messaging host for
   Chrome/Chromium/Brave/Vivaldi/Edge + Firefox.
2. Load the extension:
   - Chromium-based: unpack `extensions/nova-chromium-1.0.0.zip` and load it
     as an *unpacked* extension (`chrome://extensions` → Developer mode),
     then paste its ID into the registered manifest in
     `~/.config/<browser>/NativeMessagingHosts/com.nova.downloadmanager.json`.
   - Firefox: `about:debugging` → Load Temporary Add-on →
     `extensions/nova-firefox-1.0.0.zip` (manifest already allows `nova@novadm.dev`).
3. The on-player overlay button with the quality panel appears on pages with
   detected video. DRM and live streams are intentionally refused.

### What's inside
| Path | What it is |
|------|------------|
| `bundle/nova_desktop` | Flutter UI (release) |
| `bundle/NovaEngine` | Engine — framework-dependent (uses your .NET 8) |
| `engine-self-contained/NovaEngine` | Engine — no .NET runtime needed (auto-swapped by launcher) |
| `engine-self-contained/NovaBrowserHost` | Native-messaging bridge (self-contained) |
| `extensions/` | Chromium + Firefox extension packages |
| `docs/` | Architecture, IPC protocol, engine, video detection, security notes |

### Notes & known limitations
- The engine never opens TCP ports; local IPC is a Unix-domain socket only.
- Video detection refuses DRM-protected and live streams by design.
- See `docs/KNOWN-LIMITATIONS.md` for the full list.

---

## فارسی

**نووا** یک مدیریت دانلود حرفه‌ای است: رابط گرافیکی Flutter روی موتور مستقل
C#/.NET 8 (دانلود چندبخشی، ازسرگیری، زمان‌بندی، خط لوله رسانه HLS/DASH) به‌همراه
ادغام با مرورگرهای Chromium و Firefox.

### پیش‌نیازها
- لینوکس ۶۴ بیتی با GTK3 (توزیع‌های دسکتاپ معمول)
- **یا** runtime نسخه ۸ دات‌نت نصب باشد، **یا** هیچ چیز — پکیج شامل موتور
  self-contained است و لانچر در صورت نبود runtime خودکار سراغ آن می‌رود.

### اجرا
```bash
tar -xzf nova-dm-1.0.1-linux-x64.tar.gz
cd nova-dm-1.0.1-linux-x64
./RUN-LINUX.sh
```
رابط گرافیکی خودش موتور را در پس‌زمینه اجرا و از طریق سوکت دامن یونیکس
محلی وصل می‌شود (هیچ پورت شبکه‌ای باز نمی‌شود). داده‌ها در
`~/.local/share/nova` نگهداری می‌شوند.

### ادغام با مرورگر (اختیاری)
۱. `./register-browser-host.sh` را اجرا کنید.
۲. اکستنشن را از `extensions/` بارگذاری کنید (در کروم/بریو به‌صورت unpacked،
   در فایرفاکس به‌صورت موقت از about:debugging).
۳. دکمه شناور روی پلیرهای ویدیو با پنل انتخاب کیفیت ظاهر می‌شود.
   ویدیوهای DRM و پخش زنده عمداً پشتیبانی نمی‌شوند.
