import 'dart:convert';
import 'dart:io';

/// Minimal bencode reader — just enough to list a local .torrent's files
/// (v1.4.1 per-file selection in the Add dialog). Never throws: [tryParseTorrent]
/// returns null on anything malformed.
class TorrentFileEntry {
  final String subPath; // "a/b/c.bin" joined with '/'
  final int length;
  const TorrentFileEntry(this.subPath, this.length);
}

class TorrentPreview {
  final String name;
  final bool isSingleFile;
  final List<TorrentFileEntry> files;
  const TorrentPreview(this.name, this.isSingleFile, this.files);
}

/// Parses a .torrent file; returns null for missing/invalid files.
TorrentPreview? tryParseTorrent(String path) {
  try {
    final bytes = File(path).readAsBytesSync();
    final decoded = _BencReader(bytes).readValue();
    if (decoded is! Map) return null;
    final info = decoded['info'];
    if (info is! Map) return null;
    final name = _decodeBytes(info['name']) ?? '';
    final files = <TorrentFileEntry>[];
    if (info['files'] is List) {
      for (final f in (info['files'] as List)) {
        if (f is! Map) continue;
        final len = (f['length'] as num?)?.toInt() ?? 0;
        final pathParts = <String>[];
        if (f['path'] is List) {
          for (final p in (f['path'] as List)) {
            final s = _decodeBytes(p);
            if (s != null) pathParts.add(s);
          }
        }
        files.add(TorrentFileEntry(pathParts.join('/'), len));
      }
      return TorrentPreview(name, false, files);
    }
    // single-file
    final len = (info['length'] as num?)?.toInt() ?? 0;
    files.add(TorrentFileEntry(name, len));
    return TorrentPreview(name, true, files);
  } catch (_) {
    return null;
  }
}

String? _decodeBytes(Object? v) {
  if (v is String) return v;
  if (v is List<int>) {
    try {
      return utf8.decode(v, allowMalformed: true);
    } catch (_) {
      return null;
    }
  }
  return null;
}

class _BencReader {
  final Uint8ListLike _b;
  int _pos = 0;
  _BencReader(List<int> b) : _b = b;

  Object? readValue() {
    if (_pos >= _b.length) return null;
    final c = _b[_pos];
    if (c == 0x64) return _readDict(); // d
    if (c == 0x6C) return _readList(); // l
    if (c == 0x69) return _readInt(); // i
    if (c >= 0x30 && c <= 0x39) return _readBytes();
    return null; // terminator (e) handled by callers
  }

  Map<dynamic, dynamic> _readDict() {
    _pos++; // d
    final map = <dynamic, dynamic>{};
    while (_pos < _b.length && _b[_pos] != 0x65) {
      final key = _readBytes();
      if (key == null) break;
      final value = readValue();
      map[key is String ? key : _bytesToList(key)] = value;
    }
    _pos++; // e
    return map;
  }

  List<dynamic> _readList() {
    _pos++; // l
    final list = <dynamic>[];
    while (_pos < _b.length && _b[_pos] != 0x65) {
      final v = readValue();
      if (v == null && _pos < _b.length && _b[_pos] != 0x65) break;
      list.add(v);
    }
    _pos++; // e
    return list;
  }

  int _readInt() {
    _pos++; // i
    var end = _pos;
    while (end < _b.length && _b[end] != 0x65) end++;
    final s = latin1.decode(_b.sublist(_pos, end));
    _pos = end + 1;
    return int.tryParse(s) ?? 0;
  }

  Uint8ListLike? _readBytes() {
    var colon = _pos;
    while (colon < _b.length && _b[colon] != 0x3A) colon++;
    if (colon >= _b.length) return null;
    final len = int.tryParse(latin1.decode(_b.sublist(_pos, colon)));
    if (len == null || len < 0 || colon + 1 + len > _b.length) return null;
    final data = _b.sublist(colon + 1, colon + 1 + len);
    _pos = colon + 1 + len;
    return data;
  }
}

//dart:typed_data via List<int> is enough; alias keeps signatures short.
typedef Uint8ListLike = List<int>;

List<int> _bytesToList(List<int> b) => b;
