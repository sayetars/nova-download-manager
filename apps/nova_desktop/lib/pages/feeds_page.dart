import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/models.dart';
import '../core/strings.dart';
import '../core/theme.dart';
import '../providers/engine.dart' as prov;

/// v1.3.0 — RSS/Atom feed subscriptions: new enclosures matching the filters
/// become downloads automatically (podcasts, release feeds, file dumps).
class FeedsPage extends ConsumerWidget {
  const FeedsPage({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final rules = ref.watch(prov.feedRulesProvider);
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
                    Text(s.feeds,
                        style: Theme.of(context)
                            .textTheme
                            .titleLarge!
                            .copyWith(fontWeight: FontWeight.w800)),
                    Text(s.feedsHint, style: Theme.of(context).textTheme.bodySmall),
                  ],
                ),
              ),
              FilledButton.tonalIcon(
                onPressed: () => showFeedEditor(context, ref, s, null),
                icon: const Icon(Icons.add_rounded, size: 18),
                label: Text(s.addFeed),
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
                          Icon(Icons.rss_feed_rounded, size: 54, color: Theme.of(context).colorScheme.outline),
                          const SizedBox(height: 12),
                          Text(s.feeds, style: Theme.of(context).textTheme.titleMedium),
                          const SizedBox(height: 6),
                          Text(s.feedsHint,
                              style: Theme.of(context).textTheme.bodySmall,
                              textAlign: TextAlign.center),
                        ],
                      ),
                    ),
                  )
                : ListView.separated(
                    padding: const EdgeInsets.all(20),
                    itemCount: list.length,
                    separatorBuilder: (_, __) => const SizedBox(height: 8),
                    itemBuilder: (context, i) => _FeedCard(rule: list[i]),
                  ),
            loading: () => const Center(child: CircularProgressIndicator()),
            error: (e, _) => Center(child: Text('$e')),
          ),
        ),
      ],
    );
  }
}

class _FeedCard extends ConsumerWidget {
  final FeedRuleUi rule;
  const _FeedCard({required this.rule});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final cs = Theme.of(context).colorScheme;
    return Card(
      elevation: 0,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(14),
        side: BorderSide(color: cs.outlineVariant.withOpacity(0.6)),
      ),
      child: ListTile(
        leading: CircleAvatar(
          backgroundColor: (rule.enabled ? NovaColors.success : cs.outline).withOpacity(0.15),
          child: Icon(Icons.rss_feed_rounded,
              size: 20, color: rule.enabled ? NovaColors.success : cs.outline),
        ),
        title: Text(rule.name,
            style: const TextStyle(fontWeight: FontWeight.w700),
            maxLines: 1,
            overflow: TextOverflow.ellipsis),
        subtitle: Padding(
          padding: const EdgeInsets.only(top: 3),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(rule.url,
                  style: TextStyle(fontSize: 12, color: cs.onSurfaceVariant),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis),
              const SizedBox(height: 3),
              Wrap(
                spacing: 6,
                runSpacing: 4,
                children: [
                  _chip(context, '${rule.intervalMinutes} min'),
                  if (rule.filters.isNotEmpty) _chip(context, rule.filters),
                  _chip(context, rule.category),
                  if (!rule.autoStart) _chip(context, s.paused),
                ],
              ),
            ],
          ),
        ),
        trailing: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            IconButton(
              tooltip: s.runNow,
              onPressed: () async {
                final engine = ref.read(prov.engineProvider);
                if (engine == null) return;
                try {
                  final resp = await engine.call('run_feed', {'id': rule.id});
                  if (!context.mounted) return;
                  ScaffoldMessenger.of(context).showSnackBar(SnackBar(
                      content: Text(s.feedAdded
                          .replaceAll('{a}', '${resp['added'] ?? 0}')
                          .replaceAll('{s}', '${resp['seen'] ?? 0}'))));
                } catch (_) {}
                ref.invalidate(prov.feedRulesProvider);
                ref.invalidate(prov.tasksProvider);
              },
              icon: const Icon(Icons.play_arrow_rounded, size: 20),
            ),
            IconButton(
              tooltip: s.editFeed,
              onPressed: () => showFeedEditor(context, ref, s, rule),
              icon: const Icon(Icons.edit_outlined, size: 19),
            ),
            IconButton(
              tooltip: s.deleteFeed,
              onPressed: () async {
                final engine = ref.read(prov.engineProvider);
                if (engine == null) return;
                final ok = await showDialog<bool>(
                  context: context,
                  builder: (ctx) => AlertDialog(
                    title: Text(s.deleteFeed),
                    content: Text('"${rule.name}" — ${rule.url}'),
                    actions: [
                      TextButton(onPressed: () => Navigator.pop(ctx, false), child: Text(S.of(ctx).close)),
                      FilledButton(
                          style: FilledButton.styleFrom(backgroundColor: NovaColors.danger),
                          onPressed: () => Navigator.pop(ctx, true),
                          child: Text(S.of(ctx).delete)),
                    ],
                  ),
                );
                if (ok == true) {
                  await engine.call('delete_feed_rule', {'id': rule.id});
                  ref.invalidate(prov.feedRulesProvider);
                }
              },
              icon: Icon(Icons.delete_outline, size: 19, color: NovaColors.danger),
            ),
          ],
        ),
      ),
    );
  }

  Widget _chip(BuildContext context, String text) {
    final cs = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: cs.surfaceContainerHighest.withOpacity(0.6),
        borderRadius: BorderRadius.circular(99),
      ),
      child: Text(text, style: TextStyle(fontSize: 11, color: cs.onSurfaceVariant)),
    );
  }
}

Future<void> showFeedEditor(
    BuildContext context, WidgetRef ref, S s, FeedRuleUi? existing) async {
  final name = TextEditingController(text: existing?.name ?? '');
  final url = TextEditingController(text: existing?.url ?? '');
  final filters = TextEditingController(text: existing?.filters ?? '');
  final dest = TextEditingController(text: existing?.destinationDir ?? '');
  var interval = existing?.intervalMinutes ?? 30;
  var enabled = existing?.enabled ?? true;

  await showDialog(
    context: context,
    builder: (context) => StatefulBuilder(
      builder: (context, setState) => AlertDialog(
        title: Text(existing == null ? s.addFeed : s.editFeed),
        content: SizedBox(
          width: 500,
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                TextField(controller: name, decoration: InputDecoration(labelText: s.ruleName)),
                const SizedBox(height: 12),
                TextField(
                  controller: url,
                  decoration: InputDecoration(labelText: s.feedUrl, hintText: 'https://example.com/feed.xml'),
                ),
                const SizedBox(height: 12),
                TextField(
                  controller: filters,
                  decoration: InputDecoration(
                      labelText: s.feedFilters, helperText: s.feedFiltersHint),
                ),
                const SizedBox(height: 12),
                TextField(controller: dest, decoration: InputDecoration(labelText: s.destination)),
                const SizedBox(height: 12),
                DropdownButtonFormField<int>(
                  initialValue: interval,
                  decoration: InputDecoration(labelText: s.intervalMinutes),
                  items: const [
                    DropdownMenuItem(value: 5, child: Text('5')),
                    DropdownMenuItem(value: 15, child: Text('15')),
                    DropdownMenuItem(value: 30, child: Text('30')),
                    DropdownMenuItem(value: 60, child: Text('60')),
                    DropdownMenuItem(value: 360, child: Text('360')),
                    DropdownMenuItem(value: 1440, child: Text('1440')),
                  ],
                  onChanged: (v) => setState(() => interval = v ?? 30),
                ),
                SwitchListTile(
                  value: enabled,
                  onChanged: (v) => setState(() => enabled = v),
                  title: Text(s.enabled, style: const TextStyle(fontSize: 13.5)),
                  contentPadding: EdgeInsets.zero,
                  dense: true,
                ),
              ],
            ),
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: Text(s.close)),
          FilledButton(
            onPressed: () async {
              final engine = ref.read(prov.engineProvider);
              if (engine == null) return;
              await engine.call('save_feed_rule', {
                if (existing != null) 'id': existing.id,
                'name': name.text.trim(),
                'url': url.text.trim(),
                'filters': filters.text.trim(),
                'destination_dir': dest.text.trim(),
                'category': 'other',
                'interval_minutes': interval,
                'enabled': enabled,
              });
              ref.invalidate(prov.feedRulesProvider);
              if (context.mounted) Navigator.pop(context);
            },
            child: Text(s.save),
          ),
        ],
      ),
    ),
  );
}
