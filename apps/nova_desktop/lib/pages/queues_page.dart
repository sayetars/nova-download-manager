import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/models.dart';
import '../core/strings.dart';
import '../core/theme.dart';
import '../providers/engine.dart' as prov;

const dayLabels = ['S', 'M', 'T', 'W', 'T', 'F', 'S'];
const dayLabelsFa = ['ی', 'د', 'س', 'چ', 'پ', 'ج', 'ش'];

String dayLabel(int d, bool isFa) => isFa ? dayLabelsFa[d] : dayLabels[d];
String daysToText(String days, bool isFa) => days.isEmpty
    ? ''
    : days.split(',').map((d) => dayLabel(int.tryParse(d) ?? 0, isFa)).join(',');

/// Queues manager.
class QueuesPage extends ConsumerStatefulWidget {
  const QueuesPage({super.key});
  @override
  ConsumerState<QueuesPage> createState() => _QueuesPageState();
}

class _QueuesPageState extends ConsumerState<QueuesPage> {
  Future<void> _addQueue() async {
    final s = S.of(context);
    final name = TextEditingController();
    var maxConcurrent = 2;
    await showDialog(
      context: context,
      builder: (context) => StatefulBuilder(
        builder: (context, setState) => AlertDialog(
          title: Text(s.addQueue),
          content: SizedBox(
            width: 420,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                TextField(controller: name, decoration: InputDecoration(labelText: s.queueName)),
                const SizedBox(height: 14),
                DropdownButtonFormField<int>(
                  initialValue: maxConcurrent,
                  decoration: InputDecoration(labelText: s.maxConcurrentPerQueue),
                  items: [1, 2, 3, 4, 5, 8].map((v) => DropdownMenuItem(value: v, child: Text('$v'))).toList(),
                  onChanged: (v) => setState(() => maxConcurrent = v ?? 2),
                ),
              ],
            ),
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(context), child: Text(s.cancel)),
            FilledButton(
              onPressed: () => Navigator.pop(context, name.text.trim()),
              child: Text(s.save),
            ),
          ],
        ),
      ),
    ).then((name) async {
      if (name != null && name.isNotEmpty) {
        await ref.read(prov.engineProvider)?.call('save_queue', {'name': name, 'max_concurrent': maxConcurrent});
        ref.invalidate(prov.queuesProvider);
      }
      name?.dispose;
    });
  }

  @override
  Widget build(BuildContext context) {
    final s = S.of(context);
    final queues = ref.watch(prov.queuesProvider);
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 14, 20, 6),
          child: Row(
            children: [
              Expanded(child: Text(s.queues, style: Theme.of(context).textTheme.titleLarge!.copyWith(fontWeight: FontWeight.w800))),
              FilledButton.tonalIcon(
                onPressed: _addQueue,
                icon: const Icon(Icons.add_rounded, size: 18),
                label: Text(s.addQueue),
              ),
            ],
          ),
        ),
        Expanded(
          child: queues.when(
            data: (list) => ListView.separated(
              padding: const EdgeInsets.all(20),
              itemCount: list.length,
              separatorBuilder: (_, __) => const SizedBox(height: 8),
              itemBuilder: (context, i) => _QueueCard(queue: list[i]),
            ),
            loading: () => const Center(child: CircularProgressIndicator()),
            error: (e, _) => Center(child: Text('$e')),
          ),
        ),
      ],
    );
  }
}

class _QueueCard extends ConsumerWidget {
  final QueueUi queue;
  const _QueueCard({required this.queue});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final cs = Theme.of(context).colorScheme;
    final isFa = Localizations.localeOf(context).languageCode == 'fa';
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(8),
                  decoration: BoxDecoration(
                      color: NovaColors.queued.withOpacity(0.13), borderRadius: BorderRadius.circular(9)),
                  child: const Icon(Icons.account_tree_rounded, size: 17, color: NovaColors.queued),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(queue.name,
                      style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 14)),
                ),
                if (queue.name != 'main')
                  IconButton(
                    icon: const Icon(Icons.delete_outline_rounded, size: 18),
                    onPressed: () async {
                      await ref.read(prov.engineProvider)?.call('delete_queue', {'task_key': queue.name});
                      ref.invalidate(prov.queuesProvider);
                    },
                  ),
              ],
            ),
            const SizedBox(height: 12),
            Row(
              children: [
                _chip(context, '${queue.activeCount} ${s.activeDownloads.toLowerCase()}', NovaColors.downloading),
                const SizedBox(width: 8),
                _chip(context, '${queue.waitingCount} ${s.queuedDownloads.toLowerCase()}', NovaColors.queued),
                const SizedBox(width: 8),
                _chip(context, '${queue.totalCount} ${s.allDownloads.toLowerCase()}', cs.onSurfaceVariant),
                const Spacer(),
                Text(
                  '${s.maxConcurrentPerQueue}: ${queue.maxConcurrent > 0 ? '${queue.maxConcurrent}' : s.unlimited}',
                  style: TextStyle(fontSize: 12, color: cs.onSurfaceVariant),
                ),
              ],
            ),
            if (queue.schedule.isNotEmpty) ...[
              const SizedBox(height: 8),
              Wrap(
                spacing: 6,
                runSpacing: 4,
                children: [
                  for (final w in queue.schedule)
                    InputChip(
                      avatar: Icon(
                        w.action == 'run' ? Icons.play_arrow_rounded : Icons.block_rounded,
                        size: 14,
                        color: w.action == 'run' ? NovaColors.completed : NovaColors.failed,
                      ),
                      label: Text(
                        '${w.action == 'run' ? s.scheduleRunWindows : s.schedulePauseWindows}: '
                        '${w.startTime}–${w.endTime}'
                        '${w.daysOfWeek.isEmpty ? '' : ' · ${daysToText(w.daysOfWeek, isFa)}'}',
                        style: const TextStyle(fontSize: 11),
                      ),
                    ),
                ],
              ),
            ],
            Align(
              alignment: AlignmentDirectional.centerEnd,
              child: TextButton.icon(
                onPressed: () => _editSchedule(context, ref, s, queue),
                icon: const Icon(Icons.schedule_rounded, size: 16),
                label: Text(s.queueSchedule, style: const TextStyle(fontSize: 12.5)),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _chip(BuildContext context, String text, Color color) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
        decoration: BoxDecoration(color: color.withOpacity(0.12), borderRadius: BorderRadius.circular(99)),
        child: Text(text, style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: color)),
      );
}

/// Scheduler: concurrency + speed caps + time-window rules.
class SchedulerPage extends ConsumerWidget {
  const SchedulerPage({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final settings = ref.watch(prov.settingsProvider);
    final rules = ref.watch(prov.schedulesProvider);
    final engine = ref.watch(prov.engineProvider);
    final maxConcurrent = int.tryParse(settings['scheduler.max_concurrent'] ?? '') ?? 3;
    final globalCap = int.tryParse(settings['engine.global_speed_cap'] ?? '') ?? 0;
    final batteryPause = settings['scheduler.start_on_battery'] != 'false';

    final dayNames = [
      'Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat',
    ];
    final dayNamesFa = ['یک', 'دو', 'سه', 'چهار', 'پنج', 'جمعه', 'شنبه'];
    final isFa = Localizations.localeOf(context).languageCode == 'fa';

    return ListView(
      padding: const EdgeInsets.all(20),
      children: [
        Text(s.scheduler, style: Theme.of(context).textTheme.titleLarge!.copyWith(fontWeight: FontWeight.w800)),
        const SizedBox(height: 16),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Text('${s.maxConcurrent}:', style: const TextStyle(fontWeight: FontWeight.w700)),
                    const Spacer(),
                    SegmentedButton<int>(
                      segments: const [
                        ButtonSegment(value: 1, label: Text('1')),
                        ButtonSegment(value: 3, label: Text('3')),
                        ButtonSegment(value: 5, label: Text('5')),
                        ButtonSegment(value: 8, label: Text('8')),
                      ],
                      selected: {maxConcurrent},
                      onSelectionChanged: (sel) => engine?.call('set_settings', {
                        'values': {'scheduler.max_concurrent': '${sel.first}'}
                      }),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Text('${s.globalSpeedCap}:', style: const TextStyle(fontWeight: FontWeight.w700)),
                    const Spacer(),
                    SegmentedButton<int>(
                      segments: const [
                        ButtonSegment(value: 0, label: Text('∞')),
                        ButtonSegment(value: 2 * 1024 * 1024, label: Text('2 MB/s')),
                        ButtonSegment(value: 5 * 1024 * 1024, label: Text('5 MB/s')),
                        ButtonSegment(value: 10 * 1024 * 1024, label: Text('10 MB/s')),
                      ],
                      selected: {globalCap},
                      onSelectionChanged: (sel) => engine?.call('set_settings', {
                        'values': {'engine.global_speed_cap': '${sel.first}'}
                      }),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                SwitchListTile(
                  value: !batteryPause,
                  onChanged: (v) => engine?.call('set_settings', {
                    'values': {'scheduler.start_on_battery': '$v'}
                  }),
                  title: Text('Start downloads on battery', style: const TextStyle(fontSize: 13.5)),
                  dense: true,
                  contentPadding: EdgeInsets.zero,
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 20),
        Row(
          children: [
            Expanded(
                child: Text(s.newRule,
                    style: Theme.of(context).textTheme.titleMedium!.copyWith(fontWeight: FontWeight.w800))),
            FilledButton.tonalIcon(
              onPressed: () => _addRule(context, ref),
              icon: const Icon(Icons.add_rounded, size: 18),
              label: Text(s.newRule),
            ),
          ],
        ),
        const SizedBox(height: 10),
        rules.when(
          data: (list) => Column(
            children: list
                .map((r) => Card(
                      child: ListTile(
                        leading: Icon(
                          r.enabled ? Icons.alarm_on_rounded : Icons.alarm_off_rounded,
                          color: r.enabled ? NovaColors.completed : NovaColors.paused,
                        ),
                        title: Text(r.name.isEmpty ? s.ruleName : r.name,
                            style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13.5)),
                        subtitle: Text(
                          '${r.startTime} → ${r.endTime}'
                          '${r.daysOfWeek.isEmpty ? '' : ' · ${r.daysOfWeek.split(',').map((d) => isFa ? dayNamesFa[int.tryParse(d) ?? 0] : dayNames[int.tryParse(d) ?? 0]).join(', ')}'}',
                          style: const TextStyle(fontSize: 12),
                        ),
                        trailing: Switch(
                          value: r.enabled,
                          onChanged: (v) async {
                            await engine?.call('save_schedule', {...r.toJson(), 'enabled': v});
                            ref.invalidate(prov.schedulesProvider);
                          },
                        ),
                      ),
                    ))
                .toList(),
          ),
          loading: () => const SizedBox(height: 60, child: Center(child: CircularProgressIndicator())),
          error: (e, _) => Text('$e'),
        ),
      ],
    );
  }

  Future<void> _addRule(BuildContext context, WidgetRef ref) async {
    final s = S.of(context);
    final engine = ref.read(prov.engineProvider);
    final name = TextEditingController();
    final start = TextEditingController(text: '01:00');
    final end = TextEditingController(text: '07:00');
    final days = <int>{};

    await showDialog(
      context: context,
      builder: (context) => StatefulBuilder(
        builder: (context, setState) => AlertDialog(
          title: Text(s.newRule),
          content: SizedBox(
            width: 460,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                TextField(controller: name, decoration: InputDecoration(labelText: s.ruleName)),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Expanded(child: TextField(controller: start, decoration: InputDecoration(labelText: s.startTime))),
                    const SizedBox(width: 10),
                    Expanded(child: TextField(controller: end, decoration: InputDecoration(labelText: s.endTime))),
                  ],
                ),
                const SizedBox(height: 14),
                Wrap(
                  spacing: 6,
                  children: [
                    for (var d = 0; d < 7; d++)
                      FilterChip(
                        label: Text(['S', 'M', 'T', 'W', 'T', 'F', 'S'][d]),
                        selected: days.contains(d),
                        onSelected: (v) => setState(() => v ? days.add(d) : days.remove(d)),
                      ),
                  ],
                ),
              ],
            ),
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(context), child: Text(s.cancel)),
            FilledButton(
              onPressed: () => Navigator.pop(context, true),
              child: Text(s.save),
            ),
          ],
        ),
      ),
    ).then((ok) async {
      if (ok == true) {
        await engine?.call('save_schedule', {
          'name': name.text,
          'start_time': start.text,
          'end_time': end.text,
          'days_of_week': days.toList().join(','),
          'enabled': true,
        });
        ref.invalidate(prov.schedulesProvider);
      }
    });
  }
}

// ---- IDM parity v1.2.0: per-queue start/stop schedules -------------------------

extension _QueueScheduleEditor on _QueueCard {
  Future<void> _editSchedule(BuildContext context, WidgetRef ref, S s, QueueUi queue) async {
    var windows = [...queue.schedule];
    final isFa = Localizations.localeOf(context).languageCode == 'fa';

    await showDialog(
      context: context,
      builder: (context) => StatefulBuilder(
        builder: (context, setState) => AlertDialog(
          title: Text('${s.queueSchedule} — ${queue.name}'),
          content: SizedBox(
            width: 520,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.queueScheduleHint, style: Theme.of(context).textTheme.bodySmall),
                const SizedBox(height: 12),
                Flexible(
                  child: ListView.builder(
                    shrinkWrap: true,
                    itemCount: windows.length,
                    itemBuilder: (context, i) {
                      final w = windows[i];
                      return Card(
                        margin: const EdgeInsets.only(bottom: 8),
                        child: Padding(
                          padding: const EdgeInsets.all(10),
                          child: Column(
                            children: [
                              Row(
                                children: [
                                  SegmentedButton<String>(
                                    segments: [
                                      ButtonSegment(value: 'run', label: Text(s.scheduleRunWindows)),
                                      ButtonSegment(value: 'pause', label: Text(s.schedulePauseWindows)),
                                    ],
                                    selected: {w.action},
                                    onSelectionChanged: (sel) =>
                                        setState(() => windows[i] = QueueScheduleRuleUi(
                                              action: sel.first,
                                              daysOfWeek: w.daysOfWeek,
                                              startTime: w.startTime,
                                              endTime: w.endTime,
                                              enabled: w.enabled,
                                            )),
                                  ),
                                  const Spacer(),
                                  IconButton(
                                    icon: const Icon(Icons.delete_outline_rounded, size: 18),
                                    onPressed: () => setState(() => windows.removeAt(i)),
                                  ),
                                ],
                              ),
                              const SizedBox(height: 8),
                              Row(
                                children: [
                                  Expanded(
                                    child: TextFormField(
                                      initialValue: w.startTime,
                                      decoration: InputDecoration(labelText: s.startTime, isDense: true),
                                      onChanged: (v) => windows[i] = QueueScheduleRuleUi(
                                        action: w.action, daysOfWeek: w.daysOfWeek,
                                        startTime: v, endTime: w.endTime, enabled: w.enabled,
                                      ),
                                    ),
                                  ),
                                  const SizedBox(width: 10),
                                  Expanded(
                                    child: TextFormField(
                                      initialValue: w.endTime,
                                      decoration: InputDecoration(labelText: s.endTime, isDense: true),
                                      onChanged: (v) => windows[i] = QueueScheduleRuleUi(
                                        action: w.action, daysOfWeek: w.daysOfWeek,
                                        startTime: w.startTime, endTime: v, enabled: w.enabled,
                                      ),
                                    ),
                                  ),
                                ],
                              ),
                              const SizedBox(height: 8),
                              Wrap(
                                spacing: 4,
                                children: [
                                  for (var d = 0; d < 7; d++)
                                    FilterChip(
                                      label: Text(dayLabel(d, isFa)),
                                      visualDensity: VisualDensity.compact,
                                      selected: w.daysOfWeek.split(',').contains('$d'),
                                      onSelected: (v) {
                                        final set = w.daysOfWeek.split(',').where((x) => x.isNotEmpty).toSet();
                                        v ? set.add('$d') : set.remove('$d');
                                        setState(() => windows[i] = QueueScheduleRuleUi(
                                              action: w.action, daysOfWeek: set.toList().join(','),
                                              startTime: w.startTime, endTime: w.endTime, enabled: w.enabled,
                                            ));
                                      },
                                    ),
                                ],
                              ),
                            ],
                          ),
                        ),
                      );
                    },
                  ),
                ),
                TextButton.icon(
                  onPressed: () => setState(() => windows = [
                    ...windows,
                    const QueueScheduleRuleUi(action: 'pause', startTime: '09:00', endTime: '17:00'),
                  ]),
                  icon: const Icon(Icons.add_rounded, size: 18),
                  label: Text(s.addWindow),
                ),
              ],
            ),
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(context), child: Text(s.cancel)),
            FilledButton(
              onPressed: () => Navigator.pop(context, true),
              child: Text(s.save),
            ),
          ],
        ),
      ),
    ).then((ok) async {
      if (ok == true) {
        final json = windows.map((w) => w.toJson()).toList();
        await ref.read(prov.engineProvider)?.call('save_queue', {
          'name': queue.name,
          'schedule_json': jsonEncode(json),
        });
        ref.invalidate(prov.queuesProvider);
      }
    });
  }
}

// ---- IDM parity v1.2.0: periodic sync rules -------------------------------------

class SyncRulesPage extends ConsumerWidget {
  const SyncRulesPage({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final rules = ref.watch(prov.syncRulesProvider);
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 14, 20, 6),
          child: Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(s.syncRules,
                        style: Theme.of(context).textTheme.titleLarge!.copyWith(fontWeight: FontWeight.w800)),
                    Text(s.syncRulesHint, style: Theme.of(context).textTheme.bodySmall),
                  ],
                ),
              ),
              FilledButton.tonalIcon(
                onPressed: () => showSyncRuleEditor(context, ref, s, null),
                icon: const Icon(Icons.add_rounded, size: 18),
                label: Text(s.addSyncRule),
              ),
            ],
          ),
        ),
        Expanded(
          child: rules.when(
            data: (list) => list.isEmpty
                ? Center(
                    child: Padding(
                      padding: const EdgeInsets.all(40),
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Icon(Icons.sync_rounded, size: 54, color: Theme.of(context).colorScheme.outline),
                          const SizedBox(height: 12),
                          Text(s.syncRules, style: Theme.of(context).textTheme.titleMedium),
                          const SizedBox(height: 6),
                          Text(s.syncRulesHint, style: Theme.of(context).textTheme.bodySmall),
                        ],
                      ),
                    ),
                  )
                : ListView.separated(
                    padding: const EdgeInsets.all(20),
                    itemCount: list.length,
                    separatorBuilder: (_, __) => const SizedBox(height: 8),
                    itemBuilder: (context, i) => _SyncRuleCard(rule: list[i]),
                  ),
            loading: () => const Center(child: CircularProgressIndicator()),
            error: (e, _) => Center(child: Text('$e')),
          ),
        ),
      ],
    );
  }
}

Future<void> showSyncRuleEditor(BuildContext context, WidgetRef ref, S s, SyncRuleUi? existing) async {
  final name = TextEditingController(text: existing?.name ?? '');
  final urls = TextEditingController(text: existing?.urls ?? '');
  final dest = TextEditingController(text: existing?.destinationDir ?? '');
  var interval = existing?.intervalMinutes ?? 60;
  var enabled = existing?.enabled ?? true;

  await showDialog(
    context: context,
    builder: (context) => StatefulBuilder(
      builder: (context, setState) => AlertDialog(
        title: Text(existing == null ? s.addSyncRule : s.editSyncRule),
        content: SizedBox(
          width: 480,
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                TextField(controller: name, decoration: InputDecoration(labelText: s.ruleName)),
                const SizedBox(height: 12),
                TextField(
                  controller: urls,
                  maxLines: 5,
                  decoration: InputDecoration(labelText: s.urlsOnePerLine, alignLabelWithHint: true),
                ),
                const SizedBox(height: 12),
                TextField(controller: dest, decoration: InputDecoration(labelText: s.destination)),
                const SizedBox(height: 12),
                DropdownButtonFormField<int>(
                  initialValue: interval,
                  decoration: InputDecoration(labelText: s.intervalMinutes),
                  items: const [
                    DropdownMenuItem(value: 15, child: Text('15')),
                    DropdownMenuItem(value: 30, child: Text('30')),
                    DropdownMenuItem(value: 60, child: Text('60')),
                    DropdownMenuItem(value: 360, child: Text('360')),
                    DropdownMenuItem(value: 1440, child: Text('1440')),
                  ],
                  onChanged: (v) => setState(() => interval = v ?? 60),
                ),
                SwitchListTile(
                  value: enabled,
                  onChanged: (v) => setState(() => enabled = v),
                  title: Text(s.enabled, style: const TextStyle(fontSize: 13.5)),
                  dense: true,
                  contentPadding: EdgeInsets.zero,
                ),
              ],
            ),
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: Text(s.cancel)),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: Text(s.save),
          ),
        ],
      ),
    ),
  ).then((ok) async {
    if (ok == true && name.text.trim().isNotEmpty && urls.text.trim().isNotEmpty) {
      await ref.read(prov.engineProvider)?.call('save_sync_rule', {
        if (existing != null && existing.id != 0) 'id': existing.id,
        'name': name.text.trim(),
        'urls': urls.text,
        'destination_dir': dest.text,
        'interval_minutes': interval,
        'enabled': enabled,
      });
      ref.invalidate(prov.syncRulesProvider);
    }
  });
}

class _SyncRuleCard extends ConsumerWidget {
  final SyncRuleUi rule;
  const _SyncRuleCard({required this.rule});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final cs = Theme.of(context).colorScheme;
    final urls = rule.urls.split('\n').where((u) => u.trim().isNotEmpty).length;
    final lastRun = rule.lastRunMs <= 0
        ? s.never
        : DateTime.fromMillisecondsSinceEpoch(rule.lastRunMs).toLocal().toString().split('.').first;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(8),
                  decoration: BoxDecoration(
                      color: (rule.enabled ? NovaColors.completed : NovaColors.paused).withOpacity(0.13),
                      borderRadius: BorderRadius.circular(9)),
                  child: Icon(Icons.sync_rounded,
                      size: 17, color: rule.enabled ? NovaColors.completed : NovaColors.paused),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(rule.name, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 14)),
                ),
                Switch(
                  value: rule.enabled,
                  onChanged: (v) async {
                    await ref.read(prov.engineProvider)?.call('save_sync_rule', {...rule.toJson(), 'enabled': v});
                    ref.invalidate(prov.syncRulesProvider);
                  },
                ),
              ],
            ),
            const SizedBox(height: 10),
            Text(
              '$urls URL(s) · ${s.intervalMinutes}: ${rule.intervalMinutes}',
              style: TextStyle(fontSize: 12, color: cs.onSurfaceVariant),
            ),
            Text('${s.lastRun}: $lastRun', style: TextStyle(fontSize: 12, color: cs.onSurfaceVariant)),
            const SizedBox(height: 8),
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                TextButton.icon(
                  onPressed: () async {
                    final (added, unchanged) = await ref.read(prov.tasksProvider.notifier).runSync(rule.id);
                    if (context.mounted) {
                      ScaffoldMessenger.of(context).showSnackBar(SnackBar(
                        content: Text(s.syncRunResult.replaceFirst('{a}', '$added').replaceFirst('{u}', '$unchanged')),
                      ));
                    }
                    ref.invalidate(prov.syncRulesProvider);
                  },
                  icon: const Icon(Icons.play_arrow_rounded, size: 18),
                  label: Text(s.runNow, style: const TextStyle(fontSize: 12.5)),
                ),
                TextButton.icon(
                  onPressed: () => _edit(context, ref, s),
                  icon: const Icon(Icons.edit_outlined, size: 16),
                  label: Text(s.editTask, style: const TextStyle(fontSize: 12.5)),
                ),
                TextButton.icon(
                  onPressed: () async {
                    await ref.read(prov.engineProvider)?.call('delete_sync_rule', {'id': rule.id});
                    ref.invalidate(prov.syncRulesProvider);
                  },
                  icon: const Icon(Icons.delete_outline_rounded, size: 16),
                  label: Text(s.delete, style: const TextStyle(fontSize: 12.5)),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _edit(BuildContext context, WidgetRef ref, S s) async {
    await showSyncRuleEditor(context, ref, s, rule);
    ref.invalidate(prov.syncRulesProvider);
  }
}
