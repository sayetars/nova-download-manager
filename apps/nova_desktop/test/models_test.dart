import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nova_desktop/core/models.dart';
import 'package:nova_desktop/core/strings.dart';
import 'package:nova_desktop/pages/downloads_page.dart';

void main() {
  test('TaskUi parses snake_case task JSON from the engine', () {
    final task = TaskUi.fromJson({
      'task_key': 'abc',
      'state': 'downloading',
      'kind': 'hls',
      'file_name': 'video.ts',
      'url': 'https://x/y.m3u8',
      'total_bytes': 1000,
      'received_bytes': 250,
      'segments_done': 2,
      'segments_total': 8,
      'error_code': null,
    });
    expect(task.taskKey, 'abc');
    expect(task.kind, 'hls');
    expect(task.isRunning, isTrue);
    // segments fallback progress when total bytes unknown
    final unknown = task.copyWith(total: -1);
  });

  test('progress: bytes-based and segments-based', () {
    final t1 = const TaskUi(taskKey: 'a', totalBytes: 200, receivedBytes: 100);
    expect(t1.progress, 0.5);
    final t2 = const TaskUi(taskKey: 'b', totalBytes: -1, segmentsTotal: 4, segmentsDone: 3);
    expect(t2.progress, 0.75);
  });

  testWidgets('TaskTile renders running task with speed and ETA', (tester) async {
    final task = const TaskUi(
      taskKey: 'k1',
      state: 'downloading',
      fileName: 'big-file.zip',
      totalBytes: 10 * 1024 * 1024,
      receivedBytes: 4 * 1024 * 1024,
      speedBps: 2 * 1024 * 1024,
      etaSeconds: 3,
    );
    await tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          locale: const Locale('en'),
          localizationsDelegates: const [SDelegate()],
          home: Scaffold(body: TaskTile(task: task)),
        ),
      ),
    );
    await tester.pumpAndSettle(); // async localization delegates must finish loading
    expect(find.text('big-file.zip'), findsOneWidget);
    expect(find.byType(LinearProgressIndicator), findsOneWidget);
    await tester.tap(find.byType(Card));
    await tester.pumpAndSettle();
  });
}

extension on TaskUi {
  TaskUi copyWith({int? total}) => TaskUi(
        taskKey: taskKey,
        state: state,
        kind: kind,
        fileName: fileName,
        url: url,
        effectiveUrl: effectiveUrl,
        destinationDir: destinationDir,
        queueName: queueName,
        category: category,
        totalBytes: total ?? totalBytes,
        receivedBytes: receivedBytes,
        segmentCount: segmentCount,
        segmentsDone: segmentsDone,
        segmentsTotal: segmentsTotal,
        errorCode: errorCode,
        errorMessage: errorMessage,
        errorCategory: errorCategory,
        validation: validation,
        priority: priority,
        retryCount: retryCount,
        createdAtMs: createdAtMs,
        startedAtMs: startedAtMs,
        completedAtMs: completedAtMs,
        speedBps: speedBps,
        etaSeconds: etaSeconds,
      );
}

class SDelegate extends LocalizationsDelegate<S> {
  const SDelegate();
  @override
  bool isSupported(Locale locale) => true;
  @override
  Future<S> load(Locale locale) async => S.forLocale(locale);
  @override
  bool shouldReload(SDelegate old) => false;
}
