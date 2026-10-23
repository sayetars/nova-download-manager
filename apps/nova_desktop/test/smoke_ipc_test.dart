// End-to-end smoke test (Linux/dev): real engine over the real transport.
// Requires a running engine: start it first, then run with the gate enabled:
//   NOVA_SMOKE=1 flutter test test/smoke_ipc_test.dart
// Without the gate the test is skipped so normal `flutter test` stays green.
import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:nova_desktop/core/engine_client.dart';
import 'package:nova_desktop/core/protocol.dart';

void main() {
  final smoke = Platform.environment['NOVA_SMOKE'] == '1';

  test('engine end-to-end: handshake → add_download → file on disk', () async {
    final socket = Protocol.pipePath;
    expect(File(socket).existsSync(), isTrue,
        reason: 'engine socket $socket missing — is the engine running?');
    final token = await readEngineToken();
    expect(token.length, greaterThanOrEqualTo(32),
        reason: 'ipc.token missing/short in ~/.local/share/nova');

    // 1) local HTTP origin serving deterministic bytes
    final payload = Uint8List(256 * 1024);
    for (var i = 0; i < payload.length; i++) {
      payload[i] = (i * 7 + 13) & 0xFF;
    }
    late HttpServer origin;
    origin = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    origin.listen((req) async {
      if (req.uri.path == '/file.bin') {
        req.response.contentLength = payload.length;
        req.response.add(payload);
        await req.response.close();
      } else {
        req.response.statusCode = 404;
        await req.response.close();
      }
    });
    addTearDown(() => origin.close(force: true));

    // 2) handshake over the same transport the UI uses
    final client = EngineClient();
    addTearDown(() => client.dispose());
    await client.connect();
    expect(client.isReady, isTrue, reason: 'handshake must authenticate');

    final status = await client.call('engine_status');
    expect(status, isNotEmpty, reason: 'engine_status must answer');

    // 3) real download through the engine
    final url = 'http://127.0.0.1:${origin.port}/file.bin';
    final added = await client.call('add_download', {'url': url, 'auto_start': true});
    final taskKey = added['task_key'] as String?;
    expect(taskKey, isNotNull, reason: 'engine must accept add_download');

    // 4) wait for completion (events or polling)
    final done = Completer<void>();
    final sub = client.events.listen((env) {
      if (env.type == 'task_completed' || env.type == 'task_failed') {
        if (!done.isCompleted) done.complete();
      }
    });
    Map<String, dynamic> last = {};
    for (var i = 0; i < 60 && !done.isCompleted; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 500));
      final resp = await client.call('list_tasks', {'filter': 'all', 'limit': 50});
      final tasks = (resp['tasks'] as List).cast<Map<String, dynamic>>();
      final me = tasks.where((t) => t['task_key'] == taskKey).toList();
      if (me.isNotEmpty) last = me.first;
      if (last['state'] == 'completed' || last['state'] == 'failed') break;
    }
    sub.cancel();
    expect(last['state'], equals('completed'),
        reason: 'task should complete; last state: $last');

    // 5) byte-exact file on disk
    final dest = last['final_path'] as String?;
    expect(dest, isNotNull, reason: 'engine must report the output path');
    final bytes = await File(dest!).readAsBytes();
    expect(bytes.length, equals(payload.length));
    expect(bytes, equals(payload), reason: 'downloaded bytes must be identical');
  }, skip: !smoke);
}
