import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app.dart';
import 'core/models.dart';
import 'core/strings.dart';
import 'core/theme.dart';
import 'providers/engine.dart' as prov;

/// IDM parity v1.2.0 — progress mini-window. A compact always-on-top view of the
/// active downloads with global speed, pause/resume all and a back-to-main button.
/// Spawned as a second process with the `--mini` argument (see ShellPage toolbar).
class MiniApp extends ConsumerWidget {
  const MiniApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final prefs = ref.watch(_miniPrefsProvider).value ?? const _MiniPrefs(ThemeMode.dark, Locale('en'));
    return MaterialApp(
      title: 'Nova — mini',
      debugShowCheckedModeBanner: false,
      themeMode: prefs.themeMode,
      theme: NovaTheme.light(),
      darkTheme: NovaTheme.dark(),
      locale: prefs.locale,
      supportedLocales: const [Locale('en'), Locale('fa')],
      localizationsDelegates: const [
        SDelegate(),
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      home: const MiniHomePage(),
    );
  }

  static Future<void> boot() async {
    WidgetsFlutterBinding.ensureInitialized();
    await _setAlwaysOnTop();
    runApp(const ProviderScope(child: MiniApp()));
  }

  /// Native always-on-top + compact size via the "nova/window" platform channel.
  static Future<void> _setAlwaysOnTop() async {
    try {
      final channel = const MethodChannel('nova/window');
      await channel.invokeMethod('setAlwaysOnTop', true);
      await channel.invokeMethod('setSize', {'width': 340.0, 'height': 460.0});
    } catch (_) {/* runner without the channel: plain window */}
  }
}

/// Theme + language mirror of the main window (reads engine settings; falls back
/// to dark so the mini window still looks right when the engine is offline).
class _MiniPrefs {
  final ThemeMode themeMode;
  final Locale locale;
  const _MiniPrefs(this.themeMode, this.locale);
}

final _miniPrefsProvider = FutureProvider<_MiniPrefs>((ref) async {
  final engine = ref.watch(prov.engineProvider);
  var mode = ThemeMode.dark;
  var locale = Platform.environment['LANG']?.startsWith('fa') ?? false
      ? const Locale('fa')
      : const Locale('en');
  try {
    if (engine != null) {
      final resp = await engine.call('get_settings');
      final values = (resp['values'] as Map).cast<String, String>();
      mode = switch (values['ui.theme']) {
        'light' => ThemeMode.light,
        'system' => ThemeMode.system,
        _ => ThemeMode.dark,
      };
      if (values['ui.language'] == 'fa') locale = const Locale('fa');
      if (values['ui.language'] == 'en') locale = const Locale('en');
    }
  } catch (_) {/* defaults */}
  return _MiniPrefs(mode, locale);
});

class MiniHomePage extends ConsumerWidget {
  const MiniHomePage({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final cs = Theme.of(context).colorScheme;
    final tasks = ref.watch(prov.tasksProvider).value ?? <TaskUi>[];
    final active = tasks.where((t) => t.isRunning || t.state == 'queued').toList();
    final speed = tasks.fold<int>(0, (a, t) => a + (t.isRunning ? t.speedBps : 0));

    return Scaffold(
      backgroundColor: cs.surface,
      body: Column(
        children: [
          // header: global speed + open-main + close
          Padding(
            padding: const EdgeInsets.fromLTRB(14, 12, 8, 10),
            child: Row(
              children: [
                Icon(Icons.download_rounded, size: 18, color: cs.primary),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    Fmt.speed(context, speed),
                    style: Theme.of(context)
                        .textTheme
                        .titleLarge!
                        .copyWith(fontWeight: FontWeight.w900, fontFeatures: const [FontFeature.tabularFigures()]),
                  ),
                ),
                IconButton(
                  tooltip: s.pauseAll,
                  iconSize: 18,
                  visualDensity: VisualDensity.compact,
                  onPressed: () => ref.read(prov.tasksProvider.notifier).pauseAll(),
                  icon: const Icon(Icons.pause_circle_outline_rounded),
                ),
                IconButton(
                  tooltip: s.resumeAll,
                  iconSize: 18,
                  visualDensity: VisualDensity.compact,
                  onPressed: () => ref.read(prov.tasksProvider.notifier).resumeAll(),
                  icon: const Icon(Icons.play_circle_outline_rounded),
                ),
                IconButton(
                  tooltip: s.close,
                  iconSize: 18,
                  visualDensity: VisualDensity.compact,
                  onPressed: () => exit(0),
                  icon: const Icon(Icons.close_rounded),
                ),
              ],
            ),
          ),
          Divider(height: 1, color: cs.outlineVariant.withOpacity(0.4)),
          // active downloads
          Expanded(
            child: active.isEmpty
                ? Center(
                    child: Padding(
                      padding: const EdgeInsets.all(18),
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Icon(Icons.check_circle_outline_rounded,
                              size: 42, color: Theme.of(context).colorScheme.outline),
                          const SizedBox(height: 10),
                          Text(s.noDownloads, style: Theme.of(context).textTheme.bodySmall),
                        ],
                      ),
                    ),
                  )
                : ListView.builder(
                    padding: const EdgeInsets.symmetric(vertical: 8, horizontal: 12),
                    itemCount: active.length,
                    itemBuilder: (context, i) => _MiniTaskRow(task: active[i]),
                  ),
          ),
        ],
      ),
    );
  }
}

class _MiniTaskRow extends ConsumerWidget {
  final TaskUi task;
  const _MiniTaskRow({required this.task});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final cs = Theme.of(context).colorScheme;
    final color = NovaColors.stateColor(task.state, Theme.of(context).brightness);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
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
                  style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 12.5),
                ),
              ),
              if (task.isRunning && task.speedBps > 0)
                Padding(
                  padding: const EdgeInsetsDirectional.only(start: 6),
                  child: Text(Fmt.speed(context, task.speedBps),
                      style: TextStyle(fontSize: 10.5, color: color, fontWeight: FontWeight.w700)),
                ),
              const SizedBox(width: 4),
              _action(context, ref, s),
            ],
          ),
          const SizedBox(height: 5),
          ClipRRect(
            borderRadius: BorderRadius.circular(3),
            child: LinearProgressIndicator(
              value: task.progress == 0 && task.isRunning ? null : task.progress,
              minHeight: 4,
              backgroundColor: cs.surfaceContainerHighest.withOpacity(0.55),
              color: color,
            ),
          ),
        ],
      ),
    );
  }

  Widget _action(BuildContext context, WidgetRef ref, S s) {
    if (task.state == 'queued') {
      return InkWell(
        onTap: () => ref.read(prov.tasksProvider.notifier).pause(task.taskKey),
        child: const Padding(
          padding: EdgeInsets.all(4),
          child: Icon(Icons.pause_rounded, size: 15),
        ),
      );
    }
    if (task.isRunning) {
      return InkWell(
        onTap: () => ref.read(prov.tasksProvider.notifier).pause(task.taskKey),
        child: const Padding(
          padding: EdgeInsets.all(4),
          child: Icon(Icons.pause_rounded, size: 15),
        ),
      );
    }
    return InkWell(
      onTap: () => ref.read(prov.tasksProvider.notifier).resume(task.taskKey),
      child: const Padding(
        padding: EdgeInsets.all(4),
        child: Icon(Icons.play_arrow_rounded, size: 15),
      ),
    );
  }
}
