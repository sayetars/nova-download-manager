import 'dart:ui' show Locale;

import 'package:flutter_test/flutter_test.dart';
import 'package:nova_desktop/core/models.dart';
import 'package:nova_desktop/core/strings.dart';

/// v1.5.0 G-track UI models: PGP status fields, previewability, withProgress carry-over.
void main() {
  test('TaskUi parses v1.5.0 pgp fields from task JSON', () {
    final task = TaskUi.fromJson({
      'task_key': 'pgp1',
      'state': 'completed',
      'kind': 'file',
      'file_name': 'dist.tar.gz',
      'url': 'https://x/dist.meta4',
      'category': 'archive',
      'pgp_status': 'verified',
      'has_pgp_signature': true,
    });
    expect(task.pgpStatus, 'verified');
    expect(task.hasPgpSignature, isTrue);
  });

  test('TaskUi defaults pgp fields for old payloads', () {
    final task = TaskUi.fromJson({'task_key': 'old'});
    expect(task.pgpStatus, 'none');
    expect(task.hasPgpSignature, isFalse);
  });

  test('withProgress keeps pgp fields across progress events', () {
    final task = TaskUi.fromJson({
      'task_key': 'pgp2',
      'pgp_status': 'pending',
      'has_pgp_signature': true,
    });
    final updated = task.withProgress({
      'received_bytes': 500,
      'total_bytes': 1000,
      'speed': 1024,
    });
    expect(updated.pgpStatus, 'pending');
    expect(updated.hasPgpSignature, isTrue);
    expect(updated.speedBps, 1024);
  });

  test('isPreviewable: video/audio/hls/dash tasks only', () {
    expect(const TaskUi(taskKey: 'a', category: 'video').isPreviewable, isTrue);
    expect(const TaskUi(taskKey: 'b', category: 'audio').isPreviewable, isTrue);
    expect(const TaskUi(taskKey: 'c', kind: 'hls').isPreviewable, isTrue);
    expect(const TaskUi(taskKey: 'd', kind: 'dash').isPreviewable, isTrue);
    expect(const TaskUi(taskKey: 'e', category: 'program').isPreviewable, isFalse);
    expect(const TaskUi(taskKey: 'f', kind: 'torrent').isPreviewable, isFalse);
  });

  test('v1.5.0 strings exist in both EN and FA', () {
    final en = S.forLocale(const Locale('en'));
    final fa = S.forLocale(const Locale('fa'));
    for (final s in [en, fa]) {
      expect(s.previewTitle, isNotEmpty);
      expect(s.previewOpenPlayer, isNotEmpty);
      expect(s.previewCopyUrl, isNotEmpty);
      expect(s.ytdlpAnalyze, isNotEmpty);
      expect(s.ytdlpNoBinary, isNotEmpty);
      expect(s.serverProfilesTitle, isNotEmpty);
      expect(s.serverSpeed, isNotEmpty);
      expect(s.clientCertsTitle, isNotEmpty);
      expect(s.certHostPattern, isNotEmpty);
      expect(s.pgpKeysTitle, isNotEmpty);
      expect(s.pgpPolicyRequire, isNotEmpty);
      expect(s.pgpVerified, isNotEmpty);
    }
    // Persian strings are actually Persian
    expect(fa.previewTitle, contains('پیش'));
    expect(fa.serverProfilesTitle, contains('سرور'));
  });
}
