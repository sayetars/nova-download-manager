# راهنمای بیلد ویندوزی Nova

## روش ۱ — بیلد ابری بدون نیاز به ویندوز (پیشنهادی)

1. یک ریپوی گیت‌هاب بسازید (حساب رایگان؛ اگر ریپو **Public** باشد اجراکننده ویندوز کاملاً رایگان است) و محتوای پوشه `nova` را push کنید.
2. در گیت‌هاب وارد تب **Actions** شوید → ورک‌فلوی **Windows Build** را انتخاب کنید → دکمه **Run workflow** را بزنید.
3. چند دقیقه بعد، از انتهای همان اجرا، فایل **Nova-Windows-x64** (فایل zip) را از بخش **Artifacts** دانلود کنید.
4. محتویات zip: `nova.exe` (رابط کاربری) + پوشه `engine\NovaEngine.exe` (موتور دانلود).

## روش ۲ — بیلد محلی روی ویندوز ۱۱ خودتان

پیش‌نیازها:

- Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Flutter](https://docs.flutter.dev/get-started/install/windows) (کانال stable)
- Visual Studio 2022 با بار کاری «**Desktop development with C++**»

سپس در PowerShell:

```powershell
git clone <آدرس ریپو> ; cd nova
powershell -ExecutionPolicy Bypass -File scripts/build-windows.ps1
```

خروجی: `dist\Nova-Windows-x64\`

## روش ۳ — فقط موتور دانلود (همین الان آماده است)

فایل `Nova-Engine-v0.1.0-win-x64.zip` را باز کنید و در cmd اجرا کنید:

```cmd
NovaEngine.exe download https://example.com/file.zip --segments 8
NovaEngine.exe download <url> --out D:\Downloads\file.zip
NovaEngine.exe serve
NovaEngine.exe list
```

- دیتابیس: `%USERPROFILE%\.nova\nova.db`
- پیش‌فرض دانلود: `%USERPROFILE%\Downloads`
- موتور کاملاً مستقل است (self-contained) — نیازی به نصب .NET ندارد.

## چرا رابط فلاتر از لینوکس بیلد نمی‌شود؟

بیلد ویندوزی فلاتر به کامپایلر MSVC و Windows SDK نیاز دارد که فقط روی خود ویندوز در دسترس است؛ فلاتر کراس‌کامپایل ویندوز از لینوکس را پشتیبانی نمی‌کند. راه‌حل استانداردِ صنعتی، اجراکننده‌های ابری ویندوز (مثل GitHub Actions) است که ورک‌فلوی آماده آن در `.github/workflows/windows-build.yml` قرار دارد. در مقابل، موتور C#/.NET از روی لینوکس کراس‌کامپایل می‌شود (همین‌طور ساخته شده).
