import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/models.dart';
import '../core/strings.dart';
import '../core/theme.dart';
import '../providers/engine.dart' as prov;

/// Overview: live stats cards + active tasks summary.
class OverviewPage extends ConsumerWidget {
  final String search;
  const OverviewPage({super.key, this.search = ''});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final tasks = ref.watch(prov.tasksProvider).value ?? <TaskUi>[];
    final connected = ref.watch(prov.engineProvider) != null;
    final active = tasks.where((t) => t.isRunning).toList();
    final speed = active.fold<int>(0, (a, t) => a + t.speedBps);
    final queued = tasks.where((t) => t.state == 'queued').length;
    final completed = tasks.where((t) => t.state == 'completed').length;
    final failed = tasks.where((t) => t.state == 'failed' || t.state == 'interrupted').length;

    return ListView(
      padding: const EdgeInsets.all(20),
      children: [
        Row(
          children: [
            _StatCard(context, icon: Icons.rocket_launch_rounded, color: NovaColors.downloading,
                value: '${active.length}', label: s.activeTasks),
            const SizedBox(width: 14),
            _StatCard(context, icon: Icons.speed_rounded, color: const Color(0xFF8C4DF0),
                value: Fmt.speed(context, speed), label: s.totalSpeed),
            const SizedBox(width: 14),
            _StatCard(context, icon: Icons.schedule_rounded, color: NovaColors.queued,
                value: '${queued}', label: s.queuedDownloads),
          ],
        ),
        const SizedBox(height: 14),
        Row(
          children: [
            _StatCard(context, icon: Icons.task_alt_rounded, color: NovaColors.completed,
                value: '${completed}', label: s.completedDownloads),
            const SizedBox(width: 14),
            _StatCard(context, icon: Icons.error_outline_rounded, color: NovaColors.failed,
                value: '${failed}', label: s.failedDownloads),
            const SizedBox(width: 14),
            _StatCard(context, icon: Icons.power_rounded,
                color: connected ? NovaColors.success : NovaColors.danger,
                value: connected ? s.engineConnected : s.engineDisconnected, label: ''),
          ],
        ),
        const SizedBox(height: 22),
        if (active.isNotEmpty) ...[
          Text(s.activeDownloads, style: Theme.of(context).textTheme.titleMedium!.copyWith(fontWeight: FontWeight.w800)),
          const SizedBox(height: 10),
          ...active.map((t) => _ActiveRow(task: t)),
        ],
      ],
    );
  }

  Widget _StatCard(BuildContext context,
      {required IconData icon, required Color color, required String value, required String label}) {
    return Expanded(
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Container(
                width: 42,
                height: 42,
                decoration: BoxDecoration(color: color.withOpacity(0.13), borderRadius: BorderRadius.circular(11)),
                child: Icon(icon, color: color, size: 21),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(value, maxLines: 1, overflow: TextOverflow.ellipsis,
                        style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
                    if (label.isNotEmpty)
                      Text(label, style: TextStyle(fontSize: 11.5, color: Theme.of(context).colorScheme.onSurfaceVariant)),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _ActiveRow extends StatelessWidget {
  final TaskUi task;
  const _ActiveRow({required this.task});

  @override
  Widget build(BuildContext context) {
    final color = NovaColors.stateColor(task.state, Theme.of(context).brightness);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                    child: Text(task.fileName,
                        maxLines: 1, overflow: TextOverflow.ellipsis,
                        style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13))),
                Text(Fmt.percent(context, task.progress),
                    style: TextStyle(fontWeight: FontWeight.w800, color: color, fontSize: 13)),
              ],
            ),
            const SizedBox(height: 8),
            ClipRRect(
              borderRadius: BorderRadius.circular(4),
              child: LinearProgressIndicator(
                value: task.progress,
                minHeight: 5,
                color: color,
                backgroundColor: Theme.of(context).colorScheme.surfaceContainerHighest.withOpacity(0.55),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
