import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../app.dart';
import '../core/strings.dart';
import '../core/theme.dart';
import '../providers/engine.dart' as prov;

/// Settings — general / appearance / behavior / video detection / privacy.
class SettingsPage extends ConsumerWidget {
  const SettingsPage({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final prefs = ref.watch(uiPrefsProvider);
    final settings = ref.watch(prov.settingsProvider);
    final engine = ref.watch(prov.engineProvider);

    return ListView(
      padding: const EdgeInsets.all(20),
      children: [
        Text(s.settings, style: Theme.of(context).textTheme.titleLarge!.copyWith(fontWeight: FontWeight.w800)),
        const SizedBox(height: 16),

        _section(context, Icons.palette_outlined, s.appearance, [
          Row(
            children: [
              Text(s.theme, style: const TextStyle(fontSize: 13.5, fontWeight: FontWeight.w600)),
              const Spacer(),
              SegmentedButton<ThemeMode>(
                segments: [
                  ButtonSegment(value: ThemeMode.light, label: Text(s.themeLight)),
                  ButtonSegment(value: ThemeMode.dark, label: Text(s.themeDark)),
                  ButtonSegment(value: ThemeMode.system, label: Text(s.themeSystem)),
                ],
                selected: {prefs.themeMode},
                onSelectionChanged: (sel) => ref.read(uiPrefsProvider.notifier).setTheme(sel.first),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              Text(s.language, style: const TextStyle(fontSize: 13.5, fontWeight: FontWeight.w600)),
              const Spacer(),
              SegmentedButton<String>(
                segments: const [
                  ButtonSegment(value: 'en', label: Text('English')),
                  ButtonSegment(value: 'fa', label: Text('فارسی')),
                ],
                selected: {prefs.locale.languageCode},
                onSelectionChanged: (sel) =>
                    ref.read(uiPrefsProvider.notifier).setLocale(Locale(sel.first)),
              ),
            ],
          ),
        ]),

        _section(context, Icons.tune_rounded, s.general, [
          Row(
            children: [
              Expanded(
                child: Text(s.maxConcurrent, style: const TextStyle(fontSize: 13.5)),
              ),
              SegmentedButton<int>(
                segments: const [
                  ButtonSegment(value: 1, label: Text('1')),
                  ButtonSegment(value: 3, label: Text('3')),
                  ButtonSegment(value: 5, label: Text('5')),
                  ButtonSegment(value: 8, label: Text('8')),
                ],
                selected: {int.tryParse(settings['scheduler.max_concurrent'] ?? '') ?? 3},
                onSelectionChanged: (sel) => engine?.call('set_settings', {
                  'values': {'scheduler.max_concurrent': '${sel.first}'}
                }),
              ),
            ],
          ),
          const SizedBox(height: 6),
          SwitchListTile(
            value: settings['engine.resume_on_start'] != 'false',
            onChanged: (v) => engine?.call('set_settings', {
              'values': {'engine.resume_on_start': '$v'}
            }),
            title: Text(s.resumeOnStart, style: const TextStyle(fontSize: 13.5)),
            dense: true,
            contentPadding: EdgeInsets.zero,
          ),
          SwitchListTile(
            value: settings['ui.minimize_to_tray'] == 'true',
            onChanged: (v) => engine?.call('set_settings', {
              'values': {'ui.minimize_to_tray': '$v'}
            }),
            title: Text(s.minimizeToTray, style: const TextStyle(fontSize: 13.5)),
            dense: true,
            contentPadding: EdgeInsets.zero,
          ),
        ]),

        _section(context, Icons.videocam_outlined, s.videoDetection, [
          SwitchListTile(
            value: settings['video.detect'] != 'false',
            onChanged: (v) => engine?.call('set_settings', {
              'values': {'video.detect': '$v'}
            }),
            title: Text('Enable video detection', style: const TextStyle(fontSize: 13.5)),
            dense: true,
            contentPadding: EdgeInsets.zero,
          ),
          SwitchListTile(
            value: settings['video.show_button_hover'] != 'false',
            onChanged: (v) => engine?.call('set_settings', {
              'values': {'video.show_button_hover': '$v'}
            }),
            title: Text(s.showButtonHover, style: const TextStyle(fontSize: 13.5)),
            dense: true,
            contentPadding: EdgeInsets.zero,
          ),
        ]),

        _section(context, Icons.privacy_tip_outlined, s.privacy, [
          SwitchListTile(
            value: settings['history.save'] != 'false',
            onChanged: (v) => engine?.call('set_settings', {
              'values': {'history.save': '$v'}
            }),
            title: Text(s.saveHistory, style: const TextStyle(fontSize: 13.5)),
            dense: true,
            contentPadding: EdgeInsets.zero,
          ),
          Row(
            children: [
              Expanded(child: Text(s.historyRetention, style: const TextStyle(fontSize: 13.5))),
              SizedBox(
                width: 120,
                child: DropdownButtonFormField<String>(
                  initialValue: settings['history.retention_days'] ?? '90',
                  decoration: InputDecoration(suffixText: s.days, isDense: true),
                  items: ['7', '30', '90', '365', '0']
                      .map((v) => DropdownMenuItem(value: v, child: Text(v == '0' ? '∞' : v)))
                      .toList(),
                  onChanged: (v) => engine?.call('set_settings', {
                    'values': {'history.retention_days': v ?? '90'}
                  }),
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          Row(
            children: [
              Icon(Icons.lock_outline_rounded, size: 15, color: NovaColors.completed),
              const SizedBox(width: 8),
              Expanded(child: Text(s.noSensitiveLogging, style: const TextStyle(fontSize: 12.5))),
            ],
          ),
        ]),
      ],
    );
  }

  Widget _section(BuildContext context, IconData icon, String title, List<Widget> children) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(icon, size: 18, color: Theme.of(context).colorScheme.primary),
                  const SizedBox(width: 10),
                  Text(title, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 14)),
                ],
              ),
              const SizedBox(height: 14),
              ...children,
            ],
          ),
        ),
      ),
    );
  }
}
