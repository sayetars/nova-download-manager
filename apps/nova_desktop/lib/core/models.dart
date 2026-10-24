import 'dart:convert';

import '../core/protocol.dart';

/// Immutable UI-side snapshot of a download task (mirrors TaskSnapshot in C#).
class TaskUi {
  final String taskKey;
  final String state;
  final String kind;
  final String fileName;
  final String url;
  final String? effectiveUrl;
  final String destinationDir;
  final String queueName;
  final String category;
  final int totalBytes;
  final int receivedBytes;
  final int segmentCount;
  final int segmentsDone;
  final int segmentsTotal;
  final String? errorCode;
  final String? errorMessage;
  final String? errorCategory;
  final String? validation;
  final int priority;
  final int retryCount;
  final int createdAtMs;
  final int? startedAtMs;
  final int? completedAtMs;
  // live progress fields (updated by task_progress events without full refresh)
  final int speedBps;
  final int etaSeconds;

  const TaskUi({
    required this.taskKey,
    this.state = 'created',
    this.kind = 'file',
    this.fileName = '',
    this.url = '',
    this.effectiveUrl,
    this.destinationDir = '',
    this.queueName = 'main',
    this.category = 'other',
    this.totalBytes = -1,
    this.receivedBytes = 0,
    this.segmentCount = 1,
    this.segmentsDone = 0,
    this.segmentsTotal = 0,
    this.errorCode,
    this.errorMessage,
    this.errorCategory,
    this.validation,
    this.priority = 0,
    this.retryCount = 0,
    this.createdAtMs = 0,
    this.startedAtMs,
    this.completedAtMs,
    this.speedBps = 0,
    this.etaSeconds = -1,
  });

  bool get isRunning =>
      state == 'downloading' || state == 'analyzing' || state == 'verifying' || state == 'pausing' || state == 'retrying';

  double get progress {
    if (totalBytes <= 0) {
      if (segmentsTotal > 0) return segmentsDone / segmentsTotal;
      return 0;
    }
    return (receivedBytes / totalBytes).clamp(0.0, 1.0);
  }

  factory TaskUi.fromJson(Map<String, dynamic> j) => TaskUi(
        taskKey: (j['task_key'] ?? '') as String,
        state: (j['state'] ?? 'created') as String,
        kind: (j['kind'] ?? 'file') as String,
        fileName: (j['file_name'] ?? '') as String,
        url: (j['url'] ?? '') as String,
        effectiveUrl: j['effective_url'] as String?,
        destinationDir: (j['destination_dir'] ?? '') as String,
        queueName: (j['queue_name'] ?? 'main') as String,
        category: (j['category'] ?? 'other') as String,
        totalBytes: (j['total_bytes'] ?? -1) as int,
        receivedBytes: (j['received_bytes'] ?? 0) as int,
        segmentCount: (j['segment_count'] ?? 1) as int,
        segmentsDone: (j['segments_done'] ?? 0) as int,
        segmentsTotal: (j['segments_total'] ?? 0) as int,
        errorCode: j['error_code'] as String?,
        errorMessage: j['error_message'] as String?,
        errorCategory: j['error_category'] as String?,
        validation: j['validation'] as String?,
        priority: (j['priority'] ?? 0) as int,
        retryCount: (j['retry_count'] ?? 0) as int,
        createdAtMs: (j['created_at_ms'] ?? 0) as int,
        startedAtMs: j['started_at_ms'] as int?,
        completedAtMs: j['completed_at_ms'] as int?,
      );

  TaskUi withProgress(Map<String, dynamic> p) => TaskUi(
        taskKey: taskKey,
        state: (p['state'] as String?) ?? state,
        kind: kind,
        fileName: fileName,
        url: url,
        effectiveUrl: effectiveUrl,
        destinationDir: destinationDir,
        queueName: queueName,
        category: category,
        totalBytes: (p['total_bytes'] as int?) ?? totalBytes,
        receivedBytes: (p['received_bytes'] as int?) ?? receivedBytes,
        segmentCount: segmentCount,
        segmentsDone: (p['segments_done'] as int?) ?? segmentsDone,
        segmentsTotal: (p['segments_total'] as int?) ?? segmentsTotal,
        errorCode: errorCode,
        errorMessage: errorMessage,
        errorCategory: errorCategory,
        validation: validation,
        priority: priority,
        retryCount: retryCount,
        createdAtMs: createdAtMs,
        startedAtMs: startedAtMs,
        completedAtMs: completedAtMs,
        speedBps: (p['speed'] as int?) ?? speedBps,
        etaSeconds: (p['eta_seconds'] as int?) ?? etaSeconds,
      );
}

class EngineStatusUi {
  final int activeTasks;
  final int queuedTasks;
  final int globalSpeed;
  final int uptimeSeconds;
  final String schedulerState;
  final String platform;
  final QuotaUi? quota;

  const EngineStatusUi({
    this.activeTasks = 0,
    this.queuedTasks = 0,
    this.globalSpeed = 0,
    this.uptimeSeconds = 0,
    this.schedulerState = 'idle',
    this.platform = '',
    this.quota,
  });

  factory EngineStatusUi.fromJson(Map<String, dynamic> j) => EngineStatusUi(
        activeTasks: (j['active_tasks'] ?? 0) as int,
        queuedTasks: (j['queued_tasks'] ?? 0) as int,
        globalSpeed: (j['global_speed'] ?? 0) as int,
        uptimeSeconds: (j['uptime_seconds'] ?? 0) as int,
        schedulerState: (j['scheduler_state'] as String?) ?? 'idle',
        platform: (j['platform'] as String?) ?? '',
        quota: j['quota'] == null ? null : QuotaUi.fromJson(Map<String, dynamic>.from(j['quota'] as Map)),
      );
}

/// Byte-quota window state (IDM parity).
class QuotaUi {
  final bool enabled;
  final int capBytes;
  final int windowMinutes;
  final int usedBytes;
  final bool paused;
  final int resumeInMs;

  const QuotaUi({
    this.enabled = false,
    this.capBytes = 0,
    this.windowMinutes = 60,
    this.usedBytes = 0,
    this.paused = false,
    this.resumeInMs = 0,
  });

  factory QuotaUi.fromJson(Map<String, dynamic> j) => QuotaUi(
        enabled: (j['enabled'] ?? false) as bool,
        capBytes: (j['cap_bytes'] ?? 0) as int,
        windowMinutes: (j['window_minutes'] ?? 60) as int,
        usedBytes: (j['used_bytes'] ?? 0) as int,
        paused: (j['paused'] ?? false) as bool,
        resumeInMs: (j['resume_in_ms'] ?? 0) as int,
      );
}

class QueueUi {
  final String name;
  final int maxConcurrent;
  final int speedCapBytes;
  final String destinationDir;
  final String scheduleJson;
  final int activeCount;
  final int waitingCount;
  final int totalCount;

  const QueueUi({
    required this.name,
    this.maxConcurrent = 0,
    this.speedCapBytes = 0,
    this.destinationDir = '',
    this.scheduleJson = '',
    this.activeCount = 0,
    this.waitingCount = 0,
    this.totalCount = 0,
  });

  factory QueueUi.fromJson(Map<String, dynamic> j) => QueueUi(
        name: (j['name'] ?? '') as String,
        maxConcurrent: (j['max_concurrent'] ?? 0) as int,
        speedCapBytes: (j['speed_cap_bytes'] ?? 0) as int,
        destinationDir: (j['destination_dir'] ?? '') as String,
        scheduleJson: (j['schedule_json'] ?? '') as String,
        activeCount: (j['active_count'] ?? 0) as int,
        waitingCount: (j['waiting_count'] ?? 0) as int,
        totalCount: (j['total_count'] ?? 0) as int,
      );

  /// Parsed per-queue schedule entries (IDM parity v1.2.0).
  List<QueueScheduleRuleUi> get schedule {
    try {
      final raw = scheduleJson.isEmpty ? <Object?>[] : jsonDecode(scheduleJson) as List;
      return raw
          .map((e) => QueueScheduleRuleUi.fromJson((e as Map).cast<String, dynamic>()))
          .toList();
    } catch (_) {
      return const [];
    }
  }
}

/// One per-queue start/stop schedule entry ("run" window / "pause" window).
class QueueScheduleRuleUi {
  final String action; // run | pause
  final String daysOfWeek; // "" = every day; comma list of 0..6
  final String startTime;
  final String endTime;
  final bool enabled;

  const QueueScheduleRuleUi({
    this.action = 'pause',
    this.daysOfWeek = '',
    this.startTime = '00:00',
    this.endTime = '23:59',
    this.enabled = true,
  });

  factory QueueScheduleRuleUi.fromJson(Map<String, dynamic> j) => QueueScheduleRuleUi(
        action: (j['action'] ?? j['Action'] ?? 'pause') as String,
        daysOfWeek: (j['days_of_week'] ?? j['DaysOfWeek'] ?? '') as String,
        startTime: (j['start_time'] ?? j['StartTime'] ?? '00:00') as String,
        endTime: (j['end_time'] ?? j['EndTime'] ?? '23:59') as String,
        enabled: (j['enabled'] ?? j['Enabled'] ?? true) as bool,
      );

  Map<String, dynamic> toJson() => {
        'action': action,
        'days_of_week': daysOfWeek,
        'start_time': startTime,
        'end_time': endTime,
        'enabled': enabled,
      };
}

/// One periodic synchronization rule (IDM parity v1.2.0).
class SyncRuleUi {
  final int id;
  final String name;
  final String urls;
  final String destinationDir;
  final String category;
  final int intervalMinutes;
  final bool enabled;
  final int lastRunMs;

  const SyncRuleUi({
    required this.id,
    this.name = '',
    this.urls = '',
    this.destinationDir = '',
    this.category = 'other',
    this.intervalMinutes = 60,
    this.enabled = true,
    this.lastRunMs = 0,
  });

  factory SyncRuleUi.fromJson(Map<String, dynamic> j) => SyncRuleUi(
        id: (j['id'] ?? 0) as int,
        name: (j['name'] ?? '') as String,
        urls: (j['urls'] ?? '') as String,
        destinationDir: (j['destination_dir'] ?? '') as String,
        category: (j['category'] ?? 'other') as String,
        intervalMinutes: (j['interval_minutes'] ?? 60) as int,
        enabled: (j['enabled'] ?? true) as bool,
        lastRunMs: (j['last_run_ms'] ?? 0) as int,
      );

  Map<String, dynamic> toJson() => {
        'id': id == 0 ? null : id,
        'name': name,
        'urls': urls,
        'destination_dir': destinationDir,
        'category': category,
        'interval_minutes': intervalMinutes,
        'enabled': enabled,
      };
}

class ScheduleRuleUi {
  final int id;
  final String name;
  final String daysOfWeek; // "" = every day; comma list of 0..6
  final String startTime;
  final String endTime;
  final bool enabled;

  const ScheduleRuleUi({
    required this.id,
    this.name = '',
    this.daysOfWeek = '',
    this.startTime = '00:00',
    this.endTime = '23:59',
    this.enabled = true,
  });

  factory ScheduleRuleUi.fromJson(Map<String, dynamic> j) => ScheduleRuleUi(
        id: (j['id'] ?? 0) as int,
        name: (j['name'] ?? '') as String,
        daysOfWeek: (j['days_of_week'] ?? '') as String,
        startTime: (j['start_time'] ?? '00:00') as String,
        endTime: (j['end_time'] ?? '23:59') as String,
        enabled: (j['enabled'] ?? true) as bool,
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'name': name,
        'days_of_week': daysOfWeek,
        'start_time': startTime,
        'end_time': endTime,
        'enabled': enabled,
      };
}

class MediaCandidateUi {
  final String id;
  final String url;
  final String kind; // file | hls | dash
  final String? container;
  final int? height;
  final int? sizeBytes;
  final bool sizeIsEstimate;
  final String? bitrate;
  final String? codecs;
  final String label;
  final bool downloadable;
  final String? note;

  const MediaCandidateUi({
    required this.id,
    required this.url,
    this.kind = 'file',
    this.container,
    this.height,
    this.sizeBytes,
    this.sizeIsEstimate = false,
    this.bitrate,
    this.codecs,
    this.label = '',
    this.downloadable = true,
    this.note,
  });

  factory MediaCandidateUi.fromJson(Map<String, dynamic> j) => MediaCandidateUi(
        id: (j['id'] ?? '') as String,
        url: (j['url'] ?? '') as String,
        kind: (j['kind'] ?? 'file') as String,
        container: j['container'] as String?,
        height: j['height'] as int?,
        sizeBytes: j['size_bytes'] as int?,
        sizeIsEstimate: (j['size_is_estimate'] ?? false) as bool,
        bitrate: j['bitrate'] as String?,
        codecs: j['codecs'] as String?,
        label: (j['label'] ?? '') as String,
        downloadable: (j['downloadable'] ?? true) as bool,
        note: j['note'] as String?,
      );

  bool get isBest =>
      kind == 'file' ||
      (height != null && (height! >= 1080)) ||
      (height == null && label.toLowerCase().contains('direct'));
}

/// Category metadata for the sidebar.
class DownloadFilter {
  final String value; // all | active | queued | completed | failed | paused | media
  const DownloadFilter(this.value);

  static const all = DownloadFilter('all');
  static const active = DownloadFilter('active');
  static const queued = DownloadFilter('queued');
  static const completed = DownloadFilter('completed');
  static const failed = DownloadFilter('failed');
  static const paused = DownloadFilter('paused');
  static const media = DownloadFilter('media');
}

/// RSS/Atom feed subscription (v1.3.0).
class FeedRuleUi {
  final int id;
  final String name;
  final String url;
  final String filters;
  final String destinationDir;
  final String category;
  final int intervalMinutes;
  final bool enabled;
  final int lastCheckMs;
  final bool autoStart;
  final int maxItems;

  const FeedRuleUi({
    required this.id,
    this.name = '',
    this.url = '',
    this.filters = '',
    this.destinationDir = '',
    this.category = 'other',
    this.intervalMinutes = 30,
    this.enabled = true,
    this.lastCheckMs = 0,
    this.autoStart = true,
    this.maxItems = 0,
  });

  factory FeedRuleUi.fromJson(Map<String, dynamic> j) => FeedRuleUi(
        id: (j['id'] as num).toInt(),
        name: j['name'] as String? ?? '',
        url: j['url'] as String? ?? '',
        filters: j['filters'] as String? ?? '',
        destinationDir: j['destination_dir'] as String? ?? '',
        category: j['category'] as String? ?? 'other',
        intervalMinutes: (j['interval_minutes'] as num?)?.toInt() ?? 30,
        enabled: j['enabled'] as bool? ?? true,
        lastCheckMs: (j['last_check_ms'] as num?)?.toInt() ?? 0,
        autoStart: j['auto_start'] as bool? ?? true,
        maxItems: (j['max_items'] as num?)?.toInt() ?? 0,
      );
}
