import 'package:flutter/material.dart';
import 'strings.dart';

/// Nova Design System — custom M3 tokens; no default look anywhere.
class NovaColors {
  // Brand: deep indigo + electric accent. Warm neutrals for surfaces.
  static const seed = Color(0xFF4F5BEE);
  static const accentLight = Color(0xFF4F5BEE);
  static const accentDark = Color(0xFF8B93FF);
  static const success = Color(0xFF1FA97A);
  static const warning = Color(0xFFE8A020);
  static const danger = Color(0xFFE5484D);
  static const downloading = Color(0xFF3D7BFF);
  static const paused = Color(0xFF98A0B3);
  static const completed = Color(0xFF1FA97A);
  static const failed = Color(0xFFE5484D);
  static const queued = Color(0xFF7B8AF0);

  static Color stateColor(String state, Brightness b) => switch (state) {
        'downloading' => downloading,
        'pausing' => warning,
        'paused' => paused,
        'queued' || 'created' => b == Brightness.light ? const Color(0xFF6E76E8) : queued,
        'retrying' => warning,
        'verifying' => const Color(0xFF15A0C4),
        'analyzing' => const Color(0xFF15A0C4),
        'completed' => completed,
        'failed' => failed,
        'cancelled' => paused,
        'interrupted' => warning,
        _ => paused,
      };
}

class NovaTheme {
  static const radius = 12.0;
  static const radiusSm = 8.0;

  static ThemeData light() => _base(Brightness.light).copyWith(
        colorScheme: ColorScheme.fromSeed(
          seedColor: NovaColors.seed,
          brightness: Brightness.light,
          surface: const Color(0xFFFCFCFE),
        ),
      );

  static ThemeData dark() => _base(Brightness.dark).copyWith(
        colorScheme: ColorScheme.fromSeed(
          seedColor: NovaColors.seed,
          brightness: Brightness.dark,
          surface: const Color(0xFF16171D),
        ),
      );

  static ThemeData _base(Brightness b) {
    final isLight = b == Brightness.light;
    return ThemeData(
      useMaterial3: true,
      brightness: b,
      fontFamilyFallback: const ['Segoe UI', 'Tahoma', 'Vazirmatn', 'Noto Sans Arabic'],
      scaffoldBackgroundColor: isLight ? const Color(0xFFF6F7FB) : const Color(0xFF111216),
      canvasColor: isLight ? const Color(0xFFF6F7FB) : const Color(0xFF111216),
      splashFactory: InkSparkle.splashFactory,
      visualDensity: VisualDensity.compact,
      textTheme: (isLight ? Typography.blackMountainView : Typography.whiteMountainView).apply(
        fontFamilyFallback: const ['Segoe UI', 'Tahoma', 'Vazirmatn', 'Noto Sans Arabic'],
        bodyColor: isLight ? const Color(0xFF1C1D26) : const Color(0xFFE7E8EF),
        displayColor: isLight ? const Color(0xFF1C1D26) : const Color(0xFFE7E8EF),
      ),
      appBarTheme: AppBarTheme(
        backgroundColor: Colors.transparent,
        elevation: 0,
        scrolledUnderElevation: 0,
        centerTitle: false,
        titleTextStyle: (isLight ? Typography.blackMountainView : Typography.whiteMountainView)
            .titleMedium!
            .copyWith(fontFamilyFallback: const ['Segoe UI', 'Tahoma', 'Vazirmatn', 'Noto Sans Arabic'], fontWeight: FontWeight.w700),
      ),
      navigationRailTheme: NavigationRailThemeData(
        backgroundColor: isLight ? const Color(0xFFFDFDFF) : const Color(0xFF191A20),
        elevation: 0,
        selectedIconTheme: const IconThemeData(size: 22),
        unselectedIconTheme: const IconThemeData(size: 22),
        selectedLabelTextStyle: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13),
        unselectedLabelTextStyle: const TextStyle(fontSize: 13),
      ),
      cardTheme: CardThemeData(
        elevation: 0,
        color: isLight ? Colors.white : const Color(0xFF1A1B21),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(radius),
          side: BorderSide(color: isLight ? const Color(0xFFE8E9F2) : const Color(0xFF262832)),
        ),
        margin: EdgeInsets.zero,
      ),
      listTileTheme: const ListTileThemeData(dense: true),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(radiusSm)),
          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 12),
          textStyle: const TextStyle(fontWeight: FontWeight.w700),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(radiusSm)),
          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 12),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(radiusSm)),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: isLight ? Colors.white : const Color(0xFF1E1F27),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(radiusSm),
          borderSide: BorderSide(color: isLight ? const Color(0xFFE1E2EC) : const Color(0xFF2B2D38)),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(radiusSm),
          borderSide: BorderSide(color: isLight ? const Color(0xFFE1E2EC) : const Color(0xFF2B2D38)),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(radiusSm),
          borderSide: const BorderSide(color: NovaColors.accentLight, width: 1.6),
        ),
        contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      ),
      snackBarTheme: const SnackBarThemeData(behavior: SnackBarBehavior.floating),
      dividerTheme: DividerThemeData(
        color: isLight ? const Color(0xFFECECF4) : const Color(0xFF24262F),
        thickness: 1,
        space: 1,
      ),
      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(
          color: isLight ? const Color(0xFF23242C) : const Color(0xFFE9EAF0),
          borderRadius: BorderRadius.circular(8),
        ),
        textStyle: TextStyle(color: isLight ? Colors.white : const Color(0xFF1C1D26), fontSize: 12),
      ),
      switchTheme: SwitchThemeData(
        thumbColor: WidgetStateProperty.resolveWith((s) => s.contains(WidgetState.selected)
            ? Colors.white
            : (isLight ? const Color(0xFFB9BCCB) : const Color(0xFF6A6E80))),
      ),
      progressIndicatorTheme: ProgressIndicatorThemeData(
        linearTrackColor: isLight ? const Color(0xFFE9EAF2) : const Color(0xFF262832),
        circularTrackColor: isLight ? const Color(0xFFE9EAF2) : const Color(0xFF262832),
      ),
    );
  }
}

/// Locale-aware human formats (bytes, speed, durations). Persian uses Eastern Arabic digits.
class Fmt {
  static String bytes(BuildContext context, int? b) {
    if (b == null || b < 0) return S.of(context).sizeUnknown;
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    var v = b.toDouble();
    var u = 0;
    while (v >= 1024 && u < units.length - 1) {
      v /= 1024;
      u++;
    }
    final s = u == 0 ? v.toStringAsFixed(0) : v.toStringAsFixed(v >= 100 ? 0 : 1);
    final locale = Localizations.localeOf(context).toString();
    return '$s ${units[u]}'.translateDigits(locale);
  }

  static String speed(BuildContext context, int bps) => '${bytes(context, bps)}/s';

  static String eta(BuildContext context, int seconds) {
    if (seconds < 0) return S.of(context).unknownEta;
    if (seconds < 60) {
      return '${seconds}s';
    }
    final h = seconds ~/ 3600;
    final m = (seconds % 3600) ~/ 60;
    final s = seconds % 60;
    final locale = Localizations.localeOf(context).toString();
    final out = h > 0 ? '$h h $m min' : '$m min ${s}s';
    return out.translateDigits(locale);
  }

  static String percent(BuildContext context, double p) {
    final v = (p * 100).clamp(0, 100).toStringAsFixed(p >= 0.999 ? 0 : 1);
    return '$v%'.translateDigits(Localizations.localeOf(context).toString());
  }
}

extension _DigitLocalize on String {
  String translateDigits(String locale) {
    if (!locale.startsWith('fa')) return this;
    const farsi = ['۰', '۱', '۲', '۳', '۴', '۵', '۶', '۷', '۸', '۹'];
    var out = '';
    for (final c in codeUnits) {
      out += (c >= 0x30 && c <= 0x39) ? farsi[c - 0x30] : String.fromCharCode(c);
    }
    return out;
  }
}
