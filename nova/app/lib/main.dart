import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'services/app_model.dart';
import 'pages/main_page.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const NovaApp());
}

class NovaApp extends StatelessWidget {
  const NovaApp({super.key});

  @override
  Widget build(BuildContext context) {
    return ChangeNotifierProvider(
      create: (_) => AppModel()..start(),
      child: Consumer<AppModel>(
        builder: (context, model, _) => MaterialApp(
          title: 'Nova Download Manager',
          debugShowCheckedModeBanner: false,
          // RTL for Persian
          builder: (context, child) =>
              Directionality(textDirection: TextDirection.ltr, child: child!),
          theme: _lightTheme(),
          darkTheme: _darkTheme(),
          themeMode: ThemeMode.system,
          home: const MainPage(),
        ),
      ),
    );
  }

  ThemeData _lightTheme() => ThemeData(
        useMaterial3: true,
        colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xFF2563EB),
          brightness: Brightness.light,
        ),
        fontFamily: 'Segoe UI',
      );

  ThemeData _darkTheme() => ThemeData(
        useMaterial3: true,
        colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xFF2563EB),
          brightness: Brightness.dark,
        ),
        fontFamily: 'Segoe UI',
      );
}
