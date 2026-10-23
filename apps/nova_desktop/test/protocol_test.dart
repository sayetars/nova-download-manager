import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:nova_desktop/core/protocol.dart';

void main() {
  group('nipc/1 framing', () {
    test('encode/decode roundtrip preserves type and id', () {
      final env = Envelope(
        id: 'ui1',
        type: 'add_download',
        payload: {'url': 'https://example.com/file.bin', 'max_connections': 8},
      );
      final bytes = Framing.encode(env);
      // 4-byte prefix + payload
      final len = ByteData.sublistView(bytes).getInt32(0, Endian.little);
      expect(len, bytes.length - 4);

      final back = Framing.tryDecode(ByteData.sublistView(bytes));
      expect(back, isNotNull);
      expect(back!.id, 'ui1');
      expect(back.type, 'add_download');
      expect(back.payloadMap!['url'], 'https://example.com/file.bin');
      expect(back.payloadMap!['max_connections'], 8);
    });

    test('wire keys are snake_case (C# compatible)', () {
      final env = Envelope(type: 'add_download', payload: {'url': 'x'});
      final json = jsonDecode(utf8.decode(Framing.encode(env).sublist(4)))
          as Map<String, dynamic>;
      expect(json.containsKey('v'), isTrue);
      expect(json.containsKey('type'), isTrue);
      expect(json.containsKey('ts'), isTrue);
    });

    test('rejects frames exceeding the 4 MiB cap', () {
      expect(
        () => Framing.tryDecode(
          ByteData(8)..setInt32(0, Protocol.maxFrameBytes + 1, Endian.little),
        ),
        throwsStateError,
      );
    });

    test('incomplete frames return null (streaming partial reads)', () {
      final env = Envelope(id: 'x', type: 'ping');
      final bytes = Framing.encode(env);
      final partial = ByteData.sublistView(bytes.sublist(0, 7));
      expect(Framing.tryDecode(partial), isNull);
    });
  });

  group('handshake envelope', () {
    test('hello carries client and version', () {
      final env = Envelope(type: 'hello', token: 'secret', payload: {
        'client': 'flutter-ui',
        'protocol_version': Protocol.version,
      });
      final json = jsonDecode(utf8.decode(Framing.encode(env).sublist(4)))
          as Map<String, dynamic>;
      expect(json['token'], 'secret');
      expect((json['payload'] as Map)['client'], 'flutter-ui');
      expect(json['v'], Protocol.version);
    });
  });
}
