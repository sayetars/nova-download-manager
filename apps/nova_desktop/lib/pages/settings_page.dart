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

        // ---- IDM parity: site logins vault ------------------------------------
        _LoginsSection(engine: engine),

        // ---- IDM parity: categories editor ------------------------------------
        _CategoriesSection(engine: engine),

        // ---- IDM parity: byte quota -------------------------------------------
        _QuotaSection(settings: settings, engine: engine),

        // ---- check for updates --------------------------------------------------
        _section(context, Icons.system_update_alt_rounded, s.updateNow, [
          Row(children: [
            OutlinedButton.icon(
              icon: const Icon(Icons.refresh_rounded, size: 18),
              label: Text(s.updateNow),
              onPressed: () async {
                try {
                  final resp = await engine?.call('check_update');
                  if (!context.mounted) return;
                  final code = resp?['status_code'];
                  final body = (resp?['body'] as String? ?? '').trim();
                  final msg = body.length > 300 ? '${body.substring(0, 300)}…' : body;
                  ScaffoldMessenger.of(context).showSnackBar(SnackBar(
                      content: Text(code == null ? s.updatedCheck : 'HTTP $code — $msg')));
                } catch (e) {
                  if (context.mounted) {
                    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('$e')));
                  }
                }
              },
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Text('${s.updatedCheck} (update.url)',
                  style: TextStyle(fontSize: 12, color: Theme.of(context).colorScheme.onSurfaceVariant)),
            ),
          ]),
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

/// IDM parity — site logins vault: stored credentials used automatically on
/// 401/407 (engine-side retry) and by FTP. Passwords never travel back to the UI.
class _LoginsSection extends StatefulWidget {
  const _LoginsSection({required this.engine});
  final dynamic engine;

  @override
  State<_LoginsSection> createState() => _LoginsSectionState();
}

class _LoginsSectionState extends State<_LoginsSection> {
  List<Map<String, dynamic>> _logins = [];
  bool _loaded = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final resp = await widget.engine?.call('list_logins');
      if (mounted) {
        setState(() {
          _logins = (resp?['logins'] as List? ?? [])
              .map((e) => Map<String, dynamic>.from(e as Map))
              .toList();
          _loaded = true;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _loaded = true);
    }
  }

  Future<void> _addOrEdit([Map<String, dynamic>? existing]) async {
    final s = S.of(context);
    final host = TextEditingController(text: existing?['host']?.toString() ?? '');
    final user = TextEditingController(text: existing?['user_name']?.toString() ?? '');
    final pass = TextEditingController();
    String scheme = existing?['scheme']?.toString() ?? 'basic';
    final ok = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(s.loginsVault, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
        content: SizedBox(
          width: 420,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                  controller: host,
                  decoration: InputDecoration(
                      labelText: s.proxyHost, border: const OutlineInputBorder(), isDense: true)),
              const SizedBox(height: 10),
              TextField(
                  controller: user,
                  decoration: InputDecoration(
                      labelText: s.userName, border: const OutlineInputBorder(), isDense: true)),
              const SizedBox(height: 10),
              TextField(
                  controller: pass,
                  obscureText: true,
                  decoration: InputDecoration(
                      labelText: s.password, border: const OutlineInputBorder(), isDense: true)),
              const SizedBox(height: 10),
              DropdownButtonFormField<String>(
                initialValue: scheme,
                decoration: InputDecoration(
                    labelText: s.authentication, border: const OutlineInputBorder(), isDense: true),
                items: const [
                  DropdownMenuItem(value: 'basic', child: Text('Basic')),
                  DropdownMenuItem(value: 'digest', child: Text('Digest')),
                  DropdownMenuItem(value: 'ntlm', child: Text('NTLM')),
                  DropdownMenuItem(value: 'negotiate', child: Text('Kerberos / Negotiate')),
                ],
                onChanged: (v) => scheme = v ?? 'basic',
              ),
            ],
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: Text(s.close)),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: Text(s.save)),
        ],
      ),
    );
    if (ok != true) return;
    await widget.engine?.call('save_login', {
      'host': host.text.trim(),
      'realm': '',
      'user_name': user.text.trim(),
      'password': pass.text,
      'scheme': scheme,
    });
    await _load();
  }

  @override
  Widget build(BuildContext context) {
    final s = S.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(children: [
                Icon(Icons.key_rounded,
                    size: 18, color: Theme.of(context).colorScheme.primary),
                const SizedBox(width: 10),
                Text(s.loginsVault,
                    style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 14)),
                const Spacer(),
                IconButton(
                  tooltip: s.addCategory,
                  icon: const Icon(Icons.add_rounded, size: 20),
                  onPressed: () => _addOrEdit(),
                ),
              ]),
              if (_loaded && _logins.isEmpty)
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Text(s.none,
                      style: TextStyle(
                          fontSize: 12.5,
                          color: Theme.of(context).colorScheme.onSurfaceVariant)),
                ),
              for (final l in _logins)
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Row(children: [
                    Icon(Icons.language_rounded,
                        size: 16, color: Theme.of(context).colorScheme.onSurfaceVariant),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Text('${l['host']} — ${l['user_name']} (${l['scheme']})',
                          style: const TextStyle(fontSize: 13)),
                    ),
                    IconButton(
                      icon: const Icon(Icons.edit_outlined, size: 16),
                      onPressed: () => _addOrEdit(l),
                    ),
                    IconButton(
                      icon: const Icon(Icons.delete_outline_rounded, size: 16),
                      onPressed: () async {
                        await widget.engine?.call('delete_login', {'id': l['id']});
                        await _load();
                      },
                    ),
                  ]),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// IDM parity — categories editor: custom categories with URL/filename patterns
/// and a per-category destination directory.
class _CategoriesSection extends StatefulWidget {
  const _CategoriesSection({required this.engine});
  final dynamic engine;

  @override
  State<_CategoriesSection> createState() => _CategoriesSectionState();
}

class _CategoriesSectionState extends State<_CategoriesSection> {
  List<Map<String, dynamic>> _cats = [];
  bool _loaded = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final resp = await widget.engine?.call('list_categories');
      if (mounted) {
        setState(() {
          _cats = (resp?['categories'] as List? ?? [])
              .map((e) => Map<String, dynamic>.from(e as Map))
              .toList();
          _loaded = true;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _loaded = true);
    }
  }

  Future<void> _addOrEdit([Map<String, dynamic>? existing]) async {
    final s = S.of(context);
    final name = TextEditingController(text: existing?['name']?.toString() ?? '');
    final patterns = TextEditingController(text: existing?['patterns']?.toString() ?? '');
    final dir = TextEditingController(text: existing?['destination_dir']?.toString() ?? '');
    final ok = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(s.addCategory, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
        content: SizedBox(
          width: 420,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                  controller: name,
                  decoration: InputDecoration(
                      labelText: s.category, border: const OutlineInputBorder(), isDense: true)),
              const SizedBox(height: 10),
              TextField(
                  controller: patterns,
                  decoration: InputDecoration(
                      labelText: s.patterns, border: const OutlineInputBorder(), isDense: true)),
              const SizedBox(height: 10),
              TextField(
                  controller: dir,
                  decoration: InputDecoration(
                      labelText: s.destination, border: const OutlineInputBorder(), isDense: true)),
            ],
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: Text(s.close)),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: Text(s.save)),
        ],
      ),
    );
    if (ok != true) return;
    await widget.engine?.call('save_category', {
      'name': name.text.trim(),
      'patterns': patterns.text.trim(),
      'destination_dir': dir.text.trim(),
      'icon': 'folder',
    });
    await _load();
  }

  @override
  Widget build(BuildContext context) {
    final s = S.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(children: [
                Icon(Icons.category_outlined,
                    size: 18, color: Theme.of(context).colorScheme.primary),
                const SizedBox(width: 10),
                Text(s.categoriesEditor,
                    style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 14)),
                const Spacer(),
                IconButton(
                  tooltip: s.addCategory,
                  icon: const Icon(Icons.add_rounded, size: 20),
                  onPressed: () => _addOrEdit(),
                ),
              ]),
              if (_loaded && _cats.isEmpty)
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Text(s.none,
                      style: TextStyle(
                          fontSize: 12.5,
                          color: Theme.of(context).colorScheme.onSurfaceVariant)),
                ),
              for (final c in _cats)
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Row(children: [
                    Icon(Icons.folder_rounded,
                        size: 16, color: Theme.of(context).colorScheme.onSurfaceVariant),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(c['name']?.toString() ?? '',
                              style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                          if ((c['patterns']?.toString() ?? '').isNotEmpty)
                            Text(c['patterns'].toString(),
                                style: TextStyle(
                                    fontSize: 11.5,
                                    color: Theme.of(context).colorScheme.onSurfaceVariant)),
                        ],
                      ),
                    ),
                    IconButton(
                      icon: const Icon(Icons.edit_outlined, size: 16),
                      onPressed: () => _addOrEdit(c),
                    ),
                    IconButton(
                      icon: const Icon(Icons.delete_outline_rounded, size: 16),
                      onPressed: () async {
                        await widget.engine?.call('delete_category', {'name': c['name']});
                        await _load();
                      },
                    ),
                  ]),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// IDM parity — byte-quota windows: rolling cap with auto-pause + auto-resume.
class _QuotaSection extends StatelessWidget {
  const _QuotaSection({required this.settings, required this.engine});
  final Map<String, String> settings;
  final dynamic engine;

  @override
  Widget build(BuildContext context) {
    final s = S.of(context);
    final capCtrl =
        TextEditingController(text: settings['quota.max_bytes'] ?? '500MB');
    final windowCtrl =
        TextEditingController(text: settings['quota.window_minutes'] ?? '60');
    void save(String k, String v) =>
        engine?.call('set_settings', {'values': {k: v}});
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(children: [
                Icon(Icons.data_usage_rounded,
                    size: 18, color: Theme.of(context).colorScheme.primary),
                const SizedBox(width: 10),
                Text(s.quotaSection,
                    style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 14)),
              ]),
              const SizedBox(height: 12),
              SwitchListTile(
                value: settings['quota.enabled'] == 'true',
                onChanged: (v) => save('quota.enabled', '$v'),
                title: Text(s.quotaEnabled, style: const TextStyle(fontSize: 13.5)),
                dense: true,
                contentPadding: EdgeInsets.zero,
              ),
              Row(children: [
                Expanded(
                  child: TextField(
                    controller: capCtrl,
                    decoration: InputDecoration(
                        labelText: s.quotaCap,
                        border: const OutlineInputBorder(),
                        isDense: true),
                    onSubmitted: (v) => save('quota.max_bytes', v.trim()),
                  ),
                ),
                const SizedBox(width: 12),
                SizedBox(
                  width: 160,
                  child: TextField(
                    controller: windowCtrl,
                    keyboardType: TextInputType.number,
                    decoration: InputDecoration(
                        labelText: s.quotaWindow,
                        border: const OutlineInputBorder(),
                        isDense: true),
                    onSubmitted: (v) =>
                        save('quota.window_minutes', v.trim().isEmpty ? '60' : v.trim()),
                  ),
                ),
              ]),
            ],
          ),
        ),
      ),
    );
  }
}
