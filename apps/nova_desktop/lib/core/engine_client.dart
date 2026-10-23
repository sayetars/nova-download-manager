import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

import 'protocol.dart';

/// Byte transport used by the engine client. The Windows implementation is a
/// C++ runner plugin (method channel "nova/pipe"); tests and dev builds can
/// inject an in-memory transport.
abstract class NovaTransport {
  Future<void> connect(String pipeName, String token);
  Future<void> send(Uint8List bytes);
  Stream<Uint8List> get incoming;
  Future<void> close();
  bool get isConnected;
}

/// Named-pipe transport for Windows — bridged by nova_pipe_bridge.cpp in the
/// runner (CreateFileW on \\.\pipe\<name> + background reader thread).
class WindowsPipeTransport implements NovaTransport {
  static const _channel = MethodChannel('nova/pipe');
  final _incoming = StreamController<Uint8List>.broadcast();

  WindowsPipeTransport() {
    _channel.setMethodCallHandler((call) async {
      if (call.method == 'onFrame') {
        final data = call.arguments as Uint8List;
        _incoming.add(data);
      }
      return null;
    });
  }

  @override
  bool get isConnected => _connected;
  bool _connected = false;

  @override
  Future<void> connect(String pipeName, String token) async {
    final ok = await _channel.invokeMethod<bool>('connect', {
      'pipeName': pipeName,
      'token': token,
    });
    if (ok != true) {
      throw StateError('pipe connect failed');
    }
    _connected = true;
  }

  @override
  Future<void> send(Uint8List bytes) async {
    await _channel.invokeMethod<void>('send', {'bytes': bytes});
  }

  @override
  Stream<Uint8List> get incoming => _incoming.stream;

  @override
  Future<void> close() async {
    _connected = false;
    try {
      await _channel.invokeMethod<void>('disconnect');
    } catch (_) {/* engine gone */}
  }
}

/// Reads the per-user IPC token from the engine data dir (mirrors C#).
Future<String> readEngineToken() async {
  final home = Platform.environment['HOME'] ?? '';
  final isWindows = Platform.isWindows;
  final dir = isWindows
      ? '${Platform.environment['LOCALAPPDATA'] ?? ''}\\Nova'
      : '$home/.local/share/nova';
  final file = File(isWindows ? '$dir\\ipc.token' : '$dir/ipc.token');
  try {
    final t = (await file.readAsString()).trim();
    if (t.length >= 32) return t;
  } catch (_) {/* engine may not have run yet */}
  return '';
}

/// Connection to the Nova engine: handshake, request/response, events, auto-reconnect.
class EngineClient {
  EngineClient({NovaTransport? transport})
      : _transport = transport ?? _defaultTransport();

  static NovaTransport _defaultTransport() {
    if (Platform.isWindows) return WindowsPipeTransport();
    throw UnsupportedError('No default engine transport on this platform — inject one for tests/dev');
  }

  final NovaTransport _transport;
  final _events = StreamController<Envelope>.broadcast();
  final _pending = <String, Completer<Envelope>>{};
  int _seq = 0;
  bool _authed = false;
  bool _closed = false;
  Timer? _reconnectTimer;

  Stream<Envelope> get events => _events.stream;
  bool get isReady => _authed && _transport.isConnected;

  Future<void> connect({Duration retryEvery = const Duration(seconds: 3)}) async {
    if (_closed) return;
    try {
      await _transport.connect(Protocol.pipeName, await readEngineToken());
      // handshake
      final hello = Envelope(type: 'hello', payload: {
        'client': 'flutter-ui',
        'protocol_version': Protocol.version,
        'client_version': '1.0.0',
      });
      final resp = await _raw(hello, timeoutMs: 8000);
      if (resp.type != 'ok') throw StateError('handshake rejected: ${resp.type}');
      _authed = true;
      _listenIncoming();
    } catch (e) {
      _scheduleReconnect(retryEvery);
      rethrow;
    }
  }

  void _scheduleReconnect(Duration every) {
    if (_closed) return;
    _reconnectTimer?.cancel();
    _reconnectTimer = Timer(every, () => connect(retryEvery: every));
  }

  void _listenIncoming() {
    _transport.incoming.listen((frame) {
      final env = Framing.tryDecode(ByteData.sublistView(frame));
      if (env == null) return;
      if (env.id != null && _pending.containsKey(env.id)) {
        final c = _pending.remove(env.id)!;
        c.complete(env);
      } else if (!_isControlType(env.type)) {
        _events.add(env);
      }
    }, onError: (Object e) {
      _authed = false;
      _failPending('connection lost');
      _scheduleReconnect(const Duration(seconds: 3));
    }, onDone: () {
      _authed = false;
      _failPending('connection closed');
      _scheduleReconnect(const Duration(seconds: 3));
    });
  }

  static bool _isControlType(String t) =>
      t == 'ok' || t == 'error' || t == 'ping' || t == 'bye';

  Future<Envelope> _raw(Envelope env, {int timeoutMs = Protocol.requestTimeoutMs}) async {
    final id = 'ui${++_seq}';
    final withId = Envelope(
        v: env.v, id: id, type: env.type, payload: env.payload, token: env.token);
    final completer = Completer<Envelope>();
    _pending[id] = completer;
    try {
      await _transport.send(Framing.encode(withId));
    } catch (e) {
      _pending.remove(id);
      rethrow;
    }
    return completer.future.timeout(Duration(milliseconds: timeoutMs),
        onTimeout: () {
      _pending.remove(id);
      throw TimeoutException('engine request "${env.type}" timed out');
    });
  }

  /// Typed request. Throws [EngineIpcError] for engine-side failures.
  Future<Map<String, dynamic>> call(String type, [Object? payload]) async {
    if (!isReady) throw StateError('engine-not-ready');
    final resp = await _raw(Envelope(type: type, payload: payload));
    if (resp.type == 'error') {
      final err = resp.payloadMap ?? {};
      throw EngineIpcError(
        err['code'] as String? ?? 'error',
        err['message'] as String? ?? 'unknown error',
      );
    }
    return resp.payloadMap ?? <String, dynamic>{};
  }

  void _failPending(String reason) {
    final pending = List.of(_pending.values);
    _pending.clear();
    for (final c in pending) {
      c.completeError(StateError(reason));
    }
  }

  Future<void> dispose() async {
    _closed = true;
    _reconnectTimer?.cancel();
    _failPending('client disposed');
    await _transport.close();
    await _events.close();
  }
}

class EngineIpcError implements Exception {
  final String code;
  final String message;
  EngineIpcError(this.code, this.message);
  @override
  String toString() => 'EngineIpcError($code): $message';
}
