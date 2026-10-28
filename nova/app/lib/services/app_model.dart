import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';

import '../services/ipc_client.dart';

/// Central app state: task list, engine connection, settings, event stream.
class AppModel extends ChangeNotifier {
  final NovaIpcClient ipc = NovaIpcClient();

  bool engineOnline = false;
  List<Map<String, dynamic>> tasks = [];
  String filterCategory = 'all';
  Map<String, String> settings = {};
  final List<double> speedHistory = [];

  StreamSubscription? _evSub;
  Timer? _pollTimer;
  Timer? _speedTimer;

  List<Map<String, dynamic>> get filteredTasks {
    if (filterCategory == 'all') return tasks;
    return tasks.where((t) => t['category'] == filterCategory).toList();
  }

  Future<void> start() async {
    await _tryConnect();
    _pollTimer = Timer.periodic(const Duration(seconds: 2), (_) => refreshTasks());
    _speedTimer = Timer.periodic(const Duration(seconds: 1), (_) => _updateSpeed());
    _evSub = ipc.events.listen(_onEvent);
  }

  Future<void> _tryConnect() async {
    try {
      await ipc.connect();
      engineOnline = true;
      await refreshTasks();
    } catch (_) {
      engineOnline = false;
      // engine may need starting — UI shows "starting engine…" and retries
      await Future.delayed(const Duration(seconds: 2));
      if (!disposed) await _tryConnect();
    }
    notifyListeners();
  }

  void _onEvent(Map<String, dynamic> env) {
    switch (env['type']) {
      case 'ev.task.progress':
        _applyProgress(env['payload'] as Map<String, dynamic>);
        break;
      case 'ev.task.state':
        refreshTasks();
        break;
      case 'ev.speed.global':
        final dl = (env['payload']?['download'] ?? 0) as num;
        speedHistory.add(dl.toDouble());
        if (speedHistory.length > 120) speedHistory.removeAt(0);
        break;
    }
    notifyListeners();
  }

  void _applyProgress(Map<String, dynamic> p) {
    final task = tasks.where((t) => t['id'] == p['taskId']).firstOrNull;
    if (task == null) {
      refreshTasks();
      return;
    }
    task['receivedBytes'] = p['receivedBytes'] ?? task['receivedBytes'];
    task['totalBytes'] = p['totalBytes'] ?? task['totalBytes'];
    task['state'] = p['state'] ?? task['state'];
    task['speedBytesPerSec'] = p['speedBytesPerSec'] ?? 0;
  }

  void _updateSpeed() {
    double sum = 0;
    for (final t in tasks) {
      sum += ((t['speedBytesPerSec'] ?? 0) as num).toDouble();
    }
    speedHistory.add(sum);
    if (speedHistory.length > 120) speedHistory.removeAt(0);
    notifyListeners();
  }

  Future<void> refreshTasks() async {
    if (!ipc.connected) return;
    try {
      final payload = await ipc.callPayload('task.list');
      final list = (payload as List).map((e) => e as Map<String, dynamic>).toList();
      tasks = list;
      notifyListeners();
    } catch (_) {/* transient */}
  }

  Future<String> addUrl(String url, {String? saveDir, String category = 'general'}) async {
    final task = await ipc.callPayload('task.addUrl', {
      'url': url,
      if (saveDir != null) 'saveDirectory': saveDir,
      'category': category,
    });
    refreshTasks();
    return (task as Map<String, dynamic>)['id'] as String;
  }

  Future<void> pause(String id) => ipc.call('task.pause', id);
  Future<void> resume(String id) => ipc.call('task.resume', id);
  Future<void> cancel(String id) => ipc.call('task.cancel', id);
  Future<void> remove(String id) async {
    await ipc.call('task.remove', id);
    refreshTasks();
  }

  Future<void> setGlobalSpeedLimit(int bytesPerSec) =>
      ipc.call('speed.setGlobal', bytesPerSec);

  Future<Map<String, dynamic>?> getTask(String id) async {
    final t = await ipc.callPayload('task.get', id);
    return t as Map<String, dynamic>?;
  }

  void setCategoryFilter(String cat) {
    filterCategory = cat;
    notifyListeners();
  }

  bool disposed = false;

  @override
  void dispose() {
    disposed = true;
    _evSub?.cancel();
    _pollTimer?.cancel();
    _speedTimer?.cancel();
    ipc.close();
    super.dispose();
  }
}

/// Helpers shared by widgets
String formatBytes(dynamic bytes, [int precision = 1]) {
  final b = ((bytes ?? 0) as num).toDouble();
  if (b < 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  var i = 0;
  var v = b;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return '${v.toStringAsFixed(v >= 100 || i == 0 ? 0 : precision)} ${units[i]}';
}

String formatSpeed(dynamic bytesPerSec) =>
    '${formatBytes(bytesPerSec)}/s';

String formatEta(dynamic seconds) {
  final s = ((seconds ?? -1) as num).toDouble();
  if (s < 0) return '—';
  if (s < 60) return '${s.round()}s';
  if (s < 3600) return '${(s / 60).floor()}m ${s.round() % 60}s';
  return '${(s / 3600).floor()}h ${(s % 3600 / 60).floor()}m';
}

String stateLabel(String state, Map<String, String> l10n) {
  switch (state) {
    case 'downloading':
      return l10n['downloading'] ?? 'Downloading';
    case 'paused':
      return l10n['paused'] ?? 'Paused';
    case 'completed':
      return l10n['completed'] ?? 'Completed';
    case 'failed':
      return l10n['failed'] ?? 'Failed';
    case 'queued':
      return l10n['queued'] ?? 'Queued';
    case 'retrying':
      return l10n['retrying'] ?? 'Retrying…';
    case 'verifying':
      return l10n['verifying'] ?? 'Verifying…';
    default:
      return state;
  }
}

/// Localization loader — simple JSON based, no codegen needed for fa/en.
class L10n {
  final Map<String, String> strings;
  L10n(this.strings);

  static Future<L10n> load(Locale locale) async {
    final bundle = {
      'en': enStrings,
      'fa': faStrings,
    };
    return L10n(bundle[locale.languageCode] ?? bundle['en']!);
  }

  String operator [](String key) => strings[key] ?? key;
}

const Map<String, String> enStrings = {
  'appTitle': 'Nova Download Manager',
  'downloads': 'Downloads',
  'addUrl': 'Add URL',
  'pause': 'Pause',
  'resume': 'Resume',
  'cancel': 'Cancel',
  'retry': 'Retry',
  'remove': 'Remove',
  'settings': 'Settings',
  'speed': 'Speed',
  'downloading': 'Downloading',
  'paused': 'Paused',
  'completed': 'Completed',
  'failed': 'Failed',
  'queued': 'Queued',
  'retrying': 'Retrying…',
  'verifying': 'Verifying…',
  'emptyList': 'Nothing here yet.\nAdd a URL or browse the web — Nova catches it.',
  'engineOffline': 'Engine offline',
  'engineOnline': 'Engine online',
  'video': 'Video',
  'music': 'Music',
  'documents': 'Documents',
  'compressed': 'Compressed',
  'programs': 'Programs',
};

const Map<String, String> faStrings = {
  'appTitle': 'مدیریت دانلود نووا',
  'downloads': 'دانلودها',
  'addUrl': 'افزودن لینک',
  'pause': 'توقف',
  'resume': 'ادامه',
  'cancel': 'لغو',
  'retry': 'تلاش مجدد',
  'remove': 'حذف',
  'settings': 'تنظیمات',
  'speed': 'سرعت',
  'downloading': 'در حال دانلود',
  'paused': 'متوقف',
  'completed': 'کامل شد',
  'failed': 'ناموفق',
  'queued': 'در صف',
  'retrying': 'تلاش مجدد…',
  'verifying': 'در حال تأیید…',
  'emptyList': 'هنوز چیزی اینجا نیست.\nیک لینک اضافه کنید!',
  'engineOffline': 'موتور خاموش است',
  'engineOnline': 'موتور فعال',
  'video': 'ویدیو',
  'music': 'موسیقی',
  'documents': 'اسناد',
  'compressed': 'فشرده',
  'programs': 'برنامه‌ها',
};
