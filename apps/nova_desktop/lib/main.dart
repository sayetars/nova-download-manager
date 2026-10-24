import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app.dart';
import 'core/strings.dart';
import 'core/theme.dart';
import 'mini_app.dart';

void main(List<String> args) {
  WidgetsFlutterBinding.ensureInitialized();
  if (args.contains('--mini')) {
    // IDM parity: progress mini-window (always-on-top compact instance)
    MiniApp.boot();
    return;
  }
  runApp(const ProviderScope(child: NovaApp()));
}
