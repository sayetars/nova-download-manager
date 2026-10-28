using System.Globalization;

namespace Nova.Media;

/// <summary>Parsed HLS master playlist variant.</summary>
public sealed record HlsVariant
{
    public string Url { get; init; } = "";
    public long Bandwidth { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Codecs { get; init; }
    public string? Resolution { get; init; }
}

/// <summary>EXT-X-KEY tag (RFC 8216 §4.3.2.4). Applies to subsequent segments until the next key tag.</summary>
public sealed record HlsKey
{
    /// <summary>NONE | AES-128 | SAMPLE-AES (upper-cased).</summary>
    public string Method { get; init; } = "NONE";

    /// <summary>Absolute key URI (resolved against the playlist).</summary>
    public string Uri { get; init; } = "";

    /// <summary>Explicit 16-byte IV when the tag carries one; null → IV = media sequence number.</summary>
    public byte[]? Iv { get; init; }

    /// <summary>KEYFORMAT; anything other than "identity" is a DRM key container we cannot process.</summary>
    public string KeyFormat { get; init; } = "identity";

    public bool IsAes128 => Method == "AES-128" && KeyFormat == "identity";
}

/// <summary>Parsed media playlist.</summary>
public sealed record HlsMedia
{
    public required List<string> SegmentUrls { get; init; }

    /// <summary>Parallel to <see cref="SegmentUrls"/>: index into <see cref="Keys"/>, or -1 for plaintext.</summary>
    public IReadOnlyList<int> SegmentKeys { get; init; } = Array.Empty<int>();

    /// <summary>Distinct EXT-X-KEY tags in first-appearance order.</summary>
    public IReadOnlyList<HlsKey> Keys { get; init; } = Array.Empty<HlsKey>();

    /// <summary>EXT-X-MEDIA-SEQUENCE (sequence number of the first segment; IV fallback base).</summary>
    public long MediaSequence { get; init; }

    /// <summary>Any applicable key with METHOD != NONE.</summary>
    public bool Encrypted { get; init; }

    /// <summary>True when an applicable key is SAMPLE-AES or KEYFORMAT != identity — real DRM, always refused.</summary>
    public bool DrmProtected { get; init; }

    public string? InitUrl { get; init; }
    public bool IsLive => !HasEndList;
    public bool HasEndList { get; init; }
    public int TargetDurationSec { get; init; } = 4;
}

public static class HlsParser
{
    public static bool LooksLikeHls(string contentType, string urlOrBody)
    {
        if (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) return true;
        return urlOrBody.Contains("#EXTM3U", StringComparison.Ordinal);
    }

    /// <summary>Parses an m3u8. Master (has #EXT-X-STREAM-INF) → variants; else → media playlist.</summary>
    public static HlsPlaylist Parse(string body, string playlistUrl)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var isMaster = lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal));
        if (isMaster)
        {
            var variants = new List<HlsVariant>();
            for (var i = 0; i < lines.Length - 1; i++)
            {
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) continue;
                var url = lines[i + 1];
                if (url.StartsWith("#")) continue;
                var attr = lines[i][("#EXT-X-STREAM-INF:").Length..];
                variants.Add(new HlsVariant
                {
                    Url = ResolveUrl(playlistUrl, url),
                    Bandwidth = GetAttrLong(attr, "BANDWIDTH") ?? 0,
                    Width = GetAttrInt2(attr, "RESOLUTION", out var h) is { } w ? w : null,
                    Height = h,
                    Codecs = GetAttr(attr, "CODECS"),
                });
            }
            return new HlsPlaylist { Kind = HlsKind.Master, Variants = variants };
        }
        else
        {
            var segs = new List<string>();
            var segKeys = new List<int>();
            var keys = new List<HlsKey>();
            string? init = null;
            var endList = false;
            var mediaSeq = 0L;
            var curKey = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
                {
                    var method = (GetAttr(l, "METHOD") ?? "NONE").ToUpperInvariant();
                    if (string.Equals(method, "NONE", StringComparison.Ordinal))
                    {
                        curKey = -1; // explicit un-encryption from here on
                        continue;
                    }
                    var uri = GetAttr(l, "URI") ?? "";
                    var key = new HlsKey
                    {
                        Method = method,
                        Uri = ResolveUrl(playlistUrl, uri),
                        Iv = HlsCrypt.ParseIv(GetAttr(l, "IV")),
                        KeyFormat = GetAttr(l, "KEYFORMAT") ?? "identity",
                    };
                    // playlists commonly repeat the same key tag per segment — dedupe by content
                    var idx = keys.FindIndex(k => k.Method == key.Method && k.Uri == key.Uri
                                                   && k.KeyFormat == key.KeyFormat && IvEquals(k.Iv, key.Iv));
                    if (idx < 0)
                    {
                        keys.Add(key);
                        idx = keys.Count - 1;
                    }
                    curKey = idx;
                }
                else if (l.StartsWith("#EXT-X-MEDIA-SEQUENCE", StringComparison.Ordinal))
                {
                    var v = l.Split(':', 2);
                    if (v.Length == 2) long.TryParse(v[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out mediaSeq);
                }
                else if (l.StartsWith("#EXT-X-MAP", StringComparison.Ordinal))
                {
                    var uri = GetAttr(l, "URI");
                    if (uri is not null) init = ResolveUrl(playlistUrl, uri);
                }
                else if (l.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
                {
                    endList = true;
                }
                else if (!l.StartsWith("#"))
                {
                    segs.Add(ResolveUrl(playlistUrl, l));
                    segKeys.Add(curKey);
                }
            }

            var encrypted = false;
            var drm = false;
            for (var i = 0; i < segKeys.Count; i++)
            {
                if (segKeys[i] < 0) continue;
                var k = keys[segKeys[i]];
                if (k.Method == "NONE") continue;
                encrypted = true;
                if (k.Method == "SAMPLE-AES" || !string.Equals(k.KeyFormat, "identity", StringComparison.Ordinal))
                    drm = true;
            }

            return new HlsPlaylist
            {
                Kind = HlsKind.Media,
                Media = new HlsMedia
                {
                    SegmentUrls = segs,
                    SegmentKeys = segKeys,
                    Keys = keys,
                    MediaSequence = mediaSeq,
                    Encrypted = encrypted,
                    DrmProtected = drm,
                    InitUrl = init,
                    HasEndList = endList,
                },
            };
        }
    }

    private static bool IvEquals(byte[]? a, byte[]? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public static string ResolveUrl(string baseUrl, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out _)) return url;
        return new Uri(new Uri(baseUrl), url).ToString();
    }

    private static string? GetAttr(string attrs, string name)
    {
        // handles quoted and unquoted: BANDWIDTH=123456, CODECS="avc1.42E01E,mp4a.40.2"
        var idx = attrs.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = attrs[(idx + name.Length + 1)..];
        if (rest.StartsWith('"'))
        {
            var end = rest.IndexOf('"', 1);
            return end > 1 ? rest[1..end] : null;
        }
        var endComma = rest.IndexOf(',');
        return endComma >= 0 ? rest[..endComma] : rest;
    }

    private static long? GetAttrLong(string attrs, string name) =>
        long.TryParse(GetAttr(attrs, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? GetAttrInt2(string attrs, string name, out int? second)
    {
        second = null;
        var raw = GetAttr(attrs, name); // e.g. RESOLUTION=1920x1080
        if (raw is null) return null;
        var parts = raw.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
        {
            second = h;
            return w;
        }
        return null;
    }
}

public enum HlsKind { Master, Media }

public sealed record HlsPlaylist
{
    public required HlsKind Kind { get; init; }
    public IReadOnlyList<HlsVariant> Variants { get; init; } = Array.Empty<HlsVariant>();
    public HlsMedia? Media { get; init; }
}
