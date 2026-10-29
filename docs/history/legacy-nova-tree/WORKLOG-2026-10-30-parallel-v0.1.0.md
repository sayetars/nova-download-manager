# Nova Worklog — 2026-10-30 (Asia/Tehran)

## جلسه: پیاده‌سازی کامل بعد از شش دور برنامه‌ریزی

**هدف کاربر:** تحقیق عمیق قابلیت‌های IDM و پیاده‌سازی همهٔ آن‌ها در برنامهٔ خودمان — بدون هیچ پرداختی.

## آنچه واقعاً ساخته شد (فایل‌های واقعی، نه برنامه)

### موتور C#/.NET 8 (~3,700 خط، 18 فایل)
- `Core/Downloader.cs` — دانلودر چندپاره با تقسیم پویا (سبک IDM)، حداکثر 32 اتصال،
  سوئیچ آینه، watchdog توقف، ادامهٔ دانلود، فالبک تک‌جریانی
- `Core/Scheduler.cs` — زمان‌بند صف‌ها: همزمانی، پنجرهٔ زمانی شبانه، اولویت،
  backoff نمایی، ادامهٔ خودکار پس از قطع شبکه، خاموشی پس از اتمام
- `Core/Engine.cs` — API کامل (tasks/queues/settings/video/grabber/system) + رویدادها
- `IPC/` — Named Pipe (ویندوز) + UDS (لینوکس) + TCP loopback با token؛
  handshake نسخه‌دار proto=1؛ framing طول-پیشونددار
- `Storage/` — SQLite + مهاجرت نسخه‌دار (user_version=1) + مخازن
- `Protocols/Ftp.cs` — FTP/FTPS (FluentFTP) + SFTP (SSH.NET) با resume
- `Media/` — پل yt-dlp (بازرسی کیفیت + دانلود + merge)، پل FFmpeg،
  HLS با رمزگشایی AES-128-CBC، Metalink (آینه + هش + PGP)
- `Grabber/SiteGrabber.cs` — خزش BFS با فیلتر wildcard
- `Net/Proxy.cs` — SOCKS4/4a/5 (پروتکل خام)، HTTP CONNECT، PAC با Jint،
  TLS client cert، Basic + Windows auth
- `Services/Services.cs` — پایش کلیپ‌بورد (Win32)، خاموشی، RSS
- `Browser/NativeHost.cs` — پل Native Messaging کروم/فایرفاکس
- `Program.cs` — CLI کامل + تک‌نمونه + حالت پرتابل + nova://

### UI فلاتر (~1,400 خط، 10 فایل)
دوزبانه EN/FA با RTL کامل؛ لیست وظایف با سرعت زنده؛ دیالوگ افزودن ۴زبانه‌ای
(لینک/ویدیو با پنل کیفیت/گربر/گروهی)؛ صف‌ها؛ تاریخچه؛ تنظیمات کامل.

### افزونه‌ها (~700 خط)
Chrome MV3 (service worker) + Firefox (event page):接管 دانلود مرورگر با کوکی/UA،
دکمهٔ شناور ویدیو، پنل کیفیت، منوی راست‌کلیک، تست اتصال.

## آزمون‌های واقعی (لینوکس)
- ۱۲/۱۲ تست واحد: framing، ماشین حالت، چک‌سام‌ها (SHA-256/MD5/CRC32)،
  پارس HLS (کلید/IV)، مهاجرت DB
- **E2E بایت-دقیق:** دانلود ۲۰MB از سرور محلی Range-دار → ۳۲ قطعهٔ پویا →
  merge → SHA-256 == مرجع ✓ (پس از رفع ۴ باگ واقعی)
- باگ‌های پیدا و رفع‌شده در این جلسه: بن‌بست CLI (تشخیص پاسخ)، رقابت Active
  روی قطعات، مرتب‌سازی merge بر اساس نام فایل (فساد فایل!)، فراموشی
  MergeParts در FileOps

## نسخه‌گذاری و توزیع
- `VERSION` منفرد (0.1.0) + اسکریپت تزریق به ۴ مؤلفه
- CI ویندوز (engine self-contained + Flutter build + افزونه‌ها → GitHub Releases)
- LICENSE (MIT) + LICENSE-THIRD-PARTY (همه وابستگی‌ها رایگان/قابل‌توزیع)

## تصمیم‌های امنیتی
توکن IPC تصادفی در پروفایل کاربر؛ TCP فقط loopback؛ handshake الزامی.

## گام‌های بعدی پیشنهادی
نصاب Inno Setup، آیکون سینی + کلید میانبر، پشتیبانی live-HLS، flutter_localizations کامل.
