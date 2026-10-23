import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'core/strings.dart';
import 'core/theme.dart';
import 'pages/shell.dart';
import 'providers/engine.dart' as prov;

/// App-level preferences (theme + language), persisted through the engine settings.
final uiPrefsProvider = NotifierProvider<UiPrefsNotifier, UiPrefs>(UiPrefsNotifier.new);

class UiPrefs {
  final ThemeMode themeMode;
  final Locale locale;
  const UiPrefs({this.themeMode = ThemeMode.system, this.locale = const Locale('en')});

  UiPrefs copyWith({ThemeMode? themeMode, Locale? locale}) => UiPrefs(
        themeMode: themeMode ?? this.themeMode,
        locale: locale ?? this.locale,
      );
}

class UiPrefsNotifier extends Notifier<UiPrefs> {
  @override
  UiPrefs build() {
    final engine = ref.watch(prov.engineProvider);
    if (engine != null) {
      _hydrate();
    }
    return const UiPrefs();
  }

  Future<void> _hydrate() async {
    try {
      final engine = ref.read(prov.engineProvider)!;
      final resp = await engine.call('get_settings');
      final values = (resp['values'] as Map).cast<String, String>();
      final theme = switch (values['ui.theme']) {
        'dark' => ThemeMode.dark,
        'light' => ThemeMode.light,
        _ => ThemeMode.system,
      };
      final lang = values['ui.language'] == 'fa' ? const Locale('fa') : const Locale('en');
      state = UiPrefs(themeMode: theme, locale: lang);
    } catch (_) {/* defaults */}
  }

  void setTheme(ThemeMode mode) {
    state = state.copyWith(themeMode: mode);
    _persist('ui.theme', switch (mode) { ThemeMode.dark => 'dark', ThemeMode.light => 'light', _ => 'system' });
  }

  void setLocale(Locale l) {
    state = state.copyWith(locale: l);
    _persist('ui.language', l.languageCode);
  }

  Future<void> _persist(String key, String value) async {
    final engine = ref.read(prov.engineProvider);
    try {
      await engine?.call('set_settings', {
        'values': {key: value}
      });
    } catch (_) {/* offline: local-only until engine returns */}
  }
}

class NovaApp extends ConsumerWidget {
  const NovaApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final prefs = ref.watch(uiPrefsProvider);
    return MaterialApp(
      title: 'Nova Download Manager',
      debugShowCheckedModeBanner: false,
      themeMode: prefs.themeMode,
      theme: NovaTheme.light(),
      darkTheme: NovaTheme.dark(),
      locale: prefs.locale,
      supportedLocales: const [Locale('en'), Locale('fa')],
      localizationsDelegates: const [
        SDelegate(),
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      home: const ShellPage(),
    );
  }
}

/// Localizations delegate for our hand-rolled S() strings + RTL comes from the locale.
class SDelegate extends LocalizationsDelegate<S> {
  const SDelegate();
  @override
  bool isSupported(Locale locale) => ['en', 'fa'].contains(locale.languageCode);
  @override
  Future<S> load(Locale locale) async => S.forLocale(locale);
  @override
  bool shouldReload(SDelegate old) => false;
}
