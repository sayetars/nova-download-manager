import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/models.dart';
import '../core/strings.dart';
import '../providers/engine.dart' as prov;

/// v1.5.0 (G17): partial-file preview dialog. The engine serves the downloaded
/// part of the file over a private loopback HTTP URL; this dialog hands it to
/// the system media player, keeps a copy-able URL around and shows live
/// availability (contiguous downloaded bytes) as the download progresses.
Future<void> showPreviewDialog(BuildContext context, TaskUi task) async {
  final notifier = ProviderScope.containerOf(context, listen: false)
      .read(prov.tasksProvider.notifier);
  Map<String, dynamic>? resp;
  try {
    resp = await notifier.previewStart(task.taskKey);
  } catch (e) {
    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('${S.of(context).previewTitle}: $e')));
    }
    return;
  }
  if (!context.mounted || resp == null) return;
  await showDialog(
    context: context,
    builder: (context) => _PreviewDialog(
      task: task,
      url: resp!['url'] as String? ?? '',
      mime: resp['mime'] as String? ?? '',
      s: S.of(context),
    ),
  );
}

class _PreviewDialog extends ConsumerStatefulWidget {
  final TaskUi task;
  final String url;
  final String mime;
  final S s;
  const _PreviewDialog(
      {required this.task, required this.url, required this.mime, required this.s});

  @override
  ConsumerState<_PreviewDialog> createState() => _PreviewDialogState();
}

class _PreviewDialogState extends ConsumerState<_PreviewDialog> {
  bool _copied = false;

  @override
  Widget build(BuildContext context) {
    final s = widget.s;
    // live task (progress updates while the dialog is open)
    final tasks = ref.watch(prov.tasksProvider);
    final list = tasks.value ?? const <TaskUi>[];
    final live = list.firstWhere((t) => t.taskKey == widget.task.taskKey,
        orElse: () => widget.task);
    final cs = Theme.of(context).colorScheme;

    final available = live.receivedBytes;
    final total = live.totalBytes > 0 ? live.totalBytes : -1;
    final progress = total > 0 ? (available / total).clamp(0.0, 1.0) : null;

    return AlertDialog(
      title: Row(
        children: [
          Icon(Icons.smart_display_rounded, color: cs.primary, size: 22),
          const SizedBox(width: 8),
          Expanded(
              child: Text(s.previewTitle,
                  overflow: TextOverflow.ellipsis)),
        ],
      ),
      content: SizedBox(
        width: 480,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(live.fileName,
                style: Theme.of(context).textTheme.titleSmall,
                overflow: TextOverflow.ellipsis),
            const SizedBox(height: 12),
            if (progress != null) ...[
              ClipRRect(
                borderRadius: BorderRadius.circular(6),
                child: LinearProgressIndicator(value: progress, minHeight: 8),
              ),
              const SizedBox(height: 6),
            ],
            Text(
              '${fmtBytes(available)} ${s.previewAvailable}'
              '${total > 0 ? ' / ${fmtBytes(total)}' : ''}',
              style: Theme.of(context).textTheme.bodyMedium,
            ),
            const SizedBox(height: 4),
            Text('${s.previewStarted} · ${widget.mime}',
                style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 12),
            Container(
              width: double.infinity,
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: cs.surfaceContainerHighest.withValues(alpha: .45),
                borderRadius: BorderRadius.circular(8),
              ),
              child: SelectableText(widget.url,
                  style: const TextStyle(fontSize: 12, fontFamily: 'monospace')),
            ),
            const SizedBox(height: 12),
            Text(s.previewHint,
                style: Theme.of(context)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: cs.onSurfaceVariant)),
          ],
        ),
      ),
      actions: [
        TextButton.icon(
          icon: Icon(_copied ? Icons.check_rounded : Icons.copy_rounded, size: 16),
          label: Text(s.previewCopyUrl),
          onPressed: () {
            Clipboard.setData(ClipboardData(text: widget.url));
            setState(() => _copied = true);
            Future.delayed(const Duration(seconds: 2), () {
              if (mounted) setState(() => _copied = false);
            });
          },
        ),
        FilledButton.icon(
          icon: const Icon(Icons.play_circle_rounded, size: 18),
          label: Text(s.previewOpenPlayer),
          onPressed: () {
            try {
              if (Platform.isWindows) {
                Process.start('cmd', ['/c', 'start', '', widget.url],
                    mode: ProcessStartMode.detached);
              } else if (Platform.isLinux) {
                Process.start('xdg-open', [widget.url],
                    mode: ProcessStartMode.detached);
              } else {
                Process.start('open', [widget.url], mode: ProcessStartMode.detached);
              }
            } catch (_) {/* no handler */}
          },
        ),
        TextButton(onPressed: () => Navigator.pop(context), child: Text(s.close)),
      ],
    );
  }

  static String fmtBytes(int b) {
    if (b < 1024) return '$b B';
    if (b < 1024 * 1024) return '${(b / 1024).toStringAsFixed(1)} KB';
    if (b < 1024 * 1024 * 1024) return '${(b / 1048576).toStringAsFixed(1)} MB';
    return '${(b / 1073741824).toStringAsFixed(2)} GB';
  }
}
