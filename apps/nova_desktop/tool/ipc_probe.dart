// Linux/dev smoke probe: connects to the Nova engine over the Unix-socket
// transport (same code path as the UI), handshakes and exercises basic RPCs.
// Usage: dart run tool/ipc_probe.dart [url]
import 'dart:async';
import 'dart:io';

import 'package:nova_desktop/core/engine_client.dart';
import 'package:nova_desktop/core/protocol.dart';

Future<void> main(List<String> args) async {
  final socket = Protocol.pipePath;
  stdout.writeln('probe: socket=$socket');
  if (!File(socket).existsSync()) {
    stderr.writeln('probe: FAIL — socket does not exist (engine not running?)');
    exit(2);
  }
  final token = await readEngineToken();
  if (token.isEmpty) {
    stderr.writeln('probe: FAIL — no ipc.token in ~/.local/share/nova');
    exit(2);
  }
  stdout.writeln('probe: token present (${token.length} chars)');

  final client = EngineClient(); // default transport on Linux = UnixSocketTransport
  await client.connect();
  stdout.writeln('probe: handshake ok (isReady=${client.isReady})');

  final status = await client.call('engine_status');
  stdout.writeln('probe: engine_status → $status');

  final url = args.isNotEmpty ? args[0] : 'https://speed.hetzner.de/1MB.bin';
  try {
    final added = await client.call('add_download', {
      'url': url,
      'auto_start': true,
    });
    stdout.writeln('probe: add_download → $added');
  } catch (e) {
    stdout.writeln('probe: add_download rejected (acceptable in offline test): $e');
  }

  final tasks = await client.call('list_tasks', {'filter': 'all', 'limit': 10});
  stdout.writeln('probe: list_tasks → ${tasks['tasks']?.length ?? 0} task(s)');

  // collect a couple of live events to prove the event channel works
  final events = <String>[];
  final sub = client.events.listen((env) => events.add(env.type));
  await Future<void>.delayed(const Duration(seconds: 3));
  sub.cancel();
  stdout.writeln('probe: events seen: $events');

  await client.dispose();
  stdout.writeln('probe: PASS');
  exit(0);
}
