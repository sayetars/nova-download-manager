import 'package:flutter/material.dart';

void main() => runApp(const NovaApp());

/// Nova Download Manager — UI scaffold (round 12).
/// Full bilingual (EN / FA + RTL) interface lands in the next rounds; the engine
/// (versioned JSON IPC over Named Pipe / Unix socket) is already shipped and tested.
class NovaApp extends StatelessWidget {
  const NovaApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Nova Download Manager',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        useMaterial3: true,
        brightness: Brightness.dark,
        colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xFF7C5CFC),
          brightness: Brightness.dark,
        ),
      ),
      home: const HomePage(),
    );
  }
}

class HomePage extends StatelessWidget {
  const HomePage({super.key});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Nova Download Manager'),
      ),
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(Icons.download_for_offline_outlined, size: 72),
            const SizedBox(height: 16),
            Text(
              'Nova UI scaffold v0.1',
              style: Theme.of(context).textTheme.titleMedium,
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 8),
            Text(
              'Engine v0.1.0 is built, self-tested and packaged alongside this app.\n'
              'The full bilingual (English / فارسی RTL) interface is under construction.',
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ],
        ),
      ),
    );
  }
}
