import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/models.dart';
import '../core/strings.dart';
import '../core/theme.dart';
import '../providers/engine.dart' as prov;

/// The download list — reused across every filter (all/active/queued/…/media).
class DownloadsPage extends ConsumerWidget {
  final DownloadFilter filter;
  final String search;
  const DownloadsPage({super.key, required this.filter, required this.search});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final tasksAsync = ref.watch(prov.tasksProvider);
    final engine = ref.watch(prov.engineProvider);

    return tasksAsync.when(
      loading: () => const Center(child: CircularProgressIndicator()),
      error: (e, _) => _offline(context, ref, s),
      data: (tasks) {
        var list = _applyFilter(tasks).toList();
        if (search.isNotEmpty) {
          list = list
              .where((t) =>
                  t.fileName.toLowerCase().contains(search.toLowerCase()) ||
                  t.url.toLowerCase().contains(search.toLowerCase()))
              .toList();
        }
        if (list.isEmpty) return _empty(context, s);
        return Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 10, 20, 6),
              child: Row(
                children: [
                  Text('${list.length}', style: Theme.of(context).textTheme.titleMedium!.copyWith(fontWeight: FontWeight.w800)),
                  const SizedBox(width: 6),
                  Text(s.downloaded, style: Theme.of(context).textTheme.bodySmall),
                  const Spacer(),
                  if (filter.value == 'completed')
                    TextButton.icon(
                      onPressed: () => ref.read(prov.tasksProvider.notifier).clearCompleted(),
                      icon: const Icon(Icons.clear_all_rounded, size: 18),
                      label: Text(s.clearCompleted),
                    ),
                ],
              ),
            ),
            Expanded(
              child: ListView.separated(
                padding: const EdgeInsets.fromLTRB(20, 6, 20, 20),
                itemCount: list.length,
                separatorBuilder: (_, __) => const SizedBox(height: 8),
                itemBuilder: (context, i) {
                  final task = list[i];
                  return TaskTile(
                    task: task,
                    onOpen: task.state == 'completed' ? () => _open(context, ref, task) : null,
                  );
                },
              ),
            ),
          ],
        );
      },
    );
  }

  Iterable<TaskUi> _applyFilter(List<TaskUi> all) => switch (filter.value) {
        'active' => all.where((t) => t.isRunning),
        'queued' => all.where((t) => t.state == 'queued' || t.state == 'created'),
        'completed' => all.where((t) => t.state == 'completed'),
        'failed' => all.where((t) => t.state == 'failed' || t.state == 'interrupted'),
        'paused' => all.where((t) => t.state == 'paused'),
        'media' => all.where((t) => t.kind != 'file'),
        _ => all,
      };

  Widget _empty(BuildContext context, S s) => Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.cloud_download_outlined,
                size: 64, color: Theme.of(context).colorScheme.outline),
            const SizedBox(height: 14),
            Text(s.noDownloads, style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 6),
            Text(s.noDownloadsHint, style: Theme.of(context).textTheme.bodySmall),
          ],
        ),
      );

  Widget _offline(BuildContext context, WidgetRef ref, S s) => Center(
        child: Padding(
          padding: const EdgeInsets.all(40),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.power_off_rounded, size: 56, color: NovaColors.warning),
              const SizedBox(height: 14),
              Text(s.engineDisconnected, style: Theme.of(context).textTheme.titleMedium),
              const SizedBox(height: 6),
              Text(s.engineDisconnectedHint,
                  textAlign: TextAlign.center, style: Theme.of(context).textTheme.bodySmall),
              const SizedBox(height: 16),
              FilledButton.icon(
                onPressed: () => ref.read(prov.engineProvider.notifier).reconnect(),
                icon: const Icon(Icons.refresh_rounded, size: 18),
                label: Text(s.retry),
              ),
            ],
          ),
        ),
      );

  Future<void> _open(BuildContext context, WidgetRef ref, TaskUi task) async {
    try {
      await ref.read(prov.engineProvider)?.call('open_file', {'task_key': task.taskKey});
    } catch (e) {
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('$e')));
      }
    }
  }
}

/// One download row: icon by category, progress, speed, actions.
class TaskTile extends ConsumerWidget {
  final TaskUi task;
  final VoidCallback? onOpen;
  const TaskTile({super.key, required this.task, this.onOpen});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final cs = Theme.of(context).colorScheme;
    final stateColor = NovaColors.stateColor(task.state, Theme.of(context).brightness);
    final tasks = ref.read(prov.tasksProvider.notifier);

    return Card(
      child: InkWell(
        borderRadius: BorderRadius.circular(NovaTheme.radius),
        onTap: () => showTaskDetails(context, task),
        onSecondaryTapUp: (d) => _menu(context, ref, d.globalPosition, task),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _categoryIcon(context),
              const SizedBox(width: 13),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            task.fileName.isEmpty ? task.url : task.fileName,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13.5),
                          ),
                        ),
                        const SizedBox(width: 8),
                        _stateChip(context, stateColor, s),
                      ],
                    ),
                    const SizedBox(height: 9),
                    ClipRRect(
                      borderRadius: BorderRadius.circular(4),
                      child: LinearProgressIndicator(
                        value: task.progress == 0 && task.isRunning ? null : task.progress,
                        minHeight: 5,
                        backgroundColor: cs.surfaceContainerHighest.withOpacity(0.55),
                        color: stateColor,
                      ),
                    ),
                    const SizedBox(height: 8),
                    Row(
                      children: [
                        Text(
                          '${Fmt.bytes(context, task.receivedBytes)}'
                          '${task.totalBytes > 0 ? ' / ${Fmt.bytes(context, task.totalBytes)}' : ''}',
                          style: TextStyle(fontSize: 11.5, color: cs.onSurfaceVariant),
                        ),
                        if (task.isRunning && task.speedBps > 0) ...[
                          const SizedBox(width: 10),
                          Icon(Icons.arrow_right_alt_rounded, size: 13, color: cs.onSurfaceVariant),
                          const SizedBox(width: 2),
                          Text(Fmt.speed(context, task.speedBps),
                              style: TextStyle(fontSize: 11.5, color: stateColor, fontWeight: FontWeight.w700)),
                          if (task.etaSeconds >= 0) ...[
                            const SizedBox(width: 8),
                            Text(Fmt.eta(context, task.etaSeconds),
                                style: TextStyle(fontSize: 11.5, color: cs.onSurfaceVariant)),
                          ],
                        ],
                        if (task.segmentsTotal > 0) ...[
                          const SizedBox(width: 10),
                          Text('${task.segmentsDone}/${task.segmentsTotal}',
                              style: TextStyle(fontSize: 11.5, color: cs.onSurfaceVariant)),
                        ],
                        const Spacer(),
                        _actions(context, ref, tasks, s),
                      ],
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _categoryIcon(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final (icon, color) = switch (task.category) {
      'video' => (Icons.movie_rounded, const Color(0xFFE5519B)),
      'audio' => (Icons.headphones_rounded, const Color(0xFFE88A20)),
      'archive' => (Icons.folder_zip_rounded, const Color(0xFF8C60D9)),
      'program' => (Icons.apps_rounded, const Color(0xFF1FA97A)),
      'document' => (Icons.description_rounded, const Color(0xFF3D7BFF)),
      'image' => (Icons.image_rounded, const Color(0xFF15A0C4)),
      _ => (Icons.insert_drive_file_outlined, cs.onSurfaceVariant),
    };
    return Container(
      width: 38,
      height: 38,
      decoration: BoxDecoration(
        color: color.withOpacity(0.13),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Icon(icon, size: 19, color: color),
    );
  }

  Widget _stateChip(BuildContext context, Color color, S s) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 3.5),
      decoration: BoxDecoration(
        color: color.withOpacity(0.13),
        borderRadius: BorderRadius.circular(99),
      ),
      child: Text(
        s.stateLabel(task.state),
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w800, color: color),
      ),
    );
  }

  Widget _actions(BuildContext context, WidgetRef ref, prov.TasksNotifier tasks, S s) {
    final compact = const BoxConstraints(minWidth: 30, minHeight: 30);
    switch (task.state) {
      case 'downloading' || 'analyzing' || 'retrying' || 'verifying' || 'pausing':
        return IconButton(
          visualDensity: VisualDensity.compact, constraints: compact,
          tooltip: s.pause,
          onPressed: () => tasks.pause(task.taskKey),
          icon: const Icon(Icons.pause_rounded, size: 18),
        );
      case 'paused' || 'interrupted' || 'queued' || 'created':
        return IconButton(
          visualDensity: VisualDensity.compact, constraints: compact,
          tooltip: s.resume,
          onPressed: () => tasks.resume(task.taskKey),
          icon: const Icon(Icons.play_arrow_rounded, size: 20),
        );
      case 'failed':
        return IconButton(
          visualDensity: VisualDensity.compact, constraints: compact,
          tooltip: s.retry,
          onPressed: () => tasks.retry(task.taskKey),
          icon: const Icon(Icons.refresh_rounded, size: 18),
        );
      case 'completed':
        return IconButton(
          visualDensity: VisualDensity.compact, constraints: compact,
          tooltip: s.openFolder,
          onPressed: onOpen,
          icon: const Icon(Icons.folder_open_rounded, size: 18),
        );
      default:
        return const SizedBox(width: 8);
    }
  }

  void _menu(BuildContext context, WidgetRef ref, Offset pos, TaskUi task) {
    final s = S.of(context);
    final tasks = ref.read(prov.tasksProvider.notifier);
    final engine = ref.read(prov.engineProvider);
    showMenu(
      context: context,
      position: RelativeRect.fromLTRB(pos.dx, pos.dy, pos.dx + 1, pos.dy + 1),
      items: <PopupMenuEntry>[
        if (task.state == 'completed') ...[
          PopupMenuItem(
            child: Text(s.openFile),
            onTap: () => engine?.call('open_file', {'task_key': task.taskKey}),
          ),
          PopupMenuItem(
            child: Text(s.openFolder),
            onTap: () => engine?.call('open_folder', {'task_key': task.taskKey}),
          ),
        ],
        if (task.isRunning)
          PopupMenuItem(child: Text(s.pause), onTap: () => tasks.pause(task.taskKey)),
        if (task.state == 'paused' || task.state == 'interrupted')
          PopupMenuItem(child: Text(s.resume), onTap: () => tasks.resume(task.taskKey)),
        if (task.state == 'failed')
          PopupMenuItem(child: Text(s.retry), onTap: () => tasks.retry(task.taskKey)),
        if (!task.isTerminalSafe)
          PopupMenuItem(child: Text(s.cancel), onTap: () => tasks.cancel(task.taskKey)),
        PopupMenuItem(
          child: Text(s.copyLink),
          onTap: () => Clipboard.setData(ClipboardData(text: task.url)),
        ),
        const PopupMenuDivider(),
        PopupMenuItem(
          child: Text(s.remove, style: const TextStyle(color: NovaColors.danger)),
          onTap: () async {
            await Future<void>.delayed(Duration.zero);
            if (context.mounted) _confirmRemove(context, tasks, task);
          },
        ),
      ],
    );
  }

  Future<void> _confirmRemove(BuildContext context, prov.TasksNotifier tasks, TaskUi task) async {
    final s = S.of(context);
    final alsoDelete = task.state == 'completed';
    final result = await showDialog<(bool, bool)>(
      context: context,
      builder: (context) => _RemoveDialog(s: s, alsoDelete: alsoDelete),
    );
    if (result != null) {
      await tasks.remove(task.taskKey, deleteFiles: result.$2);
    }
  }
}

extension on TaskUi {
  bool get isTerminalSafe => state == 'completed' || state == 'failed' || state == 'cancelled';
}

class _RemoveDialog extends StatefulWidget {
  final S s;
  final bool alsoDelete;
  const _RemoveDialog({required this.s, required this.alsoDelete});

  @override
  State<_RemoveDialog> createState() => _RemoveDialogState();
}

class _RemoveDialogState extends State<_RemoveDialog> {
  bool _deleteFile = false;
  @override
  Widget build(BuildContext context) {
    final s = widget.s;
    return AlertDialog(
      title: Text(s.remove),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(s.confirm, style: Theme.of(context).textTheme.bodyMedium),
          if (widget.alsoDelete) ...[
            const SizedBox(height: 12),
            CheckboxListTile(
              value: _deleteFile,
              onChanged: (v) => setState(() => _deleteFile = v ?? false),
              title: Text(s.deleteAlsoFile, style: const TextStyle(fontSize: 13.5)),
              contentPadding: EdgeInsets.zero,
              dense: true,
              controlAffinity: ListTileControlAffinity.leading,
            ),
          ],
        ],
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: Text(s.close)),
        FilledButton(
          style: FilledButton.styleFrom(backgroundColor: NovaColors.danger),
          onPressed: () => Navigator.pop(context, (true, _deleteFile)),
          child: Text(s.remove),
        ),
      ],
    );
  }
}

/// Add-download dialog (Ctrl+N / toolbar) — IDM parity edition:
/// multi-line batch, [01-99] pattern preview, 32-connection chips,
/// download-later start time and mirror URLs.
Future<void> showAddDownloadDialog(BuildContext context, {String? prefillUrl}) async {
  final s = S.of(context);
  final urlCtrl = TextEditingController(text: prefillUrl ?? '');
  final fileCtrl = TextEditingController();
  final mirrorsCtrl = TextEditingController();
  final startAtCtrl = TextEditingController();
  var connections = 4;
  var downloadLater = false;
  List<String>? previewUrls;

  await showDialog(
    context: context,
    builder: (context) => StatefulBuilder(
      builder: (context, setState) => AlertDialog(
        title: Text(s.addDownload),
        content: SizedBox(
          width: 560,
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                TextField(
                  controller: urlCtrl,
                  autofocus: true,
                  maxLines: 4,
                  minLines: 1,
                  decoration: InputDecoration(
                    labelText: s.urlLabel,
                    hintText: s.urlHint,
                    helperText: s.batchHint,
                  ),
                  onChanged: (v) => setState(() => previewUrls = null),
                ),
                if (urlCtrl.text.contains('[') && previewUrls == null)
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: Align(
                      alignment: AlignmentDirectional.centerEnd,
                      child: TextButton.icon(
                        icon: const Icon(Icons.expand_circle_down_outlined, size: 16),
                        label: Text('${s.preview} ${s.urls}'),
                        onPressed: () async {
                          final urls = await ProviderScope.containerOf(context, listen: false)
                              .read(prov.tasksProvider.notifier)
                              .expandPattern(urlCtrl.text.trim());
                          if (context.mounted) setState(() => previewUrls = urls.take(20).toList());
                        },
                      ),
                    ),
                  ),
                if (previewUrls != null)
                  Container(
                    height: 120,
                    margin: const EdgeInsets.only(top: 8),
                    decoration: BoxDecoration(
                      color: Theme.of(context).colorScheme.surfaceContainerHighest.withValues(alpha: .4),
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: ListView.builder(
                      itemCount: previewUrls!.length,
                      itemBuilder: (_, i) => Padding(
                        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 2),
                        child: Text(previewUrls![i], style: const TextStyle(fontSize: 12, fontFamily: 'monospace')),
                      ),
                    ),
                  ),
                const SizedBox(height: 12),
                TextField(
                  controller: fileCtrl,
                  decoration: InputDecoration(labelText: '${s.fileName} (${s.none})'),
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Text(s.connections, style: Theme.of(context).textTheme.labelLarge),
                    const Spacer(),
                    for (final c in [1, 2, 4, 8, 16, 24, 32])
                      Padding(
                        padding: const EdgeInsets.only(left: 4, right: 4),
                        child: ChoiceChip(
                          label: Text('$c'),
                          selected: connections == c,
                          onSelected: (_) => setState(() => connections = c),
                          visualDensity: VisualDensity.compact,
                        ),
                      ),
                  ],
                ),
                const SizedBox(height: 12),
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  dense: true,
                  title: Text(s.downloadLater),
                  value: downloadLater,
                  onChanged: (v) => setState(() => downloadLater = v),
                ),
                if (downloadLater)
                  TextField(
                    controller: startAtCtrl,
                    decoration: InputDecoration(
                      labelText: s.startAt,
                      hintText: '2026-01-01 22:30',
                    ),
                  ),
                const SizedBox(height: 8),
                TextField(
                  controller: mirrorsCtrl,
                  maxLines: 3,
                  minLines: 1,
                  decoration: InputDecoration(labelText: s.mirrors, helperText: s.mirrorsHint),
                ),
              ],
            ),
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: Text(s.cancel)),
          FilledButton.icon(
            icon: const Icon(Icons.download_rounded, size: 18),
            onPressed: () => Navigator.pop(context, true),
            label: Text(s.download),
          ),
        ],
      ),
    ),
  ).then((ok) async {
    if (ok == true && context.mounted) {
      final notifier = ProviderScope.containerOf(context, listen: false)
          .read(prov.tasksProvider.notifier);
      var urls = urlCtrl.text
          .split(RegExp(r'[\r\n]+'))
          .map((u) => u.trim())
          .where((u) => u.isNotEmpty)
          .toList();
      // allow space-separated single-line pastes too
      if (urls.length == 1 && urls.first.contains(' ')) {
        urls = urls.first.split(RegExp(r'\s+')).where((u) => u.isNotEmpty).toList();
      }
      final valid = urls
          .where((u) => u.startsWith('http://') || u.startsWith('https://') || u.startsWith('ftp://') || u.startsWith('ftps://'))
          .toList();
      if (valid.isEmpty) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(S.of(context).invalidUrl)));
        return;
      }

      int? startAtMs;
      if (downloadLater && startAtCtrl.text.trim().isNotEmpty) {
        final dt = DateTime.tryParse(startAtCtrl.text.trim());
        if (dt != null) startAtMs = dt.millisecondsSinceEpoch;
      }
      final mirrors = mirrorsCtrl.text
          .split(RegExp(r'[\r\n]+'))
          .map((m) => m.trim())
          .where((m) => m.isNotEmpty)
          .toList();

      if (valid.length == 1) {
        await notifier.addDownload(
          url: valid.first,
          fileName: fileCtrl.text.trim().isEmpty ? null : fileCtrl.text.trim(),
          maxConnections: connections,
          mirrors: mirrors,
          startAtMs: startAtMs,
        );
      } else {
        await notifier.addBatch(valid,
            fileName: fileCtrl.text.trim().isEmpty ? null : fileCtrl.text.trim(),
            maxConnections: connections);
      }
      if (context.mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(S.of(context).added)));
      }
    }
    urlCtrl.dispose();
    fileCtrl.dispose();
    mirrorsCtrl.dispose();
    startAtCtrl.dispose();
  });
}

/// Full task details sheet.
void showTaskDetails(BuildContext context, TaskUi task) {
  final s = S.of(context);
  showModalBottomSheet(
    context: context,
    showDragHandle: true,
    isScrollControlled: true,
    constraints: const BoxConstraints(maxWidth: 640),
    builder: (context) => DraggableScrollableSheet(
      expand: false,
      maxChildSize: 0.85,
      initialChildSize: 0.6,
      builder: (context, controller) => ListView(
        controller: controller,
        padding: const EdgeInsets.fromLTRB(24, 4, 24, 24),
        children: [
          Row(
            children: [
              Expanded(
                child: Text(task.fileName.isEmpty ? task.url : task.fileName,
                    style: Theme.of(context).textTheme.titleLarge!.copyWith(fontWeight: FontWeight.w800)),
              ),
              _StateBadge(s: s, task: task),
            ],
          ),
          const SizedBox(height: 18),
          Wrap(
            spacing: 14,
            runSpacing: 14,
            children: [
              _kv(context, s.totalSize, Fmt.bytes(context, task.totalBytes >= 0 ? task.totalBytes : null)),
              _kv(context, s.received, Fmt.bytes(context, task.receivedBytes)),
              _kv(context, s.speed, task.speedBps > 0 ? Fmt.speed(context, task.speedBps) : s.none),
              _kv(context, s.segments, task.segmentsTotal > 0 ? '${task.segmentsDone}/${task.segmentsTotal}' : '${task.segmentCount}'),
              _kv(context, s.queue, task.queueName),
              _kv(context, s.category, task.category),
            ],
          ),
          const SizedBox(height: 18),
          if (task.url.isNotEmpty)
            SelectableText(task.url, style: TextStyle(fontSize: 12.5, color: Theme.of(context).colorScheme.onSurfaceVariant)),
          const SizedBox(height: 10),
          if (task.destinationDir.isNotEmpty)
            SelectableText(task.destinationDir,
                style: TextStyle(fontSize: 12.5, color: Theme.of(context).colorScheme.onSurfaceVariant)),
          if (task.errorMessage != null) ...[
            const SizedBox(height: 16),
            Container(
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(
                color: NovaColors.danger.withOpacity(0.09),
                borderRadius: BorderRadius.circular(10),
              ),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Icon(Icons.error_outline_rounded, color: NovaColors.danger, size: 18),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(s.errorCategory(task.errorCategory ?? 'unknown'),
                            style: const TextStyle(fontWeight: FontWeight.w800, color: NovaColors.danger, fontSize: 12.5)),
                        const SizedBox(height: 4),
                        Text(task.errorMessage ?? '', style: const TextStyle(fontSize: 12.5)),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ],
        ],
      ),
    ),
  );
}

class _StateBadge extends StatelessWidget {
  final S s;
  final TaskUi task;
  const _StateBadge({required this.s, required this.task});

  @override
  Widget build(BuildContext context) {
    final color = NovaColors.stateColor(task.state, Theme.of(context).brightness);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      decoration: BoxDecoration(color: color.withOpacity(0.13), borderRadius: BorderRadius.circular(99)),
      child: Text(s.stateLabel(task.state), style: TextStyle(color: color, fontWeight: FontWeight.w800, fontSize: 12)),
    );
  }
}

Widget _kv(BuildContext context, String k, String v) {
  return Container(
    width: 176,
    padding: const EdgeInsets.all(10),
    decoration: BoxDecoration(
      color: Theme.of(context).colorScheme.surfaceContainerHighest.withOpacity(0.4),
      borderRadius: BorderRadius.circular(8),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(k, style: TextStyle(fontSize: 11, color: Theme.of(context).colorScheme.onSurfaceVariant)),
        const SizedBox(height: 3),
        Text(v, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13), maxLines: 1, overflow: TextOverflow.ellipsis),
      ],
    ),
  );
}
