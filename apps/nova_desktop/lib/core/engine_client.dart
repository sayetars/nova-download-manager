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

/// Unix-domain-socket transport for Linux/macOS dev — the .NET engine's
/// System.IO.Pipes maps its named pipe to a user-owned UDS
/// (<tempdir>/CoreFxPipe_<name>, or NOVA_PIPE_PATH when absolute).
/// Stream chunks may contain several length-prefixed frames, so bytes are
/// buffered and split into exactly one frame per event.
class UnixSocketTransport implements NovaTransport {
  Socket? _socket;
  final _incoming = StreamController<Uint8List>.broadcast();
  Uint8List _buf = Uint8List(0);
  bool _closed = false;

  @override
  bool get isConnected => _socket != null;

  @override
  Future<void> connect(String pipeName, String token) async {
    final socket = await Socket.connect(
      InternetAddress(Protocol.pipePath, type: InternetAddressType.unix),
      0,
      timeout: const Duration(seconds: 5),
    );
    _closed = false;
    _socket = socket;
    socket.listen(
      _onData,
      onError: (Object e) {
        if (!_closed) _incoming.addError(e);
      },
      onDone: () {
        // Surface the disconnect as an error instead of closing the stream —
        // the controller must stay open so the client can reconnect.
        _socket = null;
        if (!_closed) _incoming.addError(StateError('socket closed'));
      },
    );
  }

  void _onData(Uint8List chunk) {
    if (_closed) return;
    final merged = Uint8List(_buf.length + chunk.length)
      ..setRange(0, _buf.length, _buf)
      ..setRange(_buf.length, _buf.length + chunk.length, chunk);
    _buf = merged;
    var offset = 0;
    while (_buf.length - offset >= 4) {
      final len = ByteData.sublistView(_buf, offset, offset + 4)
          .getInt32(0, Endian.little);
      if (len < 0 || len > Protocol.maxFrameBytes) {
        throw StateError('bad frame length $len');
      }
      if (_buf.length - offset < 4 + len) break;
      // Emit the FULL frame (length prefix included) — that is the contract
      // Framing.tryDecode expects (mirrors the Windows C++ bridge).
      _incoming.add(Uint8List.fromList(_buf.sublist(offset, offset + 4 + len)));
      offset += 4 + len;
    }
    if (offset > 0) {
      _buf = Uint8List.fromList(_buf.sublist(offset));
    }
  }

  @override
  Future<void> send(Uint8List bytes) async {
    final socket = _socket;
    if (_closed || socket == null) {
      throw StateError('unix socket not connected');
    }
    socket.add(bytes);
    await socket.flush();
  }

  @override
  Stream<Uint8List> get incoming => _incoming.stream;

  @override
  Future<void> close() async {
    _closed = true;
    _socket?.destroy();
    _socket = null;
    await _incoming.close();
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
    if (Platform.isLinux || Platform.isMacOS) return UnixSocketTransport();
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
      final token = await readEngineToken();
      await _transport.connect(Protocol.pipeName, token);
      // Listen BEFORE the handshake goes out — the ok/error reply arrives on
      // this broadcast stream and must not be dropped.
      _listenIncoming();
      final hello = Envelope(type: 'hello', payload: {
        'client': 'flutter-ui',
        'protocol_version': Protocol.version,
        'client_version': '1.0.0',
      }, token: token);
      final resp = await _raw(hello, timeoutMs: 8000);
      if (resp.type != 'ok') throw StateError('handshake rejected: ${resp.type}');
      _authed = true;
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

  StreamSubscription<Uint8List>? _rawSub;

  void _listenIncoming() {
    // Idempotent: reconnects reuse the same subscription on the same transport.
    if (_rawSub != null) return;
    _rawSub = _transport.incoming.listen((frame) {
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
      _rawSub = null;
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
