import 'dart:io';
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

        // ---- IDM parity: proxy ------------------------------------------------
        _ProxySection(settings: settings, engine: engine),

        // ---- IDM parity: antivirus -------------------------------------------
        _section(context, Icons.health_and_safety_outlined, s.antivirusSection, [
          SwitchListTile(
            value: settings['av.enabled'] == 'true',
            onChanged: (v) => engine?.call('set_settings', {
              'values': {'av.enabled': '$v'}
            }),
            title: Text(s.antivirusEnabled, style: const TextStyle(fontSize: 13.5)),
            dense: true,
            contentPadding: EdgeInsets.zero,
          ),
          _settingField(
            context, engine, s.antivirusCommand, 'av.command',
            settings['av.command'] ?? 'MpCmdRun.exe -Scan -ScanType 3 -File {file}',
          ),
        ]),

        // ---- IDM parity: file types + temp dir + post action -----------------
        _section(context, Icons.file_present_outlined, s.fileTypes, [
          _settingField(context, engine, s.fileTypes, 'intercept.file_types',
              settings['intercept.file_types'] ?? '', hint: s.fileTypesHint),
          const SizedBox(height: 8),
          _settingField(context, engine, s.tempDir, 'engine.temp_dir',
              settings['engine.temp_dir'] ?? ''),
          const SizedBox(height: 8),
          _postActionDropdown(context, engine, settings),
        ]),

        // ---- IDM parity: import / export --------------------------------------
        _section(context, Icons.import_export_rounded, s.importExport, [
          Row(children: [
            OutlinedButton.icon(
              icon: const Icon(Icons.file_download_outlined, size: 18),
              label: Text(s.exportList),
              onPressed: () async {
                final resp = await engine?.call('export_tasks');
                if (resp == null || !context.mounted) return;
                final json = resp['json'] as String? ?? '';
                final file = await FilePicker.saveText(context, 'nova-tasks.json', json);
                if (file != null && context.mounted) {
                  ScaffoldMessenger.of(context)
                      .showSnackBar(SnackBar(content: Text('${s.exportList}: ${file.path}')));
                }
              },
            ),
            const SizedBox(width: 12),
            OutlinedButton.icon(
              icon: const Icon(Icons.file_upload_outlined, size: 18),
              label: Text(s.importList),
              onPressed: () async {
                final json = await FilePicker.pickText(context, 'nova-tasks.json');
                if (json == null) return;
                await engine?.call('import_tasks', {'json': json, 'auto_start': false});
                if (context.mounted) {
                  ScaffoldMessenger.of(context)
                      .showSnackBar(SnackBar(content: Text('${s.importList}: OK')));
                }
              },
            ),
          ]),
        ]),
      ],
    );
  }

  Widget _settingField(BuildContext context, dynamic engine, String label, String key, String value,
      {String? hint}) {
    final ctrl = TextEditingController(text: value);
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: TextField(
        controller: ctrl,
        decoration: InputDecoration(
          labelText: label,
          hintText: hint,
          border: const OutlineInputBorder(),
          isDense: true,
        ),
        onSubmitted: (v) =>
            engine?.call('set_settings', {'values': {key: v}}),
      ),
    );
  }

  Widget _postActionDropdown(BuildContext context, dynamic engine, Map<String, String> settings) {
    final s = S.of(context);
    final current = settings['engine.post_action'] ?? '';
    return DropdownButtonFormField<String>(
      initialValue: current.isEmpty ? '' : current,
      decoration: InputDecoration(
        labelText: s.postAction,
        border: const OutlineInputBorder(),
        isDense: true,
      ),
      items: [
        DropdownMenuItem(value: '', child: Text(s.postActionNone)),
        DropdownMenuItem(value: 'exit', child: Text(s.postActionExit)),
        DropdownMenuItem(value: 'shutdown', child: Text(s.postActionShutdown)),
        DropdownMenuItem(value: 'hibernate', child: Text(s.postActionHibernate)),
        DropdownMenuItem(value: 'sleep', child: Text(s.postActionSleep)),
        DropdownMenuItem(value: 'logoff', child: Text(s.postActionLogoff)),
      ],
      onChanged: (v) =>
          engine?.call('set_settings', {'values': {'engine.post_action': v ?? ''}}),
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

/// Proxy section (IDM parity): http/https/socks4/4a/5 + auth + bypass list.
class _ProxySection extends StatefulWidget {
  const _ProxySection({required this.settings, required this.engine});
  final Map<String, String> settings;
  final dynamic engine;

  @override
  State<_ProxySection> createState() => _ProxySectionState();
}

class _ProxySectionState extends State<_ProxySection> {
  late String _scheme = widget.settings['proxy.scheme'] ?? 'http';
  late final _host = TextEditingController(text: widget.settings['proxy.host'] ?? '');
  late final _port = TextEditingController(text: widget.settings['proxy.port'] ?? '');
  late final _user = TextEditingController(text: widget.settings['proxy.user'] ?? '');
  late final _pass = TextEditingController(text: widget.settings['proxy.pass'] ?? '');
  late final _bypass = TextEditingController(text: widget.settings['proxy.bypass'] ?? 'localhost');

  @override
  void dispose() {
    for (final c in [_host, _port, _user, _pass, _bypass]) {
      c.dispose();
    }
    super.dispose();
  }

  void _save() {
    widget.engine?.call('set_settings', {
      'values': {
        'proxy.enabled': 'true',
        'proxy.scheme': _scheme,
        'proxy.host': _host.text.trim(),
        'proxy.port': _port.text.trim(),
        'proxy.user': _user.text.trim(),
        'proxy.pass': _pass.text,
        'proxy.bypass': _bypass.text.trim(),
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final s = S.of(context);
    final enabled = widget.settings['proxy.enabled'] == 'true';
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(children: [
                Icon(Icons.vpn_lock_outlined, size: 18, color: Theme.of(context).colorScheme.primary),
                const SizedBox(width: 10),
                Text(s.proxySection, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 14)),
                const Spacer(),
                Switch(
                  value: enabled,
                  onChanged: (v) => widget.engine?.call('set_settings', {
                    'values': {'proxy.enabled': '$v'}
                  }),
                ),
              ]),
              const SizedBox(height: 10),
              DropdownButtonFormField<String>(
                initialValue: _scheme,
                decoration: InputDecoration(labelText: s.proxyType, border: const OutlineInputBorder(), isDense: true),
                items: const [
                  DropdownMenuItem(value: 'http', child: Text('HTTP')),
                  DropdownMenuItem(value: 'https', child: Text('HTTPS')),
                  DropdownMenuItem(value: 'socks4', child: Text('SOCKS4')),
                  DropdownMenuItem(value: 'socks4a', child: Text('SOCKS4a')),
                  DropdownMenuItem(value: 'socks5', child: Text('SOCKS5')),
                ],
                onChanged: (v) => setState(() => _scheme = v ?? 'http'),
              ),
              const SizedBox(height: 8),
              Row(children: [
                Expanded(flex: 3, child: TextField(controller: _host, decoration: InputDecoration(labelText: s.proxyHost, border: const OutlineInputBorder(), isDense: true))),
                const SizedBox(width: 8),
                Expanded(flex: 1, child: TextField(controller: _port, decoration: InputDecoration(labelText: s.proxyPort, border: const OutlineInputBorder(), isDense: true))),
              ]),
              const SizedBox(height: 8),
              Row(children: [
                Expanded(child: TextField(controller: _user, decoration: InputDecoration(labelText: s.proxyUser, border: const OutlineInputBorder(), isDense: true))),
                const SizedBox(width: 8),
                Expanded(child: TextField(controller: _pass, obscureText: true, decoration: InputDecoration(labelText: s.proxyPassword, border: const OutlineInputBorder(), isDense: true))),
              ]),
              const SizedBox(height: 8),
              TextField(controller: _bypass, decoration: InputDecoration(labelText: s.proxyBypass, border: const OutlineInputBorder(), isDense: true)),
              const SizedBox(height: 10),
              Align(
                alignment: AlignmentDirectional.centerEnd,
                child: FilledButton(onPressed: _save, child: Text(s.save)),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Minimal save/pick text helpers without file-selector packages
/// (desktop native pickers are not wired yet — files land in ~/Downloads).
class FilePicker {
  static Future<File?> saveText(BuildContext context, String suggestedName, String content) async {
    try {
      final dir = Platform.isWindows
          ? '${Platform.environment['USERPROFILE']}\\Downloads'
          : '${Platform.environment['HOME']}/Downloads';
      final file = File('$dir${Platform.pathSeparator}$suggestedName');
      await file.writeAsString(content);
      return file;
    } catch (_) {
      return null;
    }
  }

  static Future<String?> pickText(BuildContext context, String pattern) async {
    try {
      final dir = Platform.isWindows
          ? '${Platform.environment['USERPROFILE']}\\Downloads'
          : '${Platform.environment['HOME']}/Downloads';
      final file = File('$dir${Platform.pathSeparator}$pattern');
      if (!await file.exists()) return null;
      return await file.readAsString();
    } catch (_) {
      return null;
    }
  }
}
