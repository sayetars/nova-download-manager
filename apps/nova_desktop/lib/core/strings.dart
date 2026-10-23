import 'package:flutter/material.dart';

/// All user-facing strings in English and Persian. One source of truth — no codegen.
abstract class S {
  static S of(BuildContext context) {
    final locale = Localizations.localeOf(context);
    return S.forLocale(locale);
  }

  static S forLocale(Locale l) => (l.languageCode == 'fa') ? const FaStrings() : const EnStrings();

  // ----- common
  String get appName;
  String get search;
  String get addDownload;
  String get addUrl;
  String get download;
  String get pause;
  String get resume;
  String get retry;
  String get cancel;
  String get remove;
  String get openFile;
  String get openFolder;
  String get copyLink;
  String get settings;
  String get save;
  String get close;
  String get delete;
  String get confirm;
  String get confirmDeleteFile;
  String get fileName;
  String get destination;
  String get connections;
  String get priority;
  String get queue;
  String get category;
  // ----- nav
  String get overview;
  String get allDownloads;
  String get activeDownloads;
  String get queuedDownloads;
  String get completedDownloads;
  String get failedDownloads;
  String get mediaDownloads;
  String get queues;
  String get scheduler;
  String get browserIntegration;
  String get history;
  String get diagnostics;
  // ----- details
  String get totalSize;
  String get received;
  String get speed;
  String get avgSpeed;
  String get eta;
  String get unknownEta;
  String get segments;
  String get status;
  String get error;
  String get none;
  String get downloaded;
  String get deleteAlsoFile;
  // ----- pages
  String get engineConnected;
  String get engineDisconnected;
  String get engineDisconnectedHint;
  String get reconnecting;
  String get totalSpeed;
  String get activeTasks;
  String get schedulerState;
  String get noDownloads;
  String get noDownloadsHint;
  String get clearCompleted;
  String get purgeHistory;
  String get purgeHistoryHint;
  String get maxConcurrent;
  String get globalSpeedCap;
  String get perTaskCap;
  String get unlimited;
  String get general;
  String get appearance;
  String get language;
  String get theme;
  String get themeLight;
  String get themeDark;
  String get themeSystem;
  String get defaultDir;
  String get onStartup;
  String get autoStartEngine;
  String get resumeOnStart;
  String get minimizeToTray;
  String get behavior;
  String get videoDetection;
  String get showButtonHover;
  String get preferredQuality;
  String get siteAllowlist;
  String get siteBlocklist;
  String get notifications;
  String get notifyOnComplete;
  String get historyRetention;
  String get days;
  String get saveHistory;
  String get privacy;
  String get noSensitiveLogging;
  String get addQueue;
  String get queueName;
  String get maxConcurrentPerQueue;
  String get speedCapPerQueue;
  String get newRule;
  String get daysOfWeek;
  String get startTime;
  String get endTime;
  String get enabled;
  String get ruleName;
  String get batteryPause;
  String get extensions;
  String get chromiumExt;
  String get firefoxExt;
  String get extStatusConnected;
  String get extStatusNotInstalled;
  String get extHowTo;
  String get logs;
  String get engineLog;
  String get protocolVersion;
  String get uptime;
  String get platform;
  String get dataDir;
  // ----- states
  String stateLabel(String state);
  // ----- errors
  String errorCategory(String category);
  // ----- add dialog
  String get urlLabel;
  String get urlHint;
  String get startNow;
  String get added;
  String get duplicateDetected;
  String get invalidUrl;
  // ----- misc
  String get paused;
  String get completed;
  String get failed;
  String get cancelled;
  String get interrupted;
  String get verifying;
  String get retrying;
  String get analyzing;
  String get remaining;
  String get sizeUnknown;
  String get videoQualityPanel;
  String get bestAvailable;
  String get chooseFormat;
  String get downloadAudio;
  String get copyDirectLink;
  String get rescan;
  String get min;
  String get hoursShort;
  String get dayShort;
}

class EnStrings implements S {
  const EnStrings();

  @override String get appName => 'Nova Download Manager';
  @override String get search => 'Search downloads';
  @override String get addDownload => 'New download';
  @override String get addUrl => 'Paste the download link';
  @override String get download => 'Download';
  @override String get pause => 'Pause';
  @override String get resume => 'Resume';
  @override String get retry => 'Retry';
  @override String get cancel => 'Cancel';
  @override String get remove => 'Remove';
  @override String get openFile => 'Open file';
  @override String get openFolder => 'Open folder';
  @override String get copyLink => 'Copy link';
  @override String get settings => 'Settings';
  @override String get save => 'Save';
  @override String get close => 'Close';
  @override String get delete => 'Delete';
  @override String get confirm => 'Confirm';
  @override String get confirmDeleteFile => 'Also delete the downloaded file from disk?';
  @override String get fileName => 'File name';
  @override String get destination => 'Destination';
  @override String get connections => 'Connections';
  @override String get priority => 'Priority';
  @override String get queue => 'Queue';
  @override String get category => 'Category';
  @override String get overview => 'Overview';
  @override String get allDownloads => 'All downloads';
  @override String get activeDownloads => 'Active';
  @override String get queuedDownloads => 'Queued';
  @override String get completedDownloads => 'Completed';
  @override String get failedDownloads => 'Failed';
  @override String get mediaDownloads => 'Media';
  @override String get queues => 'Queues';
  @override String get scheduler => 'Scheduler';
  @override String get browserIntegration => 'Browser integration';
  @override String get history => 'History';
  @override String get diagnostics => 'Diagnostics';
  @override String get totalSize => 'Total size';
  @override String get received => 'Received';
  @override String get speed => 'Speed';
  @override String get avgSpeed => 'Average speed';
  @override String get eta => 'Remaining time';
  @override String get unknownEta => 'calculating…';
  @override String get segments => 'Segments';
  @override String get status => 'Status';
  @override String get error => 'Error';
  @override String get none => '—';
  @override String get downloaded => 'Downloaded';
  @override String get deleteAlsoFile => 'Delete file from disk';
  @override String get engineConnected => 'Engine connected';
  @override String get engineDisconnected => 'Engine offline';
  @override String get engineDisconnectedHint => 'Nova engine is not running. Downloads will resume when it is back.';
  @override String get reconnecting => 'Reconnecting…';
  @override String get totalSpeed => 'Total speed';
  @override String get activeTasks => 'Active';
  @override String get schedulerState => 'Scheduler';
  @override String get noDownloads => 'Nothing here yet';
  @override String get noDownloadsHint => 'Add a link with Ctrl+N or drop files here.';
  @override String get clearCompleted => 'Clear completed';
  @override String get purgeHistory => 'Purge history';
  @override String get purgeHistoryHint => 'Remove entries older than (days)';
  @override String get maxConcurrent => 'Simultaneous downloads';
  @override String get globalSpeedCap => 'Global speed cap';
  @override String get perTaskCap => 'Per-task speed cap';
  @override String get unlimited => 'Unlimited';
  @override String get general => 'General';
  @override String get appearance => 'Appearance';
  @override String get language => 'Language';
  @override String get theme => 'Theme';
  @override String get themeLight => 'Light';
  @override String get themeDark => 'Dark';
  @override String get themeSystem => 'System';
  @override String get defaultDir => 'Default download folder';
  @override String get onStartup => 'Startup';
  @override String get autoStartEngine => 'Start engine with Windows (user level)';
  @override String get resumeOnStart => 'Resume interrupted downloads on start';
  @override String get minimizeToTray => 'Minimize to tray instead of taskbar';
  @override String get behavior => 'Behavior';
  @override String get videoDetection => 'Video detection';
  @override String get showButtonHover => 'Show download button on hover';
  @override String get preferredQuality => 'Preferred quality';
  @override String get siteAllowlist => 'Site allowlist';
  @override String get siteBlocklist => 'Site blocklist';
  @override String get notifications => 'Notifications';
  @override String get notifyOnComplete => 'Notify when downloads complete';
  @override String get historyRetention => 'Keep history for';
  @override String get days => 'days';
  @override String get saveHistory => 'Save history';
  @override String get privacy => 'Privacy';
  @override String get noSensitiveLogging => 'Cookies and tokens are never written to logs.';
  @override String get addQueue => 'New queue';
  @override String get queueName => 'Queue name';
  @override String get maxConcurrentPerQueue => 'Max simultaneous per queue';
  @override String get speedCapPerQueue => 'Speed cap per queue (bytes/s, 0 = off)';
  @override String get newRule => 'New rule';
  @override String get daysOfWeek => 'Days of week';
  @override String get startTime => 'Start time';
  @override String get endTime => 'End time';
  @override String get enabled => 'Enabled';
  @override String get ruleName => 'Rule name';
  @override String get batteryPause => 'Pause new downloads on battery';
  @override String get extensions => 'Extensions';
  @override String get chromiumExt => 'Chrome / Brave extension';
  @override String get firefoxExt => 'Firefox extension';
  @override String get extStatusConnected => 'Registered — engine reachable';
  @override String get extStatusNotInstalled => 'Not installed';
  @override String get extHowTo => 'Load the unpacked extension from extensions/chromium (dev mode), or install via the Nova installer.';
  @override String get logs => 'Logs';
  @override String get engineLog => 'Engine log';
  @override String get protocolVersion => 'Protocol version';
  @override String get uptime => 'Uptime';
  @override String get platform => 'Platform';
  @override String get dataDir => 'Data directory';
  @override String stateLabel(String state) => switch (state) {
        'created' => 'Created', 'analyzing' => 'Analyzing', 'queued' => 'Queued',
        'downloading' => 'Downloading', 'pausing' => 'Pausing', 'paused' => 'Paused',
        'retrying' => 'Retrying', 'interrupted' => 'Interrupted', 'verifying' => 'Verifying',
        'completed' => 'Completed', 'failed' => 'Failed', 'cancelled' => 'Cancelled',
        _ => state,
      };
  @override String errorCategory(String category) => switch (category) {
        'network' => 'Network error', 'dns' => 'Address not found', 'tls' => 'Secure connection failed',
        'auth' => 'Authentication required', 'not_found' => 'Not found', 'forbidden' => 'Access denied',
        'rate_limited' => 'Too many requests', 'server' => 'Server error',
        'disk_full' => 'Not enough disk space', 'file_io' => 'File error',
        'access_denied' => 'Permission denied', 'validator_changed' => 'File changed on server',
        'hash_mismatch' => 'Checksum mismatch', 'url_expired' => 'Link expired',
        'media' => 'Media error', 'cancelled' => 'Cancelled', _ => 'Error',
      };
  @override String get urlLabel => 'Link';
  @override String get urlHint => 'https://example.com/file.zip';
  @override String get startNow => 'Start now';
  @override String get added => 'Download added';
  @override String get duplicateDetected => 'This link is already in the list.';
  @override String get invalidUrl => 'Enter a valid http(s) link.';
  @override String get paused => 'Paused';
  @override String get completed => 'Completed';
  @override String get failed => 'Failed';
  @override String get cancelled => 'Cancelled';
  @override String get interrupted => 'Interrupted';
  @override String get verifying => 'Verifying';
  @override String get retrying => 'Retrying';
  @override String get analyzing => 'Analyzing';
  @override String get remaining => 'remaining';
  @override String get sizeUnknown => 'Unknown size';
  @override String get videoQualityPanel => 'Video quality';
  @override String get bestAvailable => 'Best available quality';
  @override String get chooseFormat => 'Choose format';
  @override String get downloadAudio => 'Download audio only';
  @override String get copyDirectLink => 'Copy direct link';
  @override String get rescan => 'Rescan sources';
  @override String get min => 'min';
  @override String get hoursShort => 'h';
  @override String get dayShort => 'd';
}

class FaStrings implements S {
  const FaStrings();

  @override String get appName => 'مدیریت دانلود نوا';
  @override String get search => 'جست‌وجوی دانلودها';
  @override String get addDownload => 'دانلود جدید';
  @override String get addUrl => 'لینک دانلود را وارد کنید';
  @override String get download => 'دانلود';
  @override String get pause => 'توقف';
  @override String get resume => 'ادامه';
  @override String get retry => 'تلاش مجدد';
  @override String get cancel => 'لغو';
  @override String get remove => 'حذف';
  @override String get openFile => 'بازکردن فایل';
  @override String get openFolder => 'بازکردن پوشه';
  @override String get copyLink => 'کپی لینک';
  @override String get settings => 'تنظیمات';
  @override String get save => 'ذخیره';
  @override String get close => 'بستن';
  @override String get delete => 'حذف';
  @override String get confirm => 'تأیید';
  @override String get confirmDeleteFile => 'فایل دانلودشده هم از دیسک حذف شود؟';
  @override String get fileName => 'نام فایل';
  @override String get destination => 'مقصد';
  @override String get connections => 'اتصال‌ها';
  @override String get priority => 'اولویت';
  @override String get queue => 'صف';
  @override String get category => 'دسته';
  @override String get overview => 'نمای کلی';
  @override String get allDownloads => 'همه دانلودها';
  @override String get activeDownloads => 'در حال دانلود';
  @override String get queuedDownloads => 'در صف';
  @override String get completedDownloads => 'تکمیل‌شده';
  @override String get failedDownloads => 'ناموفق';
  @override String get mediaDownloads => 'رسانه';
  @override String get queues => 'صف‌ها';
  @override String get scheduler => 'زمان‌بند';
  @override String get browserIntegration => 'یکپارچگی مرورگر';
  @override String get history => 'تاریخچه';
  @override String get diagnostics => 'عیب‌یابی';
  @override String get totalSize => 'حجم کل';
  @override String get received => 'دریافت‌شده';
  @override String get speed => 'سرعت';
  @override String get avgSpeed => 'میانگین سرعت';
  @override String get eta => 'زمان باقی‌مانده';
  @override String get unknownEta => 'در حال محاسبه…';
  @override String get segments => 'بخش‌ها';
  @override String get status => 'وضعیت';
  @override String get error => 'خطا';
  @override String get none => '—';
  @override String get downloaded => 'دانلود شده';
  @override String get deleteAlsoFile => 'فایل也从 دیسک حذف شود';
  @override String get engineConnected => 'موتور متصل است';
  @override String get engineDisconnected => 'موتور قطع است';
  @override String get engineDisconnectedHint => 'موتور نوا در حال اجرا نیست. با راه‌اندازی مجدد، دانلودها ادامه می‌یابند.';
  @override String get reconnecting => 'در حال اتصال مجدد…';
  @override String get totalSpeed => 'سرعت کل';
  @override String get activeTasks => 'فعال';
  @override String get schedulerState => 'زمان‌بند';
  @override String get noDownloads => 'هنوز چیزی اینجا نیست';
  @override String get noDownloadsHint => 'با Ctrl+N لینک اضافه کنید یا فایل‌ها را بکشید و رها کنید.';
  @override String get clearCompleted => 'پاک‌سازی تکمیل‌شده‌ها';
  @override String get purgeHistory => 'پاک‌سازی تاریخچه';
  @override String get purgeHistoryHint => 'حذف موارد قدیمی‌تر از (روز)';
  @override String get maxConcurrent => 'دانلودهای هم‌زمان';
  @override String get globalSpeedCap => 'سقف سرعت کلی';
  @override String get perTaskCap => 'سقف سرعت هر دانلود';
  @override String get unlimited => 'نامحدود';
  @override String get general => 'عمومی';
  @override String get appearance => 'ظاهر';
  @override String get language => 'زبان';
  @override String get theme => 'پوسته';
  @override String get themeLight => 'روشن';
  @override String get themeDark => 'تیره';
  @override String get themeSystem => 'سیستم';
  @override String get defaultDir => 'پوشه پیش‌فرض دانلود';
  @override String get onStartup => 'شروع به کار';
  @override String get autoStartEngine => 'اجرای موتور با ورود به ویندوز (سطح کاربر)';
  @override String get resumeOnStart => 'ادامه دانلودهای ناتمام هنگام شروع';
  @override String get minimizeToTray => 'کمینه‌سازی به سینی به‌جای نوار وظیفه';
  @override String get behavior => 'رفتار';
  @override String get videoDetection => 'تشخیص ویدئو';
  @override String get showButtonHover => 'نمایش دکمه دانلود هنگام بردن نشانگر';
  @override String get preferredQuality => 'کیفیت ترجیحی';
  @override String get siteAllowlist => 'سایت‌های مجاز';
  @override String get siteBlocklist => 'سایت‌های مسدود';
  @override String get notifications => 'اعلان‌ها';
  @override String get notifyOnComplete => 'اعلان پایان دانلود';
  @override String get historyRetention => 'نگهداری تاریخچه';
  @override String get days => 'روز';
  @override String get saveHistory => 'ذخیره تاریخچه';
  @override String get privacy => 'حریم خصوصی';
  @override String get noSensitiveLogging => 'کوکی و توکن هرگز در لاگ ثبت نمی‌شوند.';
  @override String get addQueue => 'صف جدید';
  @override String get queueName => 'نام صف';
  @override String get maxConcurrentPerQueue => 'حداکثر هم‌زمان در صف';
  @override String get speedCapPerQueue => 'سقف سرعت صف (بایت/ثانیه، ۰ = خاموش)';
  @override String get newRule => 'قاعده جدید';
  @override String get daysOfWeek => 'روزهای هفته';
  @override String get startTime => 'ساعت شروع';
  @override String get endTime => 'ساعت پایان';
  @override String get enabled => 'فعال';
  @override String get ruleName => 'نام قاعده';
  @override String get batteryPause => 'توقف دانلود جدید در حالت باتری';
  @override String get extensions => 'افزونه‌ها';
  @override String get chromiumExt => 'افزونه کروم / بریو';
  @override String get firefoxExt => 'افزونه فایرفاکس';
  @override String get extStatusConnected => 'ثبت‌شده — موتور در دسترس است';
  @override String get extStatusNotInstalled => 'نصب نشده';
  @override String get extHowTo => 'افزونه را در حالت توسعه‌دهنده از extensions/chromium بارگذاری کنید یا از طریق نصب‌کننده نوا نصب کنید.';
  @override String get logs => 'لاگ‌ها';
  @override String get engineLog => 'لاگ موتور';
  @override String get protocolVersion => 'نسخه پروتکل';
  @override String get uptime => 'مدت فعالیت';
  @override String get platform => 'سامانه';
  @override String get dataDir => 'پوشه داده‌ها';
  @override String stateLabel(String state) => switch (state) {
        'created' => 'ایجادشده', 'analyzing' => 'در حال تحلیل', 'queued' => 'در صف',
        'downloading' => 'در حال دانلود', 'pausing' => 'در حال توقف', 'paused' => 'متوقف',
        'retrying' => 'تلاش مجدد', 'interrupted' => 'قطع‌شده', 'verifying' => 'در حال راستی‌آزمایی',
        'completed' => 'تکمیل‌شده', 'failed' => 'ناموفق', 'cancelled' => 'لغوشده',
        _ => state,
      };
  @override String errorCategory(String category) => switch (category) {
        'network' => 'خطای شبکه', 'dns' => 'نشانی یافت نشد', 'tls' => 'اتصال امن برقرار نشد',
        'auth' => 'نیاز به احراز هویت', 'not_found' => 'یافت نشد', 'forbidden' => 'دسترسی مجاز نیست',
        'rate_limited' => 'درخواست‌های بیش از حد', 'server' => 'خطای سرور',
        'disk_full' => 'فضای دیسک کافی نیست', 'file_io' => 'خطای فایل',
        'access_denied' => 'مجوز دسترسی ندارید', 'validator_changed' => 'فایل سرور تغییر کرده است',
        'hash_mismatch' => 'عدم تطابق چک‌سام', 'url_expired' => 'لینک منقضی شده',
        'media' => 'خطای رسانه', 'cancelled' => 'لغوشده', _ => 'خطا',
      };
  @override String get urlLabel => 'لینک';
  @override String get urlHint => 'https://example.com/file.zip';
  @override String get startNow => 'همین حالا شروع کن';
  @override String get added => 'دانلود اضافه شد';
  @override String get duplicateDetected => 'این لینک از قبل در فهرست هست.';
  @override String get invalidUrl => 'یک لینک http(s) معتبر وارد کنید.';
  @override String get paused => 'متوقف';
  @override String get completed => 'تکمیل‌شده';
  @override String get failed => 'ناموفق';
  @override String get cancelled => 'لغوشده';
  @override String get interrupted => 'قطع‌شده';
  @override String get verifying => 'راستی‌آزمایی';
  @override String get retrying => 'تلاش مجدد';
  @override String get analyzing => 'تحلیل';
  @override String get remaining => 'باقی‌مانده';
  @override String get sizeUnknown => 'حجم نامشخص';
  @override String get videoQualityPanel => 'کیفیت ویدئو';
  @override String get bestAvailable => 'بهترین کیفیت موجود';
  @override String get chooseFormat => 'انتخاب قالب';
  @override String get downloadAudio => 'فقط صدای ویدئو';
  @override String get copyDirectLink => 'کپی لینک مستقیم';
  @override String get rescan => 'بازاسکن منابع';
  @override String get min => 'دقیقه';
  @override String get hoursShort => 'ساعت';
  @override String get dayShort => 'روز';
}
