import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../services/app_model.dart';
import '../widgets/task_card.dart';
import 'add_url_dialog.dart';
import 'settings_page.dart';
import 'scheduler_page.dart';
import 'video_preview_player.dart';

/// Main shell: navigation rail with categories + task list + toolbar.
class MainPage extends StatefulWidget {
  const MainPage({super.key});

  @override
  State<MainPage> createState() => _MainPageState();
}

class _MainPageState extends State<MainPage> {
  int _navIndex = 0;
  double _speedLimitKb = 0; // 0 = unlimited

  static const _cats = [
    ('all', Icons.download_rounded, 'All'),
    ('video', Icons.play_circle_outline_rounded, 'Video'),
    ('music', Icons.music_note_outlined, 'Music'),
    ('documents', Icons.description_outlined, 'Docs'),
    ('compressed', Icons.folder_zip_outlined, 'Zips'),
    ('programs', Icons.apps_outlined, 'Apps'),
  ];

  @override
  Widget build(BuildContext context) {
    final model = context.watch<AppModel>();
    final l10n = Localizations.localeOf(context).languageCode == 'fa';

    return Scaffold(
      appBar: AppBar(
        title: Text(l10n ? 'مدیریت دانلود نووا' : 'Nova Download Manager'),
        actions: [
          // engine status pill
          _EngineStatusPill(online: model.engineOnline),
          const SizedBox(width: 8),
          // speed limiter menu
          PopupMenuButton<double>(
            icon: const Icon(Icons.speed_rounded),
            tooltip: l10n ? 'محدودکننده سرعت' : 'Speed limiter',
            onSelected: (kb) {
              setState(() => _speedLimitKb = kb);
              context.read<AppModel>().setGlobalSpeedLimit((kb * 1024).round());
            },
            itemBuilder: (_) => [
              CheckedPopupMenuItem(value: 0, checked: _speedLimitKb == 0, child: const Text('Unlimited')),
              ...[128, 256, 512, 1024, 2048, 4096]
                  .map((kb) => CheckedPopupMenuItem(
                        value: kb.toDouble(),
                        checked: _speedLimitKb == kb,
                        child: Text(kb >= 1024 ? '${kb ~/ 1024} MB/s' : '$kb KB/s'),
                      )),
            ],
          ),
          IconButton(
            icon: const Icon(Icons.schedule_rounded),
            tooltip: l10n ? 'زمان‌بند' : 'Scheduler',
            onPressed: () => _push(const SchedulerPage()),
          ),
          IconButton(
            icon: const Icon(Icons.settings_outlined),
            tooltip: l10n ? 'تنظیمات' : 'Settings',
            onPressed: () => _push(const SettingsPage()),
          ),
        ],
      ),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () => showAddUrlDialog(context),
        icon: const Icon(Icons.add_link_rounded),
        label: Text(l10n ? 'افزودن لینک' : 'Add URL'),
      ),
      body: Row(
        children: [
          // ---- navigation rail ----
          NavigationRail(
            selectedIndex: _navIndex,
            onDestinationSelected: (i) {
              setState(() => _navIndex = i);
              model.setCategoryFilter(_cats[i].$1);
            },
            labelType: NavigationRailLabelType.all,
            leading: _SpeedGraph(model: model),
            destinations: [
              for (final c in _cats)
                NavigationRailDestination(
                  icon: Icon(c.$2),
                  label: Text(c.$3),
                ),
            ],
          ),
          const VerticalDivider(width: 1),
          // ---- task list ----
          Expanded(
            child: model.filteredTasks.isEmpty
                ? Center(
                    child: Text(
                      l10n
                          ? 'هنوز چیزی اینجا نیست.\nیک لینک اضافه کنید!'
                          : 'Nothing here yet.\nAdd a URL or browse the web — Nova catches it.',
                      textAlign: TextAlign.center,
                      style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                            color: Theme.of(context).disabledColor,
                          ),
                    ),
                  )
                : ListView.builder(
                    padding: const EdgeInsets.symmetric(vertical: 4),
                    itemCount: model.filteredTasks.length,
                    itemBuilder: (context, i) =>
                        TaskCard(task: model.filteredTasks[i]),
                  ),
          ),
        ],
      ),
    );
  }

  void _push(Widget page) =>
      Navigator.push(context, MaterialPageRoute(builder: (_) => page));
}

class _EngineStatusPill extends StatelessWidget {
  final bool online;
  const _EngineStatusPill({required this.online});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
      decoration: BoxDecoration(
        color: online
            ? Colors.green.withValues(alpha: .15)
            : Colors.red.withValues(alpha: .15),
        borderRadius: BorderRadius.circular(999),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.circle, size: 8, color: online ? Colors.green : Colors.red),
          const SizedBox(width: 6),
          Text(
            online ? 'Engine online' : 'Engine offline',
            style: TextStyle(
              fontSize: 12,
              color: online ? Colors.green.shade700 : Colors.red.shade700,
            ),
          ),
        ],
      ),
    );
  }
}

/// Small live speed graph in the rail (last 2 minutes).
class _SpeedGraph extends StatelessWidget {
  final AppModel model;
  const _SpeedGraph({required this.model});

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 72,
      height: 56,
      padding: const EdgeInsets.all(6),
      child: CustomPaint(
        painter: _SpeedPainter(model.speedHistory),
        size: const Size(60, 44),
      ),
    );
  }
}

class _SpeedPainter extends CustomPainter {
  final List<double> history;
  _SpeedPainter(this.history);

  @override
  void paint(Canvas canvas, Size size) {
    if (history.length < 2) return;
    final maxV = history.reduce((a, b) => a > b ? a : b).clamp(1.0, double.infinity);
    final paint = Paint()
      ..color = const Color(0xFF2563EB)
      ..strokeWidth = 1.5
      ..style = PaintingStyle.stroke;
    final path = Path();
    for (var i = 0; i < history.length; i++) {
      final x = i / (history.length - 1) * size.width;
      final y = size.height - (history[i] / maxV) * size.height;
      if (i == 0) {
        path.moveTo(x, y);
      } else {
        path.lineTo(x, y);
      }
    }
    canvas.drawPath(path, paint);
  }

  @override
  bool shouldRepaint(covariant _SpeedPainter oldDelegate) =>
      oldDelegate.history != history;
}
