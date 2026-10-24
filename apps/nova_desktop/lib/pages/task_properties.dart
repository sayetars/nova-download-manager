import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/models.dart';
import '../core/strings.dart';
import '../providers/engine.dart' as prov;

/// IDM parity — per-download properties editor: queue, category, priority,
/// connection count, speed cap, scheduled start, post-completion action and
/// mirror list. Only for non-running tasks (the engine enforces this too).
Future<void> showTaskProperties(BuildContext context, TaskUi task) =>
    showDialog<void>(
      context: context,
      builder: (context) => _TaskPropertiesDialog(task: task),
    );

class _TaskPropertiesDialog extends ConsumerStatefulWidget {
  final TaskUi task;
  const _TaskPropertiesDialog({required this.task});

  @override
  ConsumerState<_TaskPropertiesDialog> createState() =>
      _TaskPropertiesDialogState();
}

class _TaskPropertiesDialogState extends ConsumerState<_TaskPropertiesDialog> {
  late final TextEditingController _speedCap;
  late final TextEditingController _mirrors;
  late final TextEditingController _startAt;
  late String _queue;
  late String _category;
  late int _priority;
  late int _connections;
  late String _postAction;
  List<String> _queues = const ['main'];
  List<String> _categories = const [];
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    final t = widget.task;
    _speedCap = TextEditingController(text: '');
    _mirrors = TextEditingController(text: '');
    _startAt = TextEditingController(text: '');
    _queue = t.queueName;
    _category = t.category;
    _priority = t.priority.clamp(0, 2);
    _connections = t.segmentCount > 0 ? t.segmentCount : 4;
    _postAction = 'none';
    _loadLookups();
  }

  Future<void> _loadLookups() async {
    final engine = ref.read(prov.engineProvider);
    if (engine == null) return;
    try {
      final q = await engine.call('list_queues');
      if (mounted) {
        setState(() => _queues = (q['queues'] as List)
            .map((e) => (e as Map)['name'].toString())
            .toList());
      }
    } catch (_) {/* offline: keep defaults */}
    try {
      final c = await engine.call('list_categories');
      if (mounted) {
        final names = (c['categories'] as List? ?? [])
            .map((e) => (e as Map)['name'].toString())
            .toList();
        setState(() => _categories = names);
      }
    } catch (_) {/* offline: keep defaults */}
  }

  @override
  void dispose() {
    _speedCap.dispose();
    _mirrors.dispose();
    _startAt.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    setState(() => _saving = true);
    final notifier = ref.read(prov.tasksProvider.notifier);
    final mirrors = _mirrors.text
        .split('\n')
        .map((l) => l.trim())
        .where((l) => l.isNotEmpty)
        .toList();
    final cap = int.tryParse(_speedCap.text.trim()) ?? 0;
    int? startAtMs;
    final raw = _startAt.text.trim();
    if (raw.isNotEmpty) {
      final today = DateTime.now();
      DateTime? v;
      for (final fmt in ['HH:mm', 'yyyy-MM-dd HH:mm', 'yyyy/MM/dd HH:mm']) {
        v = _tryParse(today, raw, fmt);
        if (v != null) break;
      }
      if (v != null) {
        startAtMs = v.isAfter(today) ? v.millisecondsSinceEpoch : 0;
      }
    }
    try {
      await notifier.updateTask(widget.task.taskKey,
          queueName: _queue,
          category: _category,
          priority: _priority,
          maxConnections: _connections,
          speedCapBytes: cap,
          postAction: _postAction == 'none' ? '' : _postAction,
          mirrors: mirrors,
          startAtMs: startAtMs);
    } catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text('$e')));
      }
    }
    if (mounted) Navigator.of(context).pop();
  }

  static DateTime? _tryParse(DateTime now, String raw, String fmt) {
    if (fmt == 'HH:mm') {
      final parts = raw.split(':');
      if (parts.length == 2) {
        final h = int.tryParse(parts[0]);
        final m = int.tryParse(parts[1]);
        if (h != null && m != null && h >= 0 && h < 24 && m >= 0 && m < 60) {
          return DateTime(now.year, now.month, now.day, h, m);
        }
      }
      return null;
    }
    final normalized = raw.replaceAll('/', '-');
    return DateTime.tryParse('$normalized:00'.length >= 16
        ? normalized.contains(' ')
            ? normalized
            : normalized
        : normalized);
  }

  @override
  Widget build(BuildContext context) {
    final s = S.of(context);
    final cs = Theme.of(context).colorScheme;
    return AlertDialog(
      title: Row(children: [
        const Icon(Icons.tune_rounded, size: 22),
        const SizedBox(width: 10),
        Expanded(
            child: Text(s.editTask,
                style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18))),
      ]),
      content: SizedBox(
        width: 480,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              SelectableText(widget.task.fileName.isEmpty ? widget.task.url : widget.task.fileName,
                  style: TextStyle(fontSize: 12.5, color: cs.onSurfaceVariant)),
              const SizedBox(height: 16),
              _field(s.queue, DropdownButtonFormField<String>(
                value: _queues.contains(_queue) ? _queue : null,
                items: _queues.map((q) => DropdownMenuItem(value: q, child: Text(q))).toList(),
                onChanged: (v) => setState(() => _queue = v ?? 'main'),
                decoration: _deco(s.queue),
              )),
              const SizedBox(height: 12),
              _field(s.category, DropdownButtonFormField<String>(
                value: _category,
                items: {
                  ..._categories,
                  if (!_categories.contains(_category)) _category,
                }.map((c) => DropdownMenuItem(value: c, child: Text(c))).toList(),
                onChanged: (v) => setState(() => _category = v ?? 'other'),
                decoration: _deco(s.category),
              )),
              const SizedBox(height: 12),
              _field(s.priority, DropdownButtonFormField<int>(
                value: _priority,
                items: [
                  DropdownMenuItem(value: 2, child: Text(s.priorityHigh)),
                  DropdownMenuItem(value: 1, child: Text(s.priorityNormal)),
                  DropdownMenuItem(value: 0, child: Text(s.priorityLow)),
                ],
                onChanged: (v) => setState(() => _priority = v ?? 1),
                decoration: _deco(s.priority),
              )),
              const SizedBox(height: 12),
              _field(s.connections, DropdownButtonFormField<int>(
                value: _connections,
                items: [1, 2, 4, 8, 16, 24, 32]
                    .map((n) => DropdownMenuItem(value: n, child: Text('$n')))
                    .toList(),
                onChanged: (v) => setState(() => _connections = v ?? 4),
                decoration: _deco(s.connections),
              )),
              const SizedBox(height: 12),
              _field(s.speedCapBytes, TextField(
                controller: _speedCap,
                keyboardType: TextInputType.number,
                decoration: _deco(s.speedCapBytes, icon: Icons.speed_rounded),
              )),
              const SizedBox(height: 12),
              _field('${s.startAt} (HH:mm)', TextField(
                controller: _startAt,
                decoration: _deco(s.startAt, icon: Icons.schedule_rounded),
              )),
              const SizedBox(height: 12),
              _field(s.postAction, DropdownButtonFormField<String>(
                initialValue: _postAction,
                items: [
                  DropdownMenuItem(value: 'none', child: Text(s.postActionNone)),
                  DropdownMenuItem(value: 'exit', child: Text(s.postActionExit)),
                  DropdownMenuItem(value: 'shutdown', child: Text(s.postActionShutdown)),
                  DropdownMenuItem(value: 'hibernate', child: Text(s.postActionHibernate)),
                  DropdownMenuItem(value: 'sleep', child: Text(s.postActionSleep)),
                  DropdownMenuItem(value: 'logoff', child: Text(s.postActionLogoff)),
                ],
                onChanged: (v) => setState(() => _postAction = v ?? 'none'),
                decoration: _deco(s.postAction),
              )),
              const SizedBox(height: 12),
              _field(s.mirrorsHint, TextField(
                controller: _mirrors,
                maxLines: 3,
                decoration: _deco(s.mirrorsHint, icon: Icons.link_rounded),
              )),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.of(context).pop(), child: Text(s.close)),
        FilledButton(
          onPressed: _saving ? null : _save,
          child: _saving
              ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
              : Text(s.save),
        ),
      ],
    );
  }

  Widget _field(String label, Widget child) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label, style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w700)),
          const SizedBox(height: 6),
          child,
        ],
      );

  InputDecoration _deco(String hint, {IconData? icon}) => InputDecoration(
        hintText: hint,
        isDense: true,
        prefixIcon: icon == null ? null : Icon(icon, size: 17),
        border: const OutlineInputBorder(),
      );
}
