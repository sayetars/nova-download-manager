import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/models.dart';
import '../core/strings.dart';
import '../core/theme.dart';
import '../providers/engine.dart' as prov;
import 'browser_page.dart';
import 'downloads_page.dart';
import 'history_page.dart';
import 'overview_page.dart';
import 'queues_page.dart';
import 'settings_page.dart';

/// Which page is selected.
enum NavSection {
  overview,
  all,
  active,
  queued,
  completed,
  failed,
  media,
  queues,
  scheduler,
  browser,
  history,
  diagnostics,
  settings,
}

class ShellPage extends ConsumerStatefulWidget {
  const ShellPage({super.key});

  @override
  ConsumerState<ShellPage> createState() => _ShellPageState();
}

class _ShellPageState extends ConsumerState<ShellPage> {
  NavSection _section = NavSection.overview;
  bool _expanded = true;
  String _search = '';

  @override
  void initState() {
    super.initState();
    // refresh task list once engine connects (provider handles it) + keyboard shortcuts
    HardwareKeyboard.instance.addHandler(_onKey);
  }

  @override
  void dispose() {
    HardwareKeyboard.instance.removeHandler(_onKey);
    super.dispose();
  }

  bool _onKey(KeyEvent e) {
    if (e is KeyDownEvent && e.logicalKey == LogicalKeyboardKey.keyN && HardwareKeyboard.instance.isControlPressed) {
      Future<void>.microtask(() => showAddDownloadDialog(context));
      return true;
    }
    return false;
  }

  @override
  Widget build(BuildContext context) {
    final s = S.of(context);
    final tasks = ref.watch(prov.tasksProvider);
    final status = ref.watch(prov.engineStatusProvider).value;

    final counters = <NavSection, int>{};
    tasks.whenData((list) {
      counters[NavSection.active] = list.where((t) => t.isRunning).length;
      counters[NavSection.queued] = list.where((t) => t.state == 'queued').length;
      counters[NavSection.completed] = list.where((t) => t.state == 'completed').length;
      counters[NavSection.failed] = list.where((t) => t.state == 'failed' || t.state == 'interrupted').length;
      counters[NavSection.media] = list.where((t) => t.kind != 'file').length;
    });

    return Scaffold(
      body: Row(
        children: [
          _rail(s, counters),
          VerticalDivider(
            width: 1,
            color: Theme.of(context).dividerTheme.color,
          ),
          Expanded(
            child: Column(
              children: [
                _toolbar(s, status),
                Divider(height: 1, color: Theme.of(context).dividerTheme.color),
                Expanded(child: _page(s)),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _rail(S s, Map<NavSection, int> counters) {
    final items = <_NavItem>[
      _NavItem(NavSection.overview, Icons.dashboard_outlined, Icons.dashboard, s.overview, null),
      _NavItem(NavSection.all, Icons.download_outlined, Icons.download, s.allDownloads, null),
      _NavItem(NavSection.active, Icons.play_circle_outline, Icons.play_circle, s.activeDownloads, counters[NavSection.active]),
      _NavItem(NavSection.queued, Icons.schedule_outlined, Icons.schedule, s.queuedDownloads, counters[NavSection.queued]),
      _NavItem(NavSection.completed, Icons.task_alt_outlined, Icons.task_alt, s.completedDownloads, counters[NavSection.completed]),
      _NavItem(NavSection.failed, Icons.error_outline, Icons.error, s.failedDownloads, counters[NavSection.failed]),
      _NavItem(NavSection.media, Icons.movie_outlined, Icons.movie, s.mediaDownloads, counters[NavSection.media]),
      const _NavItem(null, null, null, '', null), // spacer
      _NavItem(NavSection.queues, Icons.account_tree_outlined, Icons.account_tree, s.queues, null),
      _NavItem(NavSection.scheduler, Icons.alarm_outlined, Icons.alarm, s.scheduler, null),
      _NavItem(NavSection.browser, Icons.extension_outlined, Icons.extension, s.browserIntegration, null),
      _NavItem(NavSection.history, Icons.history_outlined, Icons.history, s.history, null),
      _NavItem(NavSection.diagnostics, Icons.monitor_heart_outlined, Icons.monitor_heart, s.diagnostics, null),
      _NavItem(NavSection.settings, Icons.settings_outlined, Icons.settings, s.settings, null),
    ];

    return AnimatedContainer(
      duration: const Duration(milliseconds: 180),
      width: _expanded ? 232 : 84,
      curve: Curves.easeOutCubic,
      color: Theme.of(context).navigationRailTheme.backgroundColor,
      child: SafeArea(
        right: false,
        child: Column(
          children: [
            const SizedBox(height: 14),
            _logo(),
            const SizedBox(height: 18),
            Expanded(
              child: ListView(
                padding: const EdgeInsets.symmetric(horizontal: 8),
                children: [
                  for (final it in items)
                    if (it.section == null)
                      const SizedBox(height: 10)
                    else
                      _tile(it),
                ],
              ),
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
  }

  Widget _logo() {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 14),
      child: Row(
        children: [
          Container(
            width: 34,
            height: 34,
            decoration: BoxDecoration(
              gradient: LinearGradient(
                  begin: Alignment.topLeft, end: Alignment.bottomRight,
                  colors: isDark ? const [NovaColors.accentDark, Color(0xFF6C4DF0)] : const [NovaColors.accentLight, Color(0xFF8C4DF0)]),
              borderRadius: BorderRadius.circular(10),
            ),
            child: const Icon(Icons.bolt_rounded, color: Colors.white, size: 20),
          ),
          if (_expanded) ...[
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                'Nova',
                style: Theme.of(context).textTheme.titleLarge!.copyWith(fontWeight: FontWeight.w800, letterSpacing: -0.5),
                overflow: TextOverflow.ellipsis,
              ),
            ),
          ],
        ],
      ),
    );
  }

  Widget _tile(_NavItem it) {
    final sel = _section == it.section;
    final cs = Theme.of(context).colorScheme;
    final s = S.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 2),
      child: Material(
        color: sel ? cs.primaryContainer.withOpacity(0.45) : Colors.transparent,
        borderRadius: BorderRadius.circular(10),
        child: InkWell(
          borderRadius: BorderRadius.circular(10),
          onTap: () => setState(() => _section = it.section!),
          child: Tooltip(
            message: _expanded ? '' : _label(s, it.section!),
            child: Padding(
              padding: EdgeInsets.symmetric(horizontal: _expanded ? 12 : 0, vertical: 9),
              child: Row(
                children: [
                  Icon(
                    sel ? it.iconSelected : it.icon,
                    size: 21,
                    color: sel ? cs.primary : cs.onSurfaceVariant,
                  ),
                  if (_expanded) ...[
                    const SizedBox(width: 11),
                    Expanded(
                      child: Text(
                        _label(s, it.section!),
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(
                          fontSize: 13.5,
                          fontWeight: sel ? FontWeight.w700 : FontWeight.w500,
                          color: sel ? cs.primary : cs.onSurface,
                        ),
                      ),
                    ),
                    if (it.count != null && it.count! > 0)
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
                        decoration: BoxDecoration(
                          color: cs.secondaryContainer,
                          borderRadius: BorderRadius.circular(99),
                        ),
                        child: Text(
                          Fmt.percent(context, 0).endsWith('%')
                              ? '${it.count}'
                              : '${it.count}',
                          style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: cs.onSecondaryContainer),
                        ),
                      ),
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  String _label(S s, NavSection sec) => switch (sec) {
        NavSection.overview => s.overview,
        NavSection.all => s.allDownloads,
        NavSection.active => s.activeDownloads,
        NavSection.queued => s.queuedDownloads,
        NavSection.completed => s.completedDownloads,
        NavSection.failed => s.failedDownloads,
        NavSection.media => s.mediaDownloads,
        NavSection.queues => s.queues,
        NavSection.scheduler => s.scheduler,
        NavSection.browser => s.browserIntegration,
        NavSection.history => s.history,
        NavSection.diagnostics => s.diagnostics,
        NavSection.settings => s.settings,
      };

  Widget _toolbar(S s, dynamic status) {
    final connected = ref.watch(prov.engineProvider) != null;
    final tasks = ref.watch(prov.tasksProvider).value ?? <TaskUi>[];
    final globalSpeed = tasks.fold<int>(0, (a, t) => a + (t.isRunning ? t.speedBps : 0));

    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 12, 20, 12),
      child: Row(
        children: [
          IconButton(
            tooltip: _expanded ? '' : '',
            onPressed: () => setState(() => _expanded = !_expanded),
            icon: const Icon(Icons.menu_rounded, size: 20),
          ),
          const SizedBox(width: 4),
          Expanded(
            child: TextField(
              onChanged: (v) => setState(() => _search = v),
              decoration: InputDecoration(
                hintText: s.search,
                prefixIcon: const Icon(Icons.search_rounded, size: 20),
                isDense: true,
                constraints: const BoxConstraints(maxHeight: 42),
              ),
            ),
          ),
          const SizedBox(width: 12),
          FilledButton.icon(
            onPressed: () => showAddDownloadDialog(context),
            icon: const Icon(Icons.add_rounded, size: 19),
            label: Text(s.addDownload),
          ),
          const SizedBox(width: 10),
          _connectionChip(s, connected, globalSpeed),
        ],
      ),
    );
  }

  Widget _connectionChip(S s, bool connected, int globalSpeed) {
    final cs = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        color: connected ? NovaColors.success.withOpacity(0.12) : NovaColors.danger.withOpacity(0.12),
        borderRadius: BorderRadius.circular(99),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Container(
            width: 8,
            height: 8,
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: connected ? NovaColors.success : NovaColors.danger,
              boxShadow: [BoxShadow(color: (connected ? NovaColors.success : NovaColors.danger).withOpacity(0.5), blurRadius: 6, spreadRadius: 0)],
            ),
          ),
          const SizedBox(width: 8),
          Text(
            connected
                ? (globalSpeed > 0 ? Fmt.speed(context, globalSpeed) : s.engineConnected)
                : s.reconnecting,
            style: TextStyle(
              fontSize: 12.5,
              fontWeight: FontWeight.w700,
              color: connected ? NovaColors.success : NovaColors.danger,
            ),
          ),
        ],
      ),
    );
  }

  Widget _page(S s) {
    return switch (_section) {
      NavSection.overview => OverviewPage(search: _search),
      NavSection.queues => const QueuesPage(),
      NavSection.scheduler => const SchedulerPage(),
      NavSection.browser => const BrowserIntegrationPage(),
      NavSection.history => HistoryPage(search: _search),
      NavSection.diagnostics => const DiagnosticsPage(),
      NavSection.settings => const SettingsPage(),
      _ => DownloadsPage(
          filter: switch (_section) {
            NavSection.active => DownloadFilter.active,
            NavSection.queued => DownloadFilter.queued,
            NavSection.completed => DownloadFilter.completed,
            NavSection.failed => DownloadFilter.failed,
            NavSection.media => DownloadFilter.media,
            _ => DownloadFilter.all,
          },
          search: _search,
        ),
    };
  }
}

class _NavItem {
  final NavSection? section;
  final IconData? icon;
  final IconData? iconSelected;
  final String label;
  final int? count;
  const _NavItem(this.section, this.icon, this.iconSelected, this.label, this.count);
}
