import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/strings.dart';
import '../providers/engine.dart' as prov;

/// Site Grabber page (IDM parity): root URL, depth, include/exclude filters,
/// resource type chips, offline rewrite — backed by the engine's Nova.Grabber.
class GrabberPage extends ConsumerStatefulWidget {
  const GrabberPage({super.key});

  @override
  ConsumerState<GrabberPage> createState() => _GrabberPageState();
}

class _GrabberPageState extends ConsumerState<GrabberPage> {
  final _url = TextEditingController();
  final _dir = TextEditingController();
  final _include = TextEditingController();
  final _exclude = TextEditingController();
  final _minKb = TextEditingController();
  final _maxKb = TextEditingController();
  int _depth = 3;
  int _externalDepth = 0;
  bool _images = true;
  bool _css = true;
  bool _scripts = false;
  bool _video = false;
  bool _offline = true;
  // v1.8.0 (IDM Grabber wizard step 3 parity)
  bool _exploreParents = false;
  bool _exploreSubdomains = false;
  bool _running = false;
  String? _lastRun;

  @override
  void dispose() {
    _url.dispose();
    _dir.dispose();
    _include.dispose();
    _exclude.dispose();
    _minKb.dispose();
    _maxKb.dispose();
    super.dispose();
  }

  Future<void> _start() async {
    final s = S.of(context);
    setState(() => _running = true);
    try {
      final runId = await ref.read(prov.tasksProvider.notifier).startGrabber({
        'root_url': _url.text.trim(),
        'max_depth': _depth,
        'include_filter': _include.text.trim(),
        'exclude_filter': _exclude.text.trim(),
        'save_directory': _dir.text.trim(),
        'follow_links': true,
        'images': _images,
        'css': _css,
        'scripts': _scripts,
        'video': _video,
        'rewrite_for_offline': _offline,
        // v1.8.0 (IDM Grabber wizard steps 3+4)
        'explore_parent_directories': _exploreParents,
        'explore_subdomains': _exploreSubdomains,
        'external_depth': _externalDepth,
        'min_bytes': (double.tryParse(_minKb.text.trim()) ?? 0) > 0
            ? (double.parse(_minKb.text.trim()) * 1024).round()
            : 0,
        'max_bytes': (double.tryParse(_maxKb.text.trim()) ?? 0) > 0
            ? (double.parse(_maxKb.text.trim()) * 1024).round()
            : 0,
      });
      if (mounted) {
        setState(() => _lastRun = runId);
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text('${s.startGrabbing}: $runId')));
      }
    } finally {
      if (mounted) setState(() => _running = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = S.of(context);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        Text(s.grabber, style: Theme.of(context).textTheme.headlineSmall),
        const SizedBox(height: 16),
        TextField(
          controller: _url,
          decoration: InputDecoration(
            labelText: s.rootUrl,
            border: const OutlineInputBorder(),
            prefixIcon: const Icon(Icons.public_rounded),
          ),
        ),
        const SizedBox(height: 12),
        Row(children: [
          SizedBox(
            width: 130,
            child: TextField(
              keyboardType: TextInputType.number,
              controller: TextEditingController(text: '$_depth'),
              decoration: InputDecoration(labelText: s.depth, border: const OutlineInputBorder()),
              onSubmitted: (v) => setState(() => _depth = int.tryParse(v) ?? 3),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: TextField(
              controller: _dir,
              decoration: InputDecoration(labelText: s.destination, border: const OutlineInputBorder()),
            ),
          ),
        ]),
        const SizedBox(height: 12),
        TextField(
          controller: _include,
          decoration: InputDecoration(labelText: s.includeFilter, border: const OutlineInputBorder()),
        ),
        const SizedBox(height: 12),
        TextField(
          controller: _exclude,
          decoration: InputDecoration(labelText: s.excludeFilter, border: const OutlineInputBorder()),
        ),
        const SizedBox(height: 16),
        Wrap(
          spacing: 12,
          runSpacing: 6,
          children: [
            FilterChip(label: Text(s.images), selected: _images, onSelected: (v) => setState(() => _images = v)),
            FilterChip(label: Text(s.stylesheets), selected: _css, onSelected: (v) => setState(() => _css = v)),
            FilterChip(label: Text(s.scripts), selected: _scripts, onSelected: (v) => setState(() => _scripts = v)),
            FilterChip(label: Text(s.grabVideo), selected: _video, onSelected: (v) => setState(() => _video = v)),
            FilterChip(label: Text(s.offlineRewrite), selected: _offline, onSelected: (v) => setState(() => _offline = v)),
            // v1.8.0 (IDM Grabber wizard step 3)
            FilterChip(label: Text(s.grabParents), selected: _exploreParents, onSelected: (v) => setState(() => _exploreParents = v)),
            FilterChip(label: Text(s.grabSubdomains), selected: _exploreSubdomains, onSelected: (v) => setState(() => _exploreSubdomains = v)),
          ],
        ),
        const SizedBox(height: 12),
        // v1.8.0 (IDM Grabber wizard steps 3+4): levels on other sites + size limits
        Row(children: [
          SizedBox(
            width: 170,
            child: TextField(
              keyboardType: TextInputType.number,
              controller: TextEditingController(text: '$_externalDepth'),
              decoration: InputDecoration(labelText: s.grabExternalDepth, border: const OutlineInputBorder()),
              onSubmitted: (v) => setState(() => _externalDepth = int.tryParse(v) ?? 0),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: TextField(
              keyboardType: TextInputType.number,
              controller: _minKb,
              decoration: InputDecoration(labelText: s.grabMinSizeKb, border: const OutlineInputBorder()),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: TextField(
              keyboardType: TextInputType.number,
              controller: _maxKb,
              decoration: InputDecoration(labelText: s.grabMaxSizeKb, border: const OutlineInputBorder()),
            ),
          ),
        ]),
        const SizedBox(height: 20),
        Row(children: [
          FilledButton.icon(
            onPressed: _running || _url.text.trim().isEmpty ? null : _start,
            icon: _running
                ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                : const Icon(Icons.travel_explore_rounded),
            label: Text(s.startGrabbing),
          ),
          if (_lastRun != null) ...[
            const SizedBox(width: 12),
            Text('${s.grabberProjects}: $_lastRun', style: Theme.of(context).textTheme.bodySmall),
          ],
        ]),
      ],
    );
  }
}
