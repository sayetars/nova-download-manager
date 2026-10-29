import 'dart:io';

/// v1.8.0 (IDM "Sounds" tab parity): best-effort, dependency-free event sounds.
/// A custom file (settings `sounds.file.<event>`) wins; otherwise the platform
/// default cue plays. Windows: SoundPlayer via powershell (.wav); Linux:
/// paplay/aplay/ffplay; macOS: afplay. Failures are silent — sounds never block.
class SoundPlayer {
  static void play(String? file) {
    try {
      final f = (file ?? '').trim();
      if (f.isNotEmpty && File(f).existsSync()) {
        _runFile(f);
        return;
      }
      _default();
    } catch (_) {/* best effort */}
  }

  static void _runFile(String f) {
    if (Platform.isWindows) {
      // Built-in .wav player; other formats fall back to ffplay when present.
      if (f.toLowerCase().endsWith('.wav')) {
        Process.run('powershell', [
          '-NoProfile', '-c',
          "(New-Object System.Media.SoundPlayer '$f').PlaySync();"
        ]);
      } else {
        Process.run('ffplay', ['-nodisp', '-autoexit', '-loglevel', 'quiet', f]);
      }
    } else if (Platform.isLinux) {
      if (f.toLowerCase().endsWith('.oga') || f.toLowerCase().endsWith('.ogg')) {
        Process.run('paplay', [f]);
      } else {
        Process.run('ffplay', ['-nodisp', '-autoexit', '-loglevel', 'quiet', f]);
      }
    } else if (Platform.isMacOS) {
      Process.run('afplay', [f]);
    }
  }

  static void _default() {
    if (Platform.isWindows) {
      Process.run('powershell', ['-c', '[console]::beep(880,180)']);
    } else if (Platform.isLinux) {
      Process.run('paplay', ['/usr/share/sounds/freedesktop/stereo/complete.oga']);
    } else if (Platform.isMacOS) {
      Process.run('afplay', ['/System/Library/Sounds/Glass.aiff']);
    }
  }
}
