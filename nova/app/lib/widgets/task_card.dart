import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../services/app_model.dart';

/// One download row: filename, progress bar, speed, ETA, actions.
class TaskCard extends StatelessWidget {
  final Map<String, dynamic> task;
  const TaskCard({super.key, required this.task});

  @override
  Widget build(BuildContext context) {
    final model = context.watch<AppModel>();
    final state = (task['state'] ?? 'created') as String;
    final received = task['receivedBytes'] ?? 0;
    final total = task['totalBytes'] ?? -1;
    final progress = total > 0 ? (received as num) / (total as num) : 0.0;
    final speed = task['speedBytesPerSec'] ?? 0;
    final eta = (total > 0 && (speed as num) > 0)
        ? ((total - received) / speed).round()
        : -1;
    final fileName = task['fileName'] ?? task['url'] ?? '…';
    final isVideo = task['category'] == 'video' || task['video'] != null;
    final persian = Localizations.localeOf(context).languageCode == 'fa';

    final stateColor = switch (state) {
      'downloading' => Colors.blue,
      'completed' => Colors.green,
      'paused' => Colors.orange,
      'failed' => Colors.red,
      'retrying' => Colors.amber,
      'verifying' => Colors.purple,
      'queued' => Colors.grey,
      _ => Colors.grey,
    };

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
      child: InkWell(
        borderRadius: BorderRadius.circular(12),
        onTap: () => _openDetail(context, model),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(_stateIcon(state), color: stateColor, size: 22),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      fileName,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(fontWeight: FontWeight.w600),
                    ),
                  ),
                  Text(
                    '${formatBytes(received)} / ${formatBytes(total)}',
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ],
              ),
              const SizedBox(height: 10),
              ClipRRect(
                borderRadius: BorderRadius.circular(4),
                child: LinearProgressIndicator(
                  value: state == 'completed' ? 1 : (total > 0 ? progress : null),
                  minHeight: 6,
                  backgroundColor:
                      Theme.of(context).colorScheme.surfaceContainerHighest,
                ),
              ),
              const SizedBox(height: 8),
              Row(
                children: [
                  Container(
                    padding:
                        const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                    decoration: BoxDecoration(
                      color: stateColor.withValues(alpha: .12),
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Text(
                      stateLabel(state),
                      style: TextStyle(color: stateColor, fontSize: 11),
                    ),
                  ),
                  const SizedBox(width: 10),
                  if (state == 'downloading') ...[
                    const Icon(Icons.arrow_downward_rounded,
                        size: 14, color: Colors.blue),
                    const SizedBox(width: 3),
                    Text(formatSpeed(speed),
                        style: const TextStyle(
                            fontSize: 12, fontFeatures: [FontFeature.tabularFigures()])),
                    const SizedBox(width: 10),
                    Text(formatEta(eta),
                        style: const TextStyle(
                            fontSize: 12, fontFeatures: [FontFeature.tabularFigures()])),
                  ],
                  const Spacer(),
                  // --- actions ---
                  if (isVideo && state == 'downloading')
                    IconButton(
                      icon: const Icon(Icons.play_circle_outline_rounded),
                      tooltip: persian ? 'پیش‌نمایش' : 'Preview',
                      onPressed: () => _openPreview(context, model),
                    ),
                  if (state == 'downloading')
                    IconButton(
                      icon: const Icon(Icons.pause_circle_outline_rounded),
                      tooltip: persian ? 'توقف' : 'Pause',
                      onPressed: () => model.pause(task['id']),
                    ),
                  if (state == 'paused' ||
                      state == 'failed' ||
                      state == 'interrupted')
                    IconButton(
                      icon: const Icon(Icons.play_circle_outline_rounded),
                      tooltip: persian ? 'ادامه' : 'Resume',
                      onPressed: () => model.resume(task['id']),
                    ),
                  if (state == 'completed')
                    IconButton(
                      icon: const Icon(Icons.folder_open_rounded),
                      tooltip: persian ? 'پوشه' : 'Folder',
                      onPressed: () {}, // engine task.openFile
                    ),
                  PopupMenuButton<String>(
                    onSelected: (cmd) {
                      switch (cmd) {
                        case 'cancel':
                          model.cancel(task['id']);
                        case 'remove':
                          model.remove(task['id']);
                        case 'copy':
                          // copy URL
                          break;
                      }
                    },
                    itemBuilder: (_) => [
                      const PopupMenuItem(
                          value: 'copy', child: Text('Copy URL')),
                      const PopupMenuItem(
                          value: 'cancel', child: Text('Cancel')),
                      const PopupMenuItem(
                          value: 'remove', child: Text('Remove')),
                    ],
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  IconData _stateIcon(String state) => switch (state) {
        'downloading' => Icons.download_rounded,
        'completed' => Icons.check_circle_outline_rounded,
        'paused' => Icons.pause_circle_outline_rounded,
        'failed' => Icons.error_outline_rounded,
        'verifying' => Icons.verified_outlined,
        _ => Icons.schedule_rounded,
      };

  String stateLabel(String state) => switch (state) {
        'downloading' => 'Downloading',
        'completed' => 'Completed',
        'paused' => 'Paused',
        'failed' => 'Failed',
        'queued' => 'Queued',
        'retrying' => 'Retrying…',
        'verifying' => 'Verifying…',
        _ => state,
      };

  void _openDetail(BuildContext context, AppModel model) {
    // task detail sheet: segments, speed graph, headers, error message
    showModalBottomSheet(
      context: context,
      showDragHandle: true,
      builder: (context) => _TaskDetailSheet(task: task),
    );
  }

  void _openPreview(BuildContext context, AppModel model) {
    Navigator.push(
      context,
      MaterialPageRoute(
        builder: (_) => _VideoPreviewPage(
          taskId: task['id'],
          fileName: task['fileName'] ?? 'video',
          model: model,
        ),
      ),
    );
  }
}

class _TaskDetailSheet extends StatelessWidget {
  final Map<String, dynamic> task;
  const _TaskDetailSheet({required this.task});

  @override
  Widget build(BuildContext context) {
    final segments = (task['segments'] as List?) ?? [];
    return DraggableScrollableSheet(
      expand: false,
      initialChildSize: .5,
      builder: (context, _) => ListView(
        padding: const EdgeInsets.all(20),
        children: [
          Text(task['fileName'] ?? '',
              style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 6),
          Text(task['url'] ?? '',
              style: Theme.of(context).textTheme.bodySmall),
          const Divider(height: 24),
          if (segments.isNotEmpty) ...[
            const Text('Segments',
                style: TextStyle(fontWeight: FontWeight.bold)),
            const SizedBox(height: 8),
            for (final seg in segments.cast<Map<String, dynamic>>())
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 2),
                child: Row(
                  children: [
                    SizedBox(
                      width: 56,
                      child: Text('#${seg['index']}',
                          style: const TextStyle(fontSize: 12)),
                    ),
                    Expanded(
                      child: ClipRRect(
                        borderRadius: BorderRadius.circular(3),
                        child: LinearProgressIndicator(
                          value: seg['end'] != null && seg['end'] > 0
                              ? ((seg['received'] ?? 0) as num) /
                                  (((seg['end'] as num) -
                                          (seg['start'] ?? 0)) +
                                      1)
                              : null,
                          minHeight: 8,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
          ],
          if (task['errorMessage'] != null) ...[
            const SizedBox(height: 16),
            Text('Error',
                style: TextStyle(color: Theme.of(context).colorScheme.error)),
            Text('${task['errorMessage']}'),
          ],
        ],
      ),
    );
  }
}

class _VideoPreviewPage extends StatelessWidget {
  final String taskId;
  final String fileName;
  final AppModel model;
  const _VideoPreviewPage(
      {required this.taskId, required this.fileName, required this.model});

  @override
  Widget build(BuildContext context) {
    final persian = Localizations.localeOf(context).languageCode == 'fa';
    return Scaffold(
      appBar: AppBar(title: Text(persian ? 'پیش‌نمایش ویدیو' : 'Video Preview')),
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(Icons.videocam_outlined, size: 64),
            const SizedBox(height: 12),
            Text(fileName, style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 24),
            // media_kit player attaches to the .novapart file for
            // sequential downloads — see players/video_preview_player.dart
            const Text('Player mounts on .novapart (sequential mode)'),
            const SizedBox(height: 8),
            ElevatedButton.icon(
              icon: const Icon(Icons.play_arrow_rounded),
              label: Text(persian ? 'پخش' : 'Play partial file'),
              onPressed: () {},
            ),
          ],
        ),
      ),
    );
  }
}
