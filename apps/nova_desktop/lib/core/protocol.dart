import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

/// nipc/1 wire protocol — mirrors src/Nova.Ipc/Protocol.cs exactly.
class Protocol {
  static const int version = 1;
  static const String pipeName = 'nova-engine-v1';
  static const int maxFrameBytes = 4 * 1024 * 1024;
  static const int requestTimeoutMs = 30000;

  /// Socket path on Linux/macOS, mirroring the C# side (Protocol.ResolvedPipeName):
  /// System.IO.Pipes maps a plain pipe name to <tempdir>/CoreFxPipe_<name>;
  /// an absolute NOVA_PIPE_PATH wins on both ends for hardened setups.
  static String get pipePath {
    final env = Platform.environment['NOVA_PIPE_PATH'];
    if (env != null && env.startsWith('/')) return env;
    return '${Directory.systemTemp.path}/CoreFxPipe_$pipeName';
  }
}

class Envelope {
  final int v;
  final String? id;
  final String type;
  final Object? payload;
  final String? token;

  Envelope({
    this.v = Protocol.version,
    this.id,
    required this.type,
    this.payload,
    this.token,
  });

  Map<String, dynamic> toJson() => {
        'v': v,
        if (id != null) 'id': id,
        'type': type,
        if (payload != null) 'payload': payload,
        if (token != null) 'token': token,
        'ts': DateTime.now().millisecondsSinceEpoch,
      };

  static Envelope fromJson(Map<String, dynamic> json) => Envelope(
        v: (json['v'] as num?)?.toInt() ?? 1,
        id: json['id'] as String?,
        type: (json['type'] ?? '') as String,
        payload: json['payload'],
        token: json['token'] as String?,
      );

  Map<String, dynamic>? get payloadMap =>
      payload is Map<String, dynamic> ? payload as Map<String, dynamic> : null;
}

/// 4-byte little-endian length prefix + UTF-8 JSON payload.
class Framing {
  static Uint8List encode(Envelope env) {
    final json = jsonEncode(env.toJson());
    final payload = Uint8List.fromList(utf8.encode(json));
    final frame = ByteData(4 + payload.length);
    frame.setInt32(0, payload.length, Endian.little);
    frame.buffer.asUint8List().setRange(4, 4 + payload.length, payload);
    return frame.buffer.asUint8List();
  }

  /// Incremental decoder: feed bytes, get complete Envelopes out.
  static Envelope? tryDecode(ByteData buffer) {
    if (buffer.lengthInBytes < 4) return null;
    final len = buffer.getInt32(0, Endian.little);
    if (len < 0 || len > Protocol.maxFrameBytes) throw StateError('bad frame length $len');
    if (buffer.lengthInBytes < 4 + len) return null;
    final payload = buffer.buffer.asUint8List(buffer.offsetInBytes + 4, len);
    final json = utf8.decode(payload);
    return Envelope.fromJson(jsonDecode(json) as Map<String, dynamic>);
  }
}
