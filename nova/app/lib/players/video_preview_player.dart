import 'dart:io';

import 'package:flutter/material.dart';
import 'package:media_kit/media_kit.dart';
import 'package:media_kit_video/media_kit_video.dart';

/// Sequential video preview: plays the .novapart file while the engine
/// keeps writing it. Works because TS/MP4 sequential segments arrive in
/// order; the player treats the file as a growing stream.
///
/// This is the flagship differentiator: "watch while downloading".
class VideoPreviewPlayer extends StatefulWidget {
  final String filePath; // path to .novapart or final file
  final String title;

  const VideoPreviewPlayer({
    super.key,
    required this.filePath,
    required this.title,
  });

  @override
  State<VideoPreviewPlayer> createState() => _VideoPreviewPlayerState();
}

class _VideoPreviewPlayerState extends State<VideoPreviewPlayer> {
  late final Player _player;
  late final VideoController _controller;

  @override
  void initState() {
    super.initState();
    MediaKit.ensureInitialized();
    _player = Player();
    _controller = VideoController(_player);
    _player.open(Media('file://${widget.filePath}'));
  }

  @override
  void dispose() {
    _player.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final exists = File(widget.filePath).existsSync();
    return Scaffold(
      backgroundColor: Colors.black,
      appBar: AppBar(
        title: Text(widget.title),
        backgroundColor: Colors.transparent,
        foregroundColor: Colors.white,
      ),
      body: exists
          ? Video(
              controller: _controller,
              controls: AdaptiveVideoControls,
            )
          : const Center(
              child: Text(
                'File not found — start the download first',
                style: TextStyle(color: Colors.white70),
              ),
            ),
    );
  }
}
