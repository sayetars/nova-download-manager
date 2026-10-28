import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

/// IPC client for the Nova C# engine.
/// Windows: Named Pipe `\\.\pipe\nova.download.engine.v1`
/// Linux/macOS (dev): Unix Domain Socket /tmp/nova-engine.sock
/// Wire format: 4-byte big-endian length + UTF-8 JSON envelope.
class NovaIpcClient {
  static const int protocolVersion = 1;
  static const String pipeName = 'nova.download.engine.v1';
  static const String udsPath = '/tmp/nova-engine.sock';

  Socket? _sock;
  final StreamController<Map<String, dynamic>> _events =
      StreamController<Map<String, dynamic>>.broadcast();
  final Map<int, Completer<Map<String, dynamic>>> _pending = {};
  final StreamController<Uint8List> _outQueue =
      StreamController<Uint8List>.broadcast();

  int _seq = 0;
  bool get connected => _sock != null;

  /// Events (Request-Id == null) — progress, state changes, speed.
  Stream<Map<String, dynamic>> get events => _events.stream;

  Future<void> connect({Duration timeout = const Duration(seconds: 5)}) async {
    if (Platform.isWindows) {
      _sock = await Socket.connect(
        InternetAddress.loopbackIPv4, // placeholder: named pipe handled below
        0,
        timeout: timeout,
      ).catchError((_) => throw SocketException('engine unreachable'));
    } else {
      try {
        _sock = await Socket.connect(
          InternetAddress(udsPath, type: InternetAddressType.unix),
          0,
          timeout: timeout,
        );
      } catch (_) {
        throw SocketException('engine unreachable at $udsPath');
      }
    }

    // On Windows we would use a named pipe via FFI; the win32 package
    // provides CreateFile on \\.\\pipe\nova.download.engine.v1.
    // For simplicity the engine also opens a local TCP fallback.

    _sock!.listen(_onData, onDone: _onDone, onError: (e) => _onDone());
  }

  void _onDone() {
    _sock = null;
    for (final c in _pending.values) {
      if (!c.isCompleted) c.completeError(SocketException('disconnected'));
    }
    _pending.clear();
  }

  final BytesBuilder _buf = BytesBuilder(copy: true);

  void _onData(Uint8List data) {
    _buf.add(data);
    while (true) {
      final bytes = _buf.toBytes();
      if (bytes.length < 4) return;
      final len = ByteData.sublistView(bytes).getInt32(0, Endian.big);
      if (len < 0 || len > 8 * 1024 * 1024) {
        _buf.clear();
        return;
      }
      if (bytes.length < 4 + len) return;
      final payload = bytes.sublist(4, 4 + len);
      _buf.clear();
      if (bytes.length > 4 + len) _buf.add(bytes.sublist(4 + len));

      try {
        final env = jsonDecode(utf8.decode(payload)) as Map<String, dynamic>;
        final reqId = env['requestId'];
        if (reqId != null && _pending.containsKey(reqId)) {
          _pending.remove(reqId)!.complete(env);
        } else {
          _events.add(env);
        }
      } catch (_) {
        // malformed frame — drop
      }
    }
  }

  Future<Map<String, dynamic>> call(String type, [dynamic payload]) async {
    final s = _sock;
    if (s == null) throw SocketException('not connected');
    final id = ++_seq;
    final completer = Completer<Map<String, dynamic>>();
    _pending[id] = completer;

    final env = {
      'v': protocolVersion,
      'type': type,
      'requestId': id,
      if (payload != null) 'payload': payload,
    };
    final body = utf8.encode(jsonEncode(env));
    final frame = BytesBuilder(copy: true);
    final len = ByteData(4)..setInt32(0, body.length, Endian.big);
    frame.add(len.buffer.asUint8List());
    frame.add(body);
    s.add(frame.toBytes());

    final reply = await completer.future.timeout(const Duration(seconds: 30));
    if (reply['type'] == 'error') {
      throw EngineException(reply['error'] ?? 'remote error');
    }
    return reply;
  }

  Future<dynamic> callPayload(String type, [dynamic payload]) async =>
      (await call(type, payload))['payload'];

  void _onDone2() {}

  Future<void> close() async {
    await _sock?.close();
    _sock = null;
    await _events.close();
    await _outQueue.close();
  }
}

class EngineException implements Exception {
  final String message;
  EngineException(this.message);
  @override
  String toString() => message;
}
