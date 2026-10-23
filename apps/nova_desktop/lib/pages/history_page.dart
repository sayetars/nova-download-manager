import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/models.dart';
import '../core/strings.dart';
import '../core/theme.dart';
import '../providers/engine.dart' as prov;

/// History — completed/failed archive view.
class HistoryPage extends ConsumerWidget {
  final String search;
  const HistoryPage({super.key, this.search = ''});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final tasks = ref.watch(prov.tasksProvider).value ?? <TaskUi>[];
    var list = tasks.where((t) => t.state == 'completed' || t.state == 'cancelled').toList();
    if (search.isNotEmpty) {
      final q = search.toLowerCase();
      list = list.where((t) => t.fileName.toLowerCase().contains(q) || t.url.toLowerCase().contains(q)).toList();
    }

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 14, 20, 6),
          child: Row(
            children: [
              Expanded(
                  child: Text(s.history,
                      style: Theme.of(context).textTheme.titleLarge!.copyWith(fontWeight: FontWeight.w800))),
              TextButton.icon(
                onPressed: () async {
                  await ref.read(prov.engineProvider)?.call('purge_history', {'older_than_days': 30, 'only_completed': true});
                  await ref.read(prov.tasksProvider.notifier).refresh();
                },
                icon: const Icon(Icons.cleaning_services_outlined, size: 18),
                label: Text(s.purgeHistory),
              ),
            ],
          ),
        ),
        Expanded(
          child: list.isEmpty
              ? Center(
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Icon(Icons.history_rounded, size: 60, color: Theme.of(context).colorScheme.outline),
                      const SizedBox(height: 12),
                      Text(s.noDownloads, style: Theme.of(context).textTheme.bodyLarge),
                    ],
                  ),
                )
              : ListView.separated(
                  padding: const EdgeInsets.all(20),
                  itemCount: list.length,
                  separatorBuilder: (_, __) => const SizedBox(height: 6),
                  itemBuilder: (context, i) {
                    final t = list[i];
                    final ok = t.state == 'completed';
                    return Card(
                      child: ListTile(
                        leading: Icon(
                          ok ? Icons.check_circle_rounded : Icons.cancel_outlined,
                          color: ok ? NovaColors.completed : NovaColors.paused,
                          size: 21,
                        ),
                        title: Text(t.fileName,
                            maxLines: 1, overflow: TextOverflow.ellipsis,
                            style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13)),
                        subtitle: Text(
                          '${Fmt.bytes(context, t.totalBytes >= 0 ? t.totalBytes : null)}'
                          '${t.completedAtMs != null ? ' · ${_date(context, t.completedAtMs!)}' : ''}',
                          style: const TextStyle(fontSize: 12),
                        ),
                        trailing: IconButton(
                          icon: const Icon(Icons.folder_open_rounded, size: 18),
                          onPressed: () =>
                              ref.read(prov.engineProvider)?.call('open_folder', {'task_key': t.taskKey}),
                        ),
                      ),
                    );
                  },
                ),
        ),
      ],
    );
  }

  String _date(BuildContext context, int ms) {
    final d = DateTime.fromMillisecondsSinceEpoch(ms);
    final locale = Localizations.localeOf(context).toString();
    final two = (int v) => v.toString().padLeft(2, '0');
    return '${two(d.month)}/${two(d.day)} ${two(d.hour)}:${two(d.minute)}'
        .translateDigitsFa(locale);
  }
}

extension on String {
  String translateDigitsFa(String locale) {
    if (!locale.startsWith('fa')) return this;
    const f = ['۰', '۱', '۲', '۳', '۴', '۵', '۶', '۷', '۸', '۹'];
    var out = '';
    for (final c in codeUnits) {
      out += (c >= 0x30 && c <= 0x39) ? f[c - 0x30] : String.fromCharCode(c);
    }
    return out;
  }
}
