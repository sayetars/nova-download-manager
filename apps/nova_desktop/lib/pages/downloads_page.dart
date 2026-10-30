import 'dart:async';
import 'dart:io' as io;

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/drag_out.dart';
import '../core/models.dart';
import '../core/strings.dart';
import '../core/theme.dart';
import '../core/torrent_meta.dart';
import '../providers/engine.dart' as prov;
import 'preview_page.dart';
import 'task_properties.dart';

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
        final sortMode = ref.watch(prov.sortModeProvider);
        list = sortMode.apply(list);
        if (list.isEmpty) return _empty(context, s);
        // v1.8.0 (IDM "choose columns" parity): optional info fields in the tiles.
        final tileFields = (ref.watch(prov.settingsProvider)['ui.tile_fields'] ?? '')
            .split(',')
            .map((f) => f.trim())
            .where((f) => f.isNotEmpty)
            .toSet();
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
                  _fieldsButton(context, ref, s, tileFields),
                  _sortButton(context, ref, s, sortMode),
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
                    tileFields: tileFields,
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

  /// v1.8.0 (IDM "choose the columns to appear on the main window" parity):
  /// pick which extra info fields the tiles show (queue / category / date).
  Widget _fieldsButton(BuildContext context, WidgetRef ref, S s, Set<String> fields) {
    final engine = ref.read(prov.engineProvider);
    Future<void> toggle(String f) async {
      final next = fields.contains(f) ? fields.difference({f}) : fields.union({f});
      await engine?.call('set_settings', {
        'values': {'ui.tile_fields': next.join(',')}
      });
    }

    return PopupMenuButton<String>(
      tooltip: s.chooseFields,
      icon: const Icon(Icons.view_column_outlined, size: 19),
      itemBuilder: (_) => [
        CheckedPopupMenuItem(
          value: 'queue',
          checked: fields.contains('queue'),
          child: Text(s.fieldQueue),
        ),
        CheckedPopupMenuItem(
          value: 'category',
          checked: fields.contains('category'),
          child: Text(s.fieldCategory),
        ),
        CheckedPopupMenuItem(
          value: 'date',
          checked: fields.contains('date'),
          child: Text(s.fieldDate),
        ),
      ],
      onSelected: (f) => toggle(f),
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

  /// IDM parity v1.2.0 — sortable columns as a sort menu (field + direction).
  Widget _sortButton(BuildContext context, WidgetRef ref, S s, prov.SortMode sortMode) {
    final cs = Theme.of(context).colorScheme;
    final labels = <prov.SortField, String>{
      prov.SortField.added: s.sortAdded,
      prov.SortField.name: s.sortName,
      prov.SortField.size: s.sortSize,
      prov.SortField.progress: s.sortProgress,
      prov.SortField.state: s.sortState,
      prov.SortField.speed: s.sortSpeed,
    };
    return PopupMenuButton<prov.SortField>(
      tooltip: s.sortBy,
      icon: Icon(sortMode.field == prov.SortField.added ? Icons.sort_rounded : Icons.sort,
          size: 19, color: sortMode.field == prov.SortField.added ? cs.onSurfaceVariant : cs.primary),
      onSelected: (f) =>
          ref.read(prov.sortModeProvider.notifier).toggle(f),
      itemBuilder: (context) => [
        for (final f in prov.SortField.values)
          PopupMenuItem(
            value: f,
            child: Row(
              children: [
                if (sortMode.field == f)
                  Icon(sortMode.ascending ? Icons.arrow_upward_rounded : Icons.arrow_downward_rounded,
                      size: 16, color: cs.primary)
                else
                  const SizedBox(width: 16),
                const SizedBox(width: 8),
                Text(labels[f] ?? ''),
              ],
            ),
          ),
      ],
    );
  }

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
  /// v1.8.0: extra visible info fields (queue/category/date) — the card-UI
  /// equivalent of IDM's column chooser.
  final Set<String> tileFields;
  const TaskTile({super.key, required this.task, this.onOpen, this.tileFields = const {}});

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
              _draggableIcon(context),
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
                        // v1.8.0 (IDM column-chooser parity): optional fields
                        if (tileFields.contains('queue')) ...[
                          const SizedBox(width: 10),
                          Icon(Icons.schedule_rounded, size: 12, color: cs.onSurfaceVariant),
                          const SizedBox(width: 3),
                          Text(task.queueName,
                              style: TextStyle(fontSize: 11.5, color: cs.onSurfaceVariant)),
                        ],
                        if (tileFields.contains('category')) ...[
                          const SizedBox(width: 10),
                          Text(task.category,
                              style: TextStyle(fontSize: 11.5, color: cs.onSurfaceVariant)),
                        ],
                        if (tileFields.contains('date') && task.createdAtMs > 0) ...[
                          const SizedBox(width: 10),
                          Text(Fmt.date(context, task.createdAtMs),
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

  /// v1.8.0 (IDM "drag & drop downloaded files out of IDM" parity): on a
  /// completed task, press the category icon and move the pointer to start a
  /// NATIVE drag with the file (Windows runner; no-op elsewhere).
  Widget _draggableIcon(BuildContext context) {
    final icon = _categoryIcon(context);
    if (task.state != 'completed') return icon;
    final path = task.destinationDir.isEmpty
        ? null
        : '${task.destinationDir}${io.Platform.pathSeparator}${task.fileName}';
    if (path == null || task.fileName.isEmpty) return icon;
    Offset? downAt;
    return Listener(
      onPointerDown: (e) => downAt = e.position,
      onPointerMove: (e) {
        final start = downAt;
        if (start == null) return;
        if ((e.position - start).distance > 6) {
          downAt = null; // fire once per gesture
          NovaDragOut.start([path]);
        }
      },
      onPointerUp: (_) => downAt = null,
      child: MouseRegion(cursor: SystemMouseCursors.grab, child: icon),
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
        // v1.5.0 (G17): partial preview for media tasks (growing .novadl file)
        if (task.isPreviewable &&
            (task.state == 'downloading' ||
                task.state == 'paused' ||
                task.state == 'completed' ||
                task.state == 'interrupted'))
          PopupMenuItem(
            child: Text(s.previewTitle),
            onTap: () {
              Future<void>.delayed(Duration.zero)
                  .then((_) => showPreviewDialog(context, task));
            },
          ),
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
        if (!task.isRunning && !task.isTerminalSafe)
          PopupMenuItem(
            child: Text(s.editTask),
            onTap: () {
              Future<void>.delayed(Duration.zero)
                  .then((_) => showTaskProperties(context, task));
            },
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
Future<void> showAddDownloadDialog(BuildContext context,
    {String? prefillUrl, List<String>? prefillUrls}) async {
  final s = S.of(context);
  final initialText = (prefillUrls != null && prefillUrls.isNotEmpty)
      ? prefillUrls.join('\n')
      : (prefillUrl ?? '');
  final urlCtrl = TextEditingController(text: initialText);
  final fileCtrl = TextEditingController();
  final dirCtrl = TextEditingController(); // v1.8.0: Save As (IDM Download File Info parity)
  final mirrorsCtrl = TextEditingController();
  final startAtCtrl = TextEditingController();
  final cookiesCtrl = TextEditingController();
  final headersCtrl = TextEditingController();
  final hashCtrl = TextEditingController();
  var connections = 4;
  var downloadLater = false;
  var sequential = false;
  var seedAfter = false;
  var superSeed = false;
  List<String>? previewUrls;
  // v1.5.0 (G28): yt-dlp analysis → quality panel
  Map<String, dynamic>? ytdlpInfo;
  String? ytdlpFormatId;
  bool ytdlpBusy = false;
  String? ytdlpError;
  // v1.6.0: merge-needed formats now download via yt-dlp native mode (engine merges)
  bool ytdlpNativeMode = false;
  // v1.4.1: local .torrent file → per-file selection checkboxes
  TorrentPreview? torrentPreview;
  Set<int> deselectedFiles = {};   // indices the user unticked

  void refreshPreview(String text) {
    final t = text.trim();
    torrentPreview = (t.endsWith('.torrent') && io.File(t).existsSync())
        ? tryParseTorrent(t)
        : null;
    deselectedFiles = {};
    superSeed = false;
    seedAfter = false;
  }

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
                  onChanged: (v) => setState(() {
                        previewUrls = null;
                        refreshPreview(v);   // v1.4.1: .torrent file listing
                      }),
                ),
                if (urlCtrl.text.trim().startsWith('magnet:'))
                  Padding(
                    padding: const EdgeInsets.only(top: 6),
                    child: Row(
                      children: [
                        Icon(Icons.hub_outlined,
                            size: 16, color: Theme.of(context).colorScheme.primary),
                        const SizedBox(width: 6),
                        Expanded(
                            child: Text(s.magnetDetected,
                                style: Theme.of(context).textTheme.bodySmall)),
                      ],
                    ),
                  ),
                // v1.5.0 (G28): yt-dlp analyze + quality panel for page URLs
                if (RegExp(r'^https?://\S+$').hasMatch(urlCtrl.text.trim()) &&
                    torrentPreview == null) ...[
                  const SizedBox(height: 6),
                  Align(
                    alignment: AlignmentDirectional.centerEnd,
                    child: TextButton.icon(
                      icon: const Icon(Icons.query_stats_rounded, size: 16),
                      label: Text(ytdlpBusy ? s.ytdlpAnalyzing : s.ytdlpAnalyze),
                      onPressed: ytdlpBusy
                          ? null
                          : () async {
                              final container = ProviderScope.containerOf(context,
                                  listen: false);
                              setState(() {
                                ytdlpBusy = true;
                                ytdlpError = null;
                                ytdlpInfo = null;
                                ytdlpFormatId = null;
                              });
                              try {
                                final info = await container
                                    .read(prov.tasksProvider.notifier)
                                    .ytdlpAnalyze(urlCtrl.text.trim());
                                if (!context.mounted) return;
                                setState(() {
                                  ytdlpInfo = info;
                                  // preselect the best direct format
                                  final formats = ((info?['formats'] as List?) ?? [])
                                      .map((e) => Map<String, dynamic>.from(e as Map))
                                      .where((f) => f['direct'] == true)
                                      .toList();
                                  ytdlpFormatId = formats.isNotEmpty
                                      ? formats.first['format_id'] as String?
                                      : null;
                                });
                              } catch (e) {
                                if (!context.mounted) return;
                                setState(() => ytdlpError = e.toString());
                              } finally {
                                if (context.mounted) setState(() => ytdlpBusy = false);
                              }
                            },
                    ),
                  ),
                  if (ytdlpError != null)
                    Padding(
                      padding: const EdgeInsets.only(top: 4),
                      child: Text(
                        ytdlpError!.contains('ytdlp_missing')
                            ? s.ytdlpNoBinary
                            : ytdlpError!.contains('live_stream')
                                ? s.ytdlpLive
                                : ytdlpError!.contains('drm_protected')
                                    ? s.ytdlpDrm
                                    : s.ytdlpFailed,
                        style: TextStyle(
                            fontSize: 12,
                            color: Theme.of(context).colorScheme.error),
                      ),
                    ),
                  if (ytdlpInfo != null) ...[
                    const SizedBox(height: 6),
                    Text('${s.ytdlpPick} — ${ytdlpInfo!['title'] ?? ''}',
                        style: Theme.of(context).textTheme.titleSmall,
                        overflow: TextOverflow.ellipsis),
                    ConstrainedBox(
                      constraints: const BoxConstraints(maxHeight: 190),
                      child: ListView(
                        shrinkWrap: true,
                        children: [
                          for (final f in ((ytdlpInfo!['formats'] as List?) ?? [])
                              .map((e) => Map<String, dynamic>.from(e as Map)))
                            RadioListTile<String>(
                              dense: true,
                              contentPadding: EdgeInsets.zero,
                              value: f['format_id'] as String? ?? '',
                              groupValue: ytdlpFormatId,
                              // v1.6.0: ALL formats selectable — merge-only ones go native
                              onChanged: (v) => setState(() {
                                ytdlpFormatId = v;
                                ytdlpNativeMode = f['direct'] != true;
                              }),
                              title: Text(
                                [
                                  f['resolution'] ?? '',
                                  f['ext'] ?? '',
                                  if (f['filesize'] != null && (f['filesize'] as int) > 0)
                                    '${((f['filesize'] as int) / 1048576).toStringAsFixed(1)} MB',
                                  if (f['note'] != null) f['note'].toString(),
                                ]
                                    .where((e) => e.toString().isNotEmpty)
                                    .join(' · '),
                                style: const TextStyle(fontSize: 13),
                              ),
                              subtitle: Text(
                                '${f['format_id']} · ${f['protocol'] ?? ''}'
                                '${f['direct'] == true ? '' : '  (${s.ytdlpNeedsMerge})'}',
                                style: const TextStyle(fontSize: 11, fontFamily: 'monospace'),
                              ),
                            ),
                        ],
                      ),
                    ),
                    // v1.6.0: native-merge hint when a merge-only format is selected
                    if (ytdlpNativeMode)
                      Padding(
                        padding: const EdgeInsets.only(top: 2),
                        child: Row(
                          children: [
                            Icon(Icons.merge_type_rounded,
                                size: 14,
                                color: Theme.of(context).colorScheme.primary),
                            const SizedBox(width: 6),
                            Expanded(
                              child: Text(s.ytdlpNativeBadge,
                                  style: TextStyle(
                                      fontSize: 12,
                                      color: Theme.of(context).colorScheme.primary)),
                            ),
                          ],
                        ),
                      ),
                  ],
                ],
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
                // v1.8.0 (IDM "Download File Info" parity): Save As directory — empty
                // keeps the category default; a custom dir becomes the category's new
                // default when "change folder on last selected" is on (engine-side).
                TextField(
                  controller: dirCtrl,
                  decoration: InputDecoration(
                    labelText: s.saveTo,
                    prefixIcon: const Icon(Icons.folder_outlined, size: 18),
                  ),
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
                  decoration: InputDecoration(labelText: s.mirrors, helperText: s.mirrorsOnePerLine),
                ),
                const SizedBox(height: 8),
                ExpansionTile(
                  tilePadding: EdgeInsets.zero,
                  childrenPadding: const EdgeInsets.only(bottom: 8),
                  title: Text(s.advanced, style: Theme.of(context).textTheme.labelLarge),
                  children: [
                    TextField(
                      controller: cookiesCtrl,
                      decoration: InputDecoration(labelText: s.cookies, helperText: s.cookiesHint),
                    ),
                    const SizedBox(height: 10),
                    TextField(
                      controller: headersCtrl,
                      decoration: InputDecoration(
                          labelText: s.extraHeaders, helperText: s.extraHeadersHint),
                    ),
                    const SizedBox(height: 10),
                    TextField(
                      controller: hashCtrl,
                      decoration: InputDecoration(
                          labelText: s.expectedHash, helperText: s.expectedHashHint),
                    ),
                    const SizedBox(height: 4),
                    SwitchListTile(
                      contentPadding: EdgeInsets.zero,
                      dense: true,
                      title: Text(s.sequentialDownload),
                      subtitle: Text(s.sequentialHint, style: Theme.of(context).textTheme.bodySmall),
                      value: sequential,
                      onChanged: (v) => setState(() => sequential = v),
                    ),
                    if (urlCtrl.text.trim().startsWith('magnet:') ||
                        (urlCtrl.text.trim().endsWith('.torrent') && io.File(urlCtrl.text.trim()).existsSync()))
                      SwitchListTile(
                        contentPadding: EdgeInsets.zero,
                        dense: true,
                        title: Text(s.seedAfterTorrent),
                        subtitle: Text(s.seedAfterTorrentHint,
                            style: Theme.of(context).textTheme.bodySmall),
                        value: seedAfter,
                        onChanged: (v) => setState(() => seedAfter = v),
                      ),
                    if (seedAfter)
                      SwitchListTile(
                        contentPadding: EdgeInsets.zero,
                        dense: true,
                        title: Text(s.superSeedTorrent),
                        subtitle: Text(s.superSeedTorrentHint,
                            style: Theme.of(context).textTheme.bodySmall),
                        value: superSeed,
                        onChanged: (v) => setState(() => superSeed = v),
                      ),
                    // v1.4.1: multi-file selection for a local .torrent
                    if (torrentPreview != null && !torrentPreview!.isSingleFile) ...[
                      const SizedBox(height: 8),
                      Text(s.torrentFiles, style: Theme.of(context).textTheme.titleSmall),
                      Text(s.torrentFilesHint, style: Theme.of(context).textTheme.bodySmall),
                      const SizedBox(height: 4),
                      ConstrainedBox(
                        constraints: const BoxConstraints(maxHeight: 180),
                        child: SingleChildScrollView(
                          child: Column(
                            children: [
                              for (var i = 0; i < torrentPreview!.files.length; i++)
                                CheckboxListTile(
                                  dense: true,
                                  contentPadding: EdgeInsets.zero,
                                  controlAffinity: ListTileControlAffinity.leading,
                                  title: Text(torrentPreview!.files[i].subPath,
                                      overflow: TextOverflow.ellipsis),
                                  subtitle: Text(
                                      '${(torrentPreview!.files[i].length / 1048576).toStringAsFixed(2)} MB',
                                      style: Theme.of(context).textTheme.bodySmall),
                                  value: !deselectedFiles.contains(i),
                                  onChanged: (v) => setState(() {
                                        if (v == true) {
                                          deselectedFiles.remove(i);
                                        } else {
                                          deselectedFiles.add(i);
                                        }
                                      })),
                            ],
                          ),
                        ),
                      ),
                      if (deselectedFiles.isEmpty)
                        Text(s.torrentFilesAll, style: Theme.of(context).textTheme.bodySmall),
                    ],
                  ],
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
    try {
      await _handleAddOk(ok, context, urlCtrl, fileCtrl, dirCtrl, mirrorsCtrl, startAtCtrl,
          cookiesCtrl, headersCtrl, hashCtrl, connections, downloadLater,
          sequential, seedAfter, superSeed, torrentPreview, deselectedFiles,
          ytdlpInfo, ytdlpFormatId, ytdlpNativeMode);
    } finally {
      urlCtrl.dispose();
      fileCtrl.dispose();
      dirCtrl.dispose();
      mirrorsCtrl.dispose();
      startAtCtrl.dispose();
      cookiesCtrl.dispose();
      headersCtrl.dispose();
      hashCtrl.dispose();
    }
  });
}

/// Extracts the extension of the picked yt-dlp format (title-based naming).
String? _extOf(Map<String, dynamic>? info, String? formatId) {
  if (info == null || formatId == null) return null;
  for (final f in (info['formats'] as List? ?? [])) {
    if ((f as Map)['format_id'] == formatId) return f['ext'] as String?;
  }
  return null;
}

Future<void> _handleAddOk(
    bool ok,
    BuildContext context,
    TextEditingController urlCtrl,
    TextEditingController fileCtrl,
    TextEditingController dirCtrl, // v1.8.0: Save As directory
    TextEditingController mirrorsCtrl,
    TextEditingController startAtCtrl,
    TextEditingController cookiesCtrl,
    TextEditingController headersCtrl,
    TextEditingController hashCtrl,
    int connections,
    bool downloadLater,
    bool sequential,
    bool seedAfter,
    bool superSeed,
    TorrentPreview? torrentPreview,
    Set<int> deselectedFiles,
    Map<String, dynamic>? ytdlpInfo,
    String? ytdlpFormatId,
    bool ytdlpNativeMode) async {   // v1.6.0: merge-only → yt-dlp native download
    if (ok == true && context.mounted) {
      final saveDir = dirCtrl.text.trim();
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

      // v1.5.0 (G28): yt-dlp quality was picked → resolve through the bridge
      if (ytdlpFormatId != null && urls.length == 1) {
        try {
          await notifier.ytdlpDownload(
            url: urls.first,
            formatId: ytdlpFormatId!,
            fileName: fileCtrl.text.trim().isEmpty ? null : fileCtrl.text.trim(),
            destinationDir: saveDir.isEmpty ? null : saveDir,
            native: ytdlpNativeMode,   // v1.6.0: merge-only formats → yt-dlp downloads
            title: ytdlpInfo?['title'] as String?,
            ext: _extOf(ytdlpInfo, ytdlpFormatId),
          );
          if (context.mounted) {
            ScaffoldMessenger.of(context)
                .showSnackBar(SnackBar(content: Text(S.of(context).added)));
          }
        } catch (e) {
          if (context.mounted) {
            ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(
                '${S.of(context).ytdlpSection}: $e')));
          }
        }
        return;
      }

      final valid = urls
          .where((u) =>
              u.startsWith('http://') ||
              u.startsWith('https://') ||
              u.startsWith('ftp://') ||
              u.startsWith('ftps://') ||
              u.startsWith('sftp://') ||
              u.startsWith('dav://') ||
              u.startsWith('davs://') ||
              u.startsWith('magnet:') ||
              (u.endsWith('.torrent') && io.File(u).existsSync()))
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
          destinationDir: saveDir.isEmpty ? null : saveDir,
          maxConnections: connections,
          mirrors: mirrors,
          startAtMs: startAtMs,
          cookies: cookiesCtrl.text.trim(),
          extraHeaders: headersCtrl.text.trim(),
          expectedHash: hashCtrl.text.trim(),
          sequential: sequential,
          seedAfter: seedAfter &&
              (valid.first.startsWith('magnet:') || valid.first.endsWith('.torrent')),
          superSeed: superSeed,
          selectedFiles: (torrentPreview != null &&
                  !torrentPreview!.isSingleFile &&
                  deselectedFiles.isNotEmpty &&
                  deselectedFiles.length < torrentPreview!.files.length)
              ? [for (var i = 0; i < torrentPreview!.files.length; i++)
                  if (!deselectedFiles.contains(i)) i.toString()]
                  .join(',')
              : null,
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
}

/// Full task details sheet with live per-segment bars + speed graph (IDM parity).
void showTaskDetails(BuildContext context, TaskUi task) {
  showModalBottomSheet(
    context: context,
    showDragHandle: true,
    isScrollControlled: true,
    constraints: const BoxConstraints(maxWidth: 640),
    builder: (context) => _TaskDetailsSheet(taskKey: task.taskKey, task: task),
  );
}

class _TaskDetailsSheet extends ConsumerStatefulWidget {
  final String taskKey;
  final TaskUi task;
  const _TaskDetailsSheet({required this.taskKey, required this.task});

  @override
  ConsumerState<_TaskDetailsSheet> createState() => _TaskDetailsSheetState();
}

class _TaskDetailsSheetState extends ConsumerState<_TaskDetailsSheet> {
  List<Map<String, dynamic>> _segments = [];
  final List<int> _speedHistory = [];
  Timer? _timer;

  @override
  void initState() {
    super.initState();
    _tick();
    _timer = Timer.periodic(const Duration(milliseconds: 700), (_) => _tick());
  }

  Future<void> _tick() async {
    final engine = ref.read(prov.engineProvider);
    if (engine == null) return;
    try {
      final resp = await engine.call('task_segments', {'task_key': widget.taskKey});
      if (!mounted) return;
      setState(() {
        _segments = (resp['segments'] as List? ?? [])
            .map((e) => Map<String, dynamic>.from(e as Map))
            .toList();
      });
    } catch (_) {/* engine offline */}
    final tasks = ref.read(prov.tasksProvider).value ?? <TaskUi>[];
    final t = tasks.where((x) => x.taskKey == widget.taskKey).firstOrNull;
    if (t != null && mounted) {
      setState(() {
        _speedHistory.add(t.speedBps);
        if (_speedHistory.length > 90) _speedHistory.removeAt(0);
      });
    }
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = S.of(context);
    final tasks = ref.watch(prov.tasksProvider).value ?? <TaskUi>[];
    final task = tasks.where((t) => t.taskKey == widget.taskKey).firstOrNull ?? widget.task;
    final cs = Theme.of(context).colorScheme;

    return DraggableScrollableSheet(
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
              if (!task.isRunning && !task.isTerminalSafe) ...[
                IconButton(
                  tooltip: S.of(context).editTask,
                  icon: const Icon(Icons.tune_rounded, size: 20),
                  onPressed: () {
                    Navigator.of(context).pop();
                    showTaskProperties(context, task);
                  },
                ),
                const SizedBox(width: 4),
              ],
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
          if (_segments.length > 1) ...[
            const SizedBox(height: 18),
            Text(s.segments, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w800)),
            const SizedBox(height: 8),
            _SegmentBars(segments: _segments, totalBytes: task.totalBytes),
          ],
          if (_speedHistory.length > 3) ...[
            const SizedBox(height: 18),
            Text(s.speedGraph, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w800)),
            const SizedBox(height: 8),
            SizedBox(
              height: 72,
              child: CustomPaint(painter: _SpeedGraphPainter(_speedHistory), size: Size.infinite),
            ),
          ],
          const SizedBox(height: 18),
          if (task.url.isNotEmpty)
            SelectableText(task.url, style: TextStyle(fontSize: 12.5, color: cs.onSurfaceVariant)),
          const SizedBox(height: 10),
          if (task.destinationDir.isNotEmpty)
            SelectableText(task.destinationDir,
                style: TextStyle(fontSize: 12.5, color: cs.onSurfaceVariant)),
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
    );
  }
}

/// Stacked per-segment progress: one horizontal strip, each slice sized by its
/// byte span and filled by its received fraction (mirrors the IDM segment view).
class _SegmentBars extends StatelessWidget {
  final List<Map<String, dynamic>> segments;
  final int totalBytes;
  const _SegmentBars({required this.segments, required this.totalBytes});

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return ClipRRect(
      borderRadius: BorderRadius.circular(6),
      child: SizedBox(
        height: 26,
        child: Row(
          children: [
            for (final seg in segments)
              Expanded(
                flex: _flexOf(seg),
                child: Container(
                  decoration: BoxDecoration(border: Border.all(color: cs.surface, width: 1)),
                  child: Stack(
                    alignment: AlignmentDirectional.centerStart,
                    children: [
                      FractionallySizedBox(
                        widthFactor: _frac(seg).clamp(0.0, 1.0),
                        child: Container(color: NovaColors.completed),
                      ),
                      Center(
                        child: Text('${seg['index']}',
                            style: TextStyle(
                                fontSize: 9.5,
                                fontWeight: FontWeight.w800,
                                color: _frac(seg) > 0.45
                                    ? Colors.white
                                    : cs.onSurfaceVariant)),
                      ),
                    ],
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }

  int _flexOf(Map<String, dynamic> seg) {
    final end = (seg['end'] as int?) ?? -1;
    final start = (seg['start'] as int?) ?? 0;
    final len = end >= start ? end - start + 1 : 1;
    return len.clamp(1, 1 << 30);
  }

  double _frac(Map<String, dynamic> seg) {
    final end = (seg['end'] as int?) ?? -1;
    final start = (seg['start'] as int?) ?? 0;
    final received = (seg['received'] as int?) ?? 0;
    if (end < start) return 0;
    final len = end - start + 1;
    return len <= 0 ? 0 : received / len;
  }
}

class _SpeedGraphPainter extends CustomPainter {
  final List<int> samples;
  _SpeedGraphPainter(this.samples);

  @override
  void paint(Canvas canvas, Size size) {
    if (samples.isEmpty) return;
    final maxV = samples.reduce((a, b) => a > b ? a : b).clamp(1, 1 << 40);
    final paint = Paint()
      ..color = NovaColors.completed.withOpacity(0.85)
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.6
      ..isAntiAlias = true;
    final fill = Paint()
      ..color = NovaColors.completed.withOpacity(0.16)
      ..style = PaintingStyle.fill;
    final path = Path();
    final fillPath = Path()..moveTo(0, size.height);
    for (var i = 0; i < samples.length; i++) {
      final x = size.width * i / (samples.length - 1);
      final y = size.height - (size.height * samples[i] / maxV);
      if (i == 0) path.moveTo(x, y);
      path.lineTo(x, y);
      fillPath.lineTo(x, y);
    }
    fillPath.lineTo(size.width, size.height);
    fillPath.close();
    canvas.drawPath(fillPath, fill);
    canvas.drawPath(path, paint);
  }

  @override
  bool shouldRepaint(_SpeedGraphPainter old) => true;
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
