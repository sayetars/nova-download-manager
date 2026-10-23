import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/strings.dart';
import '../core/theme.dart';
import '../providers/engine.dart' as prov;

/// Browser integration status + guidance.
class BrowserIntegrationPage extends ConsumerWidget {
  const BrowserIntegrationPage({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final engine = ref.watch(prov.engineProvider);
    final connected = engine != null;
    final registered = _hostManifestRegistered();

    return ListView(
      padding: const EdgeInsets.all(20),
      children: [
        Text(s.browserIntegration,
            style: Theme.of(context).textTheme.titleLarge!.copyWith(fontWeight: FontWeight.w800)),
        const SizedBox(height: 16),
        _browserCard(
          context,
          icon: Icons.public_rounded,
          title: s.chromiumExt,
          registered: registered,
          path: 'extensions/chromium',
          s: s,
        ),
        const SizedBox(height: 10),
        _browserCard(
          context,
          icon: Icons.local_fire_department_outlined,
          title: s.firefoxExt,
          registered: registered,
          path: 'extensions/firefox',
          s: s,
        ),
        const SizedBox(height: 18),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(Icons.info_outline_rounded,
                        size: 20, color: connected ? NovaColors.success : NovaColors.warning),
                    const SizedBox(width: 10),
                    Text(connected ? s.extStatusConnected : s.engineDisconnected,
                        style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 13.5)),
                  ],
                ),
                const SizedBox(height: 10),
                Text(s.extHowTo, style: TextStyle(fontSize: 12.5, color: Theme.of(context).colorScheme.onSurfaceVariant)),
              ],
            ),
          ),
        ),
      ],
    );
  }

  bool _hostManifestRegistered() {
    if (!Platform.isWindows) return false;
    return Platform.environment.containsKey('NOVA_DEV'); // real check happens via engine later
  }

  Widget _browserCard(
    BuildContext context, {
    required IconData icon,
    required String title,
    required bool registered,
    required String path,
    required S s,
  }) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Container(
              width: 42,
              height: 42,
              decoration: BoxDecoration(
                  color: (registered ? NovaColors.completed : NovaColors.paused).withOpacity(0.13),
                  borderRadius: BorderRadius.circular(11)),
              child: Icon(icon, color: registered ? NovaColors.completed : NovaColors.paused, size: 21),
            ),
            const SizedBox(width: 13),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(title, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 13.5)),
                  const SizedBox(height: 3),
                  Text(registered ? s.extStatusConnected : s.extStatusNotInstalled,
                      style: TextStyle(fontSize: 12, color: registered ? NovaColors.completed : cs.onSurfaceVariant)),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Diagnostics — engine info + protocol + data dir.
class DiagnosticsPage extends ConsumerWidget {
  const DiagnosticsPage({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = S.of(context);
    final status = ref.watch(prov.engineStatusProvider).value;
    final cs = Theme.of(context).colorScheme;

    return ListView(
      padding: const EdgeInsets.all(20),
      children: [
        Text(s.diagnostics, style: Theme.of(context).textTheme.titleLarge!.copyWith(fontWeight: FontWeight.w800)),
        const SizedBox(height: 16),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              children: [
                _row(context, s.protocolVersion, 'nipc/1'),
                _row(context, s.platform, status?.platform ?? s.none),
                _row(context, s.uptime, status != null ? '${status.uptimeSeconds}s' : s.none),
                _row(context, s.activeTasks, '${status?.activeTasks ?? 0}'),
                _row(context, s.queuedDownloads, '${status?.queuedTasks ?? 0}'),
                _row(context, s.schedulerState, status?.schedulerState ?? s.none),
                _row(context, s.totalSpeed, Fmt.speed(context, status?.globalSpeed ?? 0)),
                _row(context, s.dataDir, status == null || status.platform.isEmpty ? s.none : _guessDataDir()),
              ],
            ),
          ),
        ),
      ],
    );
  }

  String _guessDataDir() {
    if (Platform.isWindows) {
      return '${Platform.environment['LOCALAPPDATA'] ?? ''}\\Nova';
    }
    final home = Platform.environment['HOME'] ?? '';
    return '$home/.local/share/nova';
  }

  Widget _row(BuildContext context, String k, String v) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 7),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 150,
            child: Text(k, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600)),
          ),
          Expanded(
            child: SelectableText(v, style: const TextStyle(fontSize: 12.5)),
          ),
        ],
      ),
    );
  }
}
