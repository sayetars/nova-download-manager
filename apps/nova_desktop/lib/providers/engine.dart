import 'dart:async';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/engine_client.dart';
import '../core/models.dart';

/// IPC connection to the engine with auto-reconnect. All pages depend on this.
final engineProvider = NotifierProvider<EngineNotifier, EngineClient?>(EngineNotifier.new);

class EngineNotifier extends Notifier<EngineClient?> {
  Timer? _reconnectTimer;

  @override
  EngineClient? build() {
    ref.onDispose(() {
      _reconnectTimer?.cancel();
    });
    _boot();
    return null;
  }

  Future<void> _boot() async {
    if (!Platform.isWindows && !Platform.isLinux && !Platform.isMacOS) {
      return; // dev on other hosts: stay offline; engine runs separately
    }
    final client = EngineClient();
    try {
      await client.connect();
    } catch (_) {
      // engine not up yet — spawn it at user level, then retry
      await _tryLaunchEngine();
      await client.connect();
    }
    state = client;

    client.events.listen((env) {
      // events are consumed by tasksProvider; but a drop of the connection
      // resubscribes and refreshes the full state.
    });
  }

  Future<void> _tryLaunchEngine() async {
    if (!Platform.isWindows && !Platform.isLinux && !Platform.isMacOS) return;
    try {
      final exe = Platform.resolvedExecutable;
      final dir = File(exe).parent.path;
      final engine = Platform.isWindows ? '$dir\\NovaEngine.exe' : '$dir/NovaEngine';
      if (File(engine).existsSync()) {
        await Process.start(engine, [], mode: ProcessStartMode.detached);
        await Future<void>.delayed(const Duration(milliseconds: 900));
      }
    } catch (_) {/* engine launch is best-effort; UI shows offline state */}
  }

  Future<void> reconnect() async {
    final old = state;
    await old?.dispose();
    await _boot();
  }
}

/// Live task list — refreshed by RPC and kept in sync by engine events.
final tasksProvider =
    NotifierProvider<TasksNotifier, AsyncValue<List<TaskUi>>>(TasksNotifier.new);

class TasksNotifier extends Notifier<AsyncValue<List<TaskUi>>> {
  @override
  AsyncValue<List<TaskUi>> build() {
    ref.listen(engineProvider, (prev, next) {
      if (next != null && prev == null) _load();
    });
    _subscribe();
    return const AsyncValue.loading();
  }

  StreamSubscription? _sub;
  bool _subscribed = false;

  void _subscribe() {
    if (_subscribed) return;
    _subscribed = true;
    ref.onDispose(() => _sub?.cancel());
  }

  Future<void> _load() async {
    final engine = ref.read(engineProvider);
    if (engine == null) {
      state = const AsyncValue.data([]);
      return;
    }
    state = const AsyncValue.loading();
    try {
      final resp = await engine.call('list_tasks', {
        'filter': 'all',
        'limit': 2000,
      });
      final list = (resp['tasks'] as List)
          .map((e) => TaskUi.fromJson((e as Map).cast<String, dynamic>()))
          .toList();
      state = AsyncValue.data(list);
    } catch (e, st) {
      state = AsyncValue.error(e, st);
    }
    _wireEvents();
  }

  void _wireEvents() {
    final engine = ref.read(engineProvider);
    if (engine == null) return;
    _sub ??= engine.events.listen((env) {
      switch (env.type) {
        case 'task_updated':
          _applyUpdate(env.payloadMap);
        case 'task_progress':
          _applyProgress(env.payloadMap);
        case 'task_completed':
          _applyUpdate(env.payloadMap?['task'] as Map<String, dynamic>?);
        case 'task_failed':
          _applyUpdate(env.payloadMap?['task'] as Map<String, dynamic>?);
        case 'engine_status':
        // status provider handles this via its own subscription
      }
    });
  }

  void _applyUpdate(Map<String, dynamic>? json) {
    if (json == null) return;
    final task = TaskUi.fromJson(json);
    state = state.whenData((list) {
      final i = list.indexWhere((t) => t.taskKey == task.taskKey);
      final copy = List<TaskUi>.of(list);
      if (i >= 0) {
        copy[i] = task;
      } else {
        copy.insert(0, task);
      }
      return copy;
    });
  }

  void _applyProgress(Map<String, dynamic>? json) {
    if (json == null) return;
    final key = json['task_key'] as String? ?? '';
    state = state.whenData((list) {
      final i = list.indexWhere((t) => t.taskKey == key);
      if (i < 0) return list;
      final copy = List<TaskUi>.of(list);
      copy[i] = copy[i].withProgress(json);
      return copy;
    });
  }

  // ---- commands ------------------------------------------------------------

  Future<String?> addDownload({
    required String url,
    String? fileName,
    String? destinationDir,
    int? maxConnections,
    bool autoStart = true,
    List<String> mirrors = const [],
    int? startAtMs,
    String? postAction,
  }) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return null;
    final resp = await engine.call('add_download', {
      'url': url,
      if (fileName != null && fileName.isNotEmpty) 'file_name': fileName,
      if (destinationDir != null && destinationDir.isNotEmpty) 'destination_dir': destinationDir,
      if (maxConnections != null) 'max_connections': maxConnections,
      if (mirrors.isNotEmpty) 'mirrors': mirrors,
      if (startAtMs != null) 'start_at_ms': startAtMs,
      if (postAction != null && postAction.isNotEmpty) 'post_action': postAction,
      'auto_start': autoStart,
    });
    await _load();
    return resp['task_key'] as String?;
  }

  /// Batch add (IDM parity): list of URLs, or one [01-99] pattern.
  Future<List<String>> addBatch(List<String> urls, {String? fileName, int? maxConnections, bool autoStart = true}) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return [];
    final tasks = urls
        .map((u) => {
              'url': u,
              if (fileName != null && fileName.isNotEmpty) 'file_name': fileName,
              if (maxConnections != null) 'max_connections': maxConnections,
              'auto_start': autoStart,
            })
        .toList();
    final resp = await engine.call('add_downloads', {'items': tasks});
    await _load();
    final list = (resp['tasks'] as List? ?? []);
    return list.map((e) => (e as Map)['task_key'] as String? ?? '').toList();
  }

  Future<List<String>> expandPattern(String pattern) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return [];
    final resp = await engine.call('expand_pattern', {'root_url': pattern});
    return ((resp['urls'] as List?) ?? []).map((e) => e.toString()).toList();
  }

  Future<void> updateTask(String key,
      {int? maxConnections,
      int? speedCapBytes,
      String? queueName,
      String? category,
      int? priority,
      String? postAction,
      List<String>? mirrors,
      int? startAtMs,
      String? referer}) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return;
    await engine.call('update_task', {
      'task_key': key,
      if (maxConnections != null) 'max_connections': maxConnections,
      if (speedCapBytes != null) 'speed_cap_bytes': speedCapBytes,
      if (queueName != null && queueName.isNotEmpty) 'queue_name': queueName,
      if (category != null && category.isNotEmpty) 'category': category,
      if (priority != null) 'priority': priority,
      if (postAction != null) 'post_action': postAction,
      if (mirrors != null) 'mirrors': mirrors,
      if (startAtMs != null) 'start_at_ms': startAtMs,
      if (referer != null) 'referer': referer,
    });
    await _load();
  }

  Future<List<Map<String, dynamic>>> zipList(String taskKey) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return [];
    final resp = await engine.call('zip_list', {'task_key': taskKey});
    return ((resp['entries'] as List?) ?? [])
        .map((e) => Map<String, dynamic>.from(e as Map))
        .toList();
  }

  Future<String?> zipExtract(String taskKey, List<String> entries) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return null;
    final resp = await engine.call('zip_extract', {'task_key': taskKey, 'entries': entries});
    await _load();
    return resp['dir'] as String?;
  }

  Future<String?> startGrabber(Map<String, dynamic> opts) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return null;
    final resp = await engine.call('grabber_start', opts);
    return resp['run_id'] as String?;
  }

  Future<void> pause(String key) => _cmd('pause_task', key);
  Future<void> resume(String key) => _cmd('resume_task', key);
  Future<void> cancel(String key) => _cmd('cancel_task', key);
  Future<void> retry(String key) => _cmd('retry_task', key);

  Future<void> openFile(String key) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return;
    final resp = await engine.call('open_file', {'task_key': key});
    final path = resp['path'] as String?;
    if (path == null || !Platform.isLinux && !Platform.isWindows && !Platform.isMacOS) return;
    try {
      if (Platform.isWindows) {
        await Process.start('explorer.exe', [path], mode: ProcessStartMode.detached);
      } else if (Platform.isLinux) {
        await Process.start('xdg-open', [path], mode: ProcessStartMode.detached);
      } else {
        await Process.start('open', [path], mode: ProcessStartMode.detached);
      }
    } catch (_) {/* desktop handler may be missing */}
  }

  Future<void> remove(String key, {bool deleteFiles = false}) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return;
    await engine.call('remove_task', {'task_key': key, 'delete_file': deleteFiles});
    state = state.whenData((l) => l.where((t) => t.taskKey != key).toList());
  }

  Future<void> setPriority(String key, int priority) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return;
    await engine.call('move_task', {'task_key': key, 'priority': priority});
  }

  Future<void> _cmd(String type, String key) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return;
    await engine.call(type, {'task_key': key});
    await refresh();
  }

  Future<void> refresh() => _load();

  Future<void> clearCompleted() async {
    final engine = ref.read(engineProvider);
    if (engine == null) return;
    await engine.call('clear_completed');
    await _load();
  }
}

/// Engine status: connection + live stats.
final engineStatusProvider =
    StreamNotifierProvider<EngineStatusNotifier, EngineStatusUi>(EngineStatusNotifier.new);

class EngineStatusNotifier extends StreamNotifier<EngineStatusUi> {
  @override
  Stream<EngineStatusUi> build() async* {
    var engine = ref.read(engineProvider);
    while (engine == null) {
      await Future<void>.delayed(const Duration(milliseconds: 300));
      engine = ref.read(engineProvider);
    }
    try {
      final resp = await engine.call('engine_status');
      yield EngineStatusUi.fromJson(resp);
    } catch (_) {}
    await for (final env in engine.events) {
      if (env.type == 'engine_status') {
        yield EngineStatusUi.fromJson(env.payloadMap ?? {});
      }
    }
  }
}

/// Queues list.
final queuesProvider = FutureProvider.autoDispose<List<QueueUi>>((ref) async {
  final engine = ref.watch(engineProvider);
  if (engine == null) return [];
  final resp = await engine.call('list_queues');
  return (resp['queues'] as List)
      .map((e) => QueueUi.fromJson((e as Map).cast<String, dynamic>()))
      .toList();
});

/// Settings (key/value) from the engine.
final settingsProvider =
    NotifierProvider<SettingsNotifier, Map<String, String>>(SettingsNotifier.new);

class SettingsNotifier extends Notifier<Map<String, String>> {
  @override
  Map<String, String> build() {
    ref.listen(engineProvider, (prev, next) {
      if (next != null && prev == null) _load();
    });
    return {};
  }

  Future<void> _load() async {
    final engine = ref.read(engineProvider);
    if (engine == null) return;
    try {
      final resp = await engine.call('get_settings');
      state = (resp['values'] as Map).cast<String, String>();
    } catch (_) {}
  }

  Future<void> set(String key, String value) async {
    final engine = ref.read(engineProvider);
    if (engine == null) return;
    await engine.call('set_settings', {
      'values': {key: value}
    });
    state = {...state, key: value};
  }
}

/// Scheduler rules.
final schedulesProvider = FutureProvider.autoDispose<List<ScheduleRuleUi>>((ref) async {
  final engine = ref.watch(engineProvider);
  if (engine == null) return [];
  final resp = await engine.call('get_schedules');
  return (resp['rules'] as List)
      .map((e) => ScheduleRuleUi.fromJson((e as Map).cast<String, dynamic>()))
      .toList();
});
