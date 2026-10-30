import 'dart:io';

import 'package:flutter/services.dart';

/// v1.8.0 (IDM "drag & drop downloaded files out" parity): starts a NATIVE
/// drag from the app with the completed file(s) — drop them into Explorer,
/// e-mail attachments, chat apps… The channel is implemented in the Windows
/// runner (OLE DoDragDrop + CF_HDROP); on other platforms it is a no-op that
/// reports false (Linux/macOS drag-out needs runner support not shipped yet).
class NovaDragOut {
  static const _channel = MethodChannel('nova/dragout');

  /// Starts a native file drag. [files] must be absolute local paths.
  /// Returns the resulting effect (1 = copied) or 0 when unsupported/cancelled.
  static Future<int> start(List<String> files) async {
    if (files.isEmpty) return 0;
    if (!Platform.isWindows) return 0;
    try {
      final effect = await _channel.invokeMethod<int>('start', files);
      return effect ?? 0;
    } on MissingPluginException {
      return 0;
    } catch (_) {
      return 0;
    }
  }
}
