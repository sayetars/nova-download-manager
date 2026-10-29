import 'package:flutter/services.dart';

/// v1.8.0 (IDM tray parity): Windows tray icon bridge (windows/runner C++ side).
/// The channel is only implemented on Windows; every call degrades to a no-op
/// (MissingPluginException) on Linux/macOS, where closing the window still
/// exits the UI while the engine keeps downloading (documented limitation).
class NovaTray {
  static const _channel = MethodChannel('nova/tray');

  /// Creates the tray icon (idempotent). Safe to call on any platform.
  static Future<bool> init() async {
    try {
      await _channel.invokeMethod('init');
      return true;
    } on MissingPluginException {
      return false;
    } catch (_) {
      return false;
    }
  }

  /// Hides the window to the tray (minimize-to-tray).
  static Future<bool> hide() async {
    try {
      await _channel.invokeMethod('hide');
      return true;
    } on MissingPluginException {
      return false;
    } catch (_) {
      return false;
    }
  }

  /// Shows the window again.
  static Future<bool> show() async {
    try {
      await _channel.invokeMethod('show');
      return true;
    } on MissingPluginException {
      return false;
    } catch (_) {
      return false;
    }
  }

  /// Tray menu events from the runner: onTrayShow / onTrayExit.
  static void setHandlers({required void Function() onShow, required void Function() onExit}) {
    _channel.setMethodCallHandler((call) async {
      switch (call.method) {
        case 'onTrayShow':
          onShow();
        case 'onTrayExit':
          onExit();
      }
      return null;
    });
  }
}
