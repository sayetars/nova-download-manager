import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/models.dart';
import '../core/strings.dart';
import '../core/theme.dart';
import '../providers/engine.dart' as prov;

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
