using Nova.Domain;
using Nova.Engine;


namespace Nova.Media;

/// <summary>
/// Server-side media analysis for the browser quality panel:
/// given a detected URL (direct file / HLS / DASH), returns verified candidates.
/// Sizes are exact only when computable; otherwise flagged as estimates or omitted.
/// </summary>
public sealed class MediaCandidatesService
{
    private readonly HttpClient _http;

    public MediaCandidatesService(HttpClient http) => _http = http;

    public async Task<MediaCandidatesResponse> AnalyzeAsync(MediaProbeRequest req, CancellationToken ct)
    {
        var result = new MediaCandidatesResponse { PageUrl = req.Referer ?? req.Url };

        // direct file?
        using (var probe = new HttpRequestMessage(HttpMethod.Get, req.Url))
        {
            probe.Headers.UserAgent.ParseAdd("NovaDownloadManager/1.0");
            if (req.Referer is { } r && Uri.TryCreate(r, UriKind.Absolute, out var ru))
                probe.Headers.Referrer = ru;
            using var resp = await _http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";

            if (HlsParser.LooksLikeHls(contentType, req.Url) || req.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    result.Candidates.Add(new MediaCandidate
                    {
                        Url = req.Url, Kind = "hls", Label = "HLS stream",
                        Downloadable = false, Note = $"Playlist unreachable ({(int)resp.StatusCode}).",
                    });
                    return result;
                }
                var body = await resp.Content.ReadAsStringAsync(ct);
                return AnalyzeHls(body, req.Url, result);
            }
            if (req.Url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) || contentType.Contains("dash+xml"))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    result.Candidates.Add(Dead(req.Url, "dash", $"Manifest unreachable ({(int)resp.StatusCode})."));
                    return result;
                }
                var xml = await resp.Content.ReadAsStringAsync(ct);
                return AnalyzeDash(xml, req.Url, result);
            }

            // plain media file
            var size = resp.Content.Headers.ContentLength;
            var filename = FilenamePolicy.FromContentDisposition(
                resp.Content.Headers.ContentDisposition?.ToString()) ?? FilenamePolicy.FromUrl(req.Url) ?? "video";
            var ext = Path.GetExtension(filename).ToLowerInvariant();
            var isMedia = contentType.StartsWith("video/") || contentType.StartsWith("audio/")
                          || ext is ".mp4" or ".webm" or ".mkv" or ".avi" or ".mov" or ".m4v" or ".mp3" or ".m4a" or ".ogg";
            result.Candidates.Add(new MediaCandidate
            {
                Id = "direct",
                Url = resp.RequestMessage?.RequestUri?.ToString() ?? req.Url,
                Kind = "file",
                Container = string.IsNullOrEmpty(ext) ? contentType : ext.TrimStart('.'),
                SizeBytes = size,
                SizeIsEstimate = false,
                Label = isMedia ? "Direct media file" : "Direct file",
                Downloadable = true,
            });
            return result;
        }
    }

    private MediaCandidatesResponse AnalyzeHls(string body, string url, MediaCandidatesResponse result)
    {
        var playlist = HlsParser.Parse(body, url);
        if (playlist.Kind == HlsKind.Master)
        {
            foreach (var v in playlist.Variants.OrderByDescending(v => v.Bandwidth))
            {
                result.Candidates.Add(new MediaCandidate
                {
                    Id = v.Url,
                    Url = v.Url,
                    Kind = "hls",
                    Container = "ts",
                    Width = v.Width,
                    Height = v.Height,
                    Bitrate = v.Bandwidth > 0 ? $"{v.Bandwidth / 1000} kbps" : null,
                    Codecs = v.Codecs,
                    Label = v.Height is > 0 ? $"{v.Height}p" : (v.Bandwidth > 0 ? $"{v.Bandwidth / 1000} kbps" : "variant"),
                    Downloadable = true,
                    Note = v.Width is null && v.Height is null ? "Resolution not declared by server" : null,
                });
            }
        }
        else
        {
            var m = playlist.Media!;
            result.Candidates.Add(new MediaCandidate
            {
                Id = url,
                Url = url,
                Kind = "hls",
                Container = m.InitUrl is null ? "ts" : "mp4",
                Label = m.IsLive ? "Live stream (not downloadable)" : $"HLS ({m.SegmentUrls.Count} segments)",
                // v1.7.0: AES-128 (identity keyformat) is downloaded + decrypted; only real DRM is refused.
                Downloadable = !m.IsLive && !m.DrmProtected,
                Note = m.DrmProtected ? "DRM-protected stream — not supported"
                    : m.IsLive ? "Live streams are not supported"
                    : m.Encrypted ? "AES-128 encrypted — supported, decrypted automatically" : null,
            });
            result.DrmDetected = m.DrmProtected;
        }
        return result;
    }

    private MediaCandidatesResponse AnalyzeDash(string xml, string url, MediaCandidatesResponse result)
    {
        DashManifest mpd;
        try { mpd = DashParser.Parse(xml, url); }
        catch (InvalidOperationException)
        {
            result.Candidates.Add(Dead(url, "dash", "Manifest could not be parsed."));
            return result;
        }
        result.DrmDetected = mpd.HasEncryption;
        if (mpd.HasEncryption)
        {
            result.Candidates.Add(Dead(url, "dash", "Protected stream (DRM) — not supported."));
            return result;
        }

        foreach (var v in mpd.Representations.Where(r => r.Type == "video")
                     .OrderByDescending(r => r.Bandwidth))
        {
            var size = v.DurationSec is > 0 ? (long?)(v.Bandwidth / 8 * (long)Math.Ceiling(v.DurationSec ?? 0)) : null;
            result.Candidates.Add(new MediaCandidate
            {
                Id = v.Id,
                Url = url,
                Kind = "dash",
                Container = "mp4",
                Width = v.Width,
                Height = v.Height,
                SizeBytes = size,
                SizeIsEstimate = true, // derived from bitrate × duration
                Bitrate = v.Bandwidth > 0 ? $"{v.Bandwidth / 1000} kbps" : null,
                Codecs = v.Codecs,
                HasAudio = false, // video-only track; audio merges when muxed
                Label = v.Height is > 0 ? $"{v.Height}p" : v.Id,
                Downloadable = true,
            });
        }
        foreach (var a in mpd.Representations.Where(r => r.Type == "audio").OrderByDescending(r => r.Bandwidth))
        {
            result.Candidates.Add(new MediaCandidate
            {
                Id = "audio:" + a.Id,
                Url = url,
                Kind = "dash",
                Container = "m4a",
                Bitrate = a.Bandwidth > 0 ? $"{a.Bandwidth / 1000} kbps" : null,
                Codecs = a.Codecs,
                HasVideo = false,
                HasAudio = true,
                Label = $"Audio track{(a.Codecs is null ? "" : $" ({a.Codecs})")}",
                Downloadable = true,
            });
        }
        return result;
    }

    private static MediaCandidate Dead(string url, string kind, string note) => new()
    {
        Id = url,
        Url = url,
        Kind = kind,
        Downloadable = false,
        Note = note,
        Label = "Unavailable",
    };
}
