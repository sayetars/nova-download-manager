using Nova.Domain;
using Nova.Engine;

namespace Nova.Media;

/// <summary>
/// Implements IMediaPipeline for the engine: HLS + DASH segment downloads, container assembly,
/// optional ffmpeg mux for separate audio/video representations.
/// NEVER touches DRM-protected content (detects and refuses).
/// </summary>
public sealed class MediaPipeline : IMediaPipeline
{
    private const int MaxSegmentRetries = 3;

    public async Task RunMediaTaskAsync(DownloadTaskContext ctx, CancellationToken ct)
    {
        var task = ctx.Task;
        var selection = ParseSelection(task.MediaSelection);

        if (task.Kind == DownloadKind.Hls)
        {
            await RunHlsAsync(ctx, selection, ct);
        }
        else if (task.Kind == DownloadKind.Dash)
        {
            await RunDashAsync(ctx, selection, ct);
        }
        else
        {
            throw new MediaProcessException("unsupported_kind", ErrorCategory.Media, "Unknown media task kind.");
        }
    }

    // ---- HLS ----------------------------------------------------------------

    private async Task RunHlsAsync(DownloadTaskContext ctx, MediaSelection sel, CancellationToken ct)
    {
        var task = ctx.Task;
        var startUrl = sel.VariantUrl ?? task.Url;

        var masterText = await FetchTextAsync(ctx, startUrl, ct);
        if (!HlsParser.LooksLikeHls("", masterText))
            throw new MediaProcessException("bad_playlist", ErrorCategory.Media, "The HLS playlist is invalid.");

        var playlist = HlsParser.Parse(masterText, startUrl);

        if (playlist.Kind == HlsKind.Master)
        {
            var variant = SelectVariant(playlist.Variants, sel);
            if (variant is null)
                throw new MediaProcessException("no_variants", ErrorCategory.Media, "No playable video variants found in the HLS playlist.");
            var mediaText = await FetchTextAsync(ctx, variant.Url, ct);
            playlist = HlsParser.Parse(mediaText, variant.Url);
            if (playlist.Kind != HlsKind.Media)
                throw new MediaProcessException("nested_master", ErrorCategory.Media, "Unexpected nested master playlist.");
        }

        var media = playlist.Media!;
        if (media.DrmProtected)
            throw new MediaProcessException("drm_protected", ErrorCategory.Media,
                "This stream is DRM-protected (SAMPLE-AES or an external key system) and cannot be downloaded.");
        if (media.IsLive)
            throw new MediaProcessException("live_stream", ErrorCategory.Media,
                "Live streams are not supported; wait until the stream has ended.");

        if (media.Encrypted)
        {
            // v1.7.0 — AES-128 (KEYFORMAT=identity) is decrypted on the fly; DRM was already refused above.
            await DownloadHlsEncryptedAsync(ctx, media, ct);
        }
        else
        {
            var segments = media.SegmentUrls;
            if (media.InitUrl is not null) segments = new[] { media.InitUrl }.Concat(segments).ToList();
            await DownloadSegmentsAsync(ctx, segments, ct);
        }
        var finalPath = ctx.Finalize();
        EngineLog.Info($"HLS assembled: {finalPath}");
    }

    public static HlsVariant? SelectVariant(IReadOnlyList<HlsVariant> variants, MediaSelection sel)
    {
        if (variants.Count == 0) return null;
        if (sel.PreferredHeight is > 0)
        {
            var exact = variants.Where(v => v.Height == sel.PreferredHeight)
                .OrderByDescending(v => v.Bandwidth).FirstOrDefault();
            if (exact is not null) return exact;
        }
        return sel.PreferLowest
            ? variants.OrderBy(v => v.Bandwidth).First()
            : variants.OrderByDescending(v => v.Bandwidth).First();
    }

    // ---- DASH ---------------------------------------------------------------

    private async Task RunDashAsync(DownloadTaskContext ctx, MediaSelection sel, CancellationToken ct)
    {
        var task = ctx.Task;
        var mpdText = await FetchTextAsync(ctx, task.Url, ct);
        DashManifest mpd;
        try { mpd = DashParser.Parse(mpdText, task.Url); }
        catch (InvalidOperationException ex)
        {
            throw new MediaProcessException("bad_mpd", ErrorCategory.Media, ex.Message);
        }

        if (mpd.HasEncryption)
            throw new MediaProcessException("drm_protected", ErrorCategory.Media,
                "This stream is protected (DRM) and cannot be downloaded.");

        var videos = mpd.Representations.Where(r => r.Type == "video").OrderByDescending(r => r.Bandwidth).ToList();
        var audios = mpd.Representations.Where(r => r.Type == "audio").OrderByDescending(r => r.Bandwidth).ToList();

        var video = sel.VideoRepId is not null ? videos.FirstOrDefault(v => v.Id == sel.VideoRepId) ?? videos.FirstOrDefault()
                    : videos.FirstOrDefault();
        var audio = sel.AudioRepId is not null ? audios.FirstOrDefault(a => a.Id == sel.AudioRepId)
                    : sel.AudioRepId == null && sel.NoAudio ? null : audios.FirstOrDefault();

        if (video is null && audio is null)
            throw new MediaProcessException("no_representations", ErrorCategory.Media, "No downloadable representations found.");

        // Single-track download (video only, or audio only)
        if (video is null || audio is null)
        {
            var track = video ?? audio!;
            var segs = new List<string>();
            if (track.InitUrl is not null) segs.Add(track.InitUrl);
            segs.AddRange(track.SegmentUrls);
            await DownloadSegmentsAsync(ctx, segs, ct);
            var path = ctx.Finalize();
            EngineLog.Info($"DASH track saved: {path}");
            return;
        }

        // Separate video+audio: download both, mux with ffmpeg when available.
        var videoPath = await DownloadRepresentationToTempAsync(ctx, video, ".video.tmp", ct);
        string? audioPath = null;
        try
        {
            audioPath = await DownloadRepresentationToTempAsync(ctx, audio, ".audio.tmp", ct);

            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null)
            {
                // Keep both files with clear suffixes — documented limitation, no fake muxing.
                var vFinal = ctx.Finalize();
                var aFinal = vFinal + ".audio" + Path.GetExtension(audioPath ?? ".m4a");
                File.Move(audioPath, aFinal);
                EngineLog.Warn("ffmpeg not found; video and audio saved as separate files.");
                return;
            }

            var mergedTemp = ctx.TempOutputPath;
            var exit = await RunFfmpegAsync(ffmpeg,
                $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c copy \"{mergedTemp}\"", ct);
            if (exit != 0 || !File.Exists(mergedTemp) || new FileInfo(mergedTemp).Length == 0)
                throw new MediaProcessException("mux_failed", ErrorCategory.Media,
                    "Combining video and audio failed (ffmpeg).");
            var finalPath = ctx.Finalize();
            EngineLog.Info($"DASH muxed: {finalPath}");
        }
        finally
        {
            TryDelete(videoPath);
            TryDelete(audioPath);
        }
    }

    private async Task<string> DownloadRepresentationToTempAsync(DownloadTaskContext ctx,
        DashRepresentation rep, string suffix, CancellationToken ct)
    {
        var tmp = ctx.TempOutputPath + suffix;
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var segs = new List<string>();
            if (rep.InitUrl is not null) segs.Add(rep.InitUrl);
            segs.AddRange(rep.SegmentUrls);
            var done = 0;
            foreach (var url in segs)
            {
                await DownloadSegmentToFileAsync(ctx, url, fs, ct);
                done++;
                ctx.OnSegments(done, segs.Count);
            }
        }
        return tmp;
    }

    // ---- HLS AES-128 (RFC 8216 §5.2) -----------------------------------------

    /// <summary>Downloads an AES-128 encrypted HLS playlist: fetches key(s), decrypts each segment
    /// (IV = tag IV or the segment's media-sequence number), writes plaintext. Mixed key/plain playlists work.</summary>
    private async Task DownloadHlsEncryptedAsync(DownloadTaskContext ctx, HlsMedia media, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(ctx.TempOutputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var fs = new FileStream(ctx.TempOutputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (media.InitUrl is not null) // initialization section is never encrypted (RFC 8216 §4.3.2.5)
            await DownloadSegmentToFileAsync(ctx, media.InitUrl, fs, ct);

        var total = media.SegmentUrls.Count;
        var keyCache = new Dictionary<string, byte[]>();
        for (var i = 0; i < total; i++)
        {
            var kIdx = i < media.SegmentKeys.Count ? media.SegmentKeys[i] : -1;
            var url = media.SegmentUrls[i];

            if (kIdx < 0 || media.Keys[kIdx].Method == "NONE")
            {
                await DownloadSegmentToFileAsync(ctx, url, fs, ct);
            }
            else
            {
                var k = media.Keys[kIdx];
                if (!k.IsAes128)
                    throw new MediaProcessException("drm_protected", ErrorCategory.Media,
                        $"Unsupported key method '{k.Method}' (keyformat '{k.KeyFormat}') — DRM streams cannot be downloaded.");
                if (!keyCache.TryGetValue(k.Uri, out var key))
                {
                    key = await FetchHlsKeyAsync(ctx, k.Uri, ct);
                    keyCache[k.Uri] = key;
                }
                var iv = k.Iv ?? HlsCrypt.IvFromSequence(media.MediaSequence + i);
                var cipher = await DownloadSegmentBytesAsync(ctx, url, ct);
                var plain = HlsCrypt.DecryptSegment(cipher, key, iv);
                await fs.WriteAsync(plain, ct);
            }
            ctx.OnSegments(i + 1, total);
        }
        await fs.FlushAsync(ct);
    }

    private async Task<byte[]> FetchHlsKeyAsync(DownloadTaskContext ctx, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(Nova.Domain.VersionInfo.UserAgent);
                if (ctx.Task.Referer is { } r && Uri.TryCreate(r, UriKind.Absolute, out var ru))
                    req.Headers.Referrer = ru;
                using var resp = await ctx.Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    throw new MediaProcessException("key_http_" + (int)resp.StatusCode, ErrorCategory.Http,
                        $"Key fetch failed ({(int)resp.StatusCode}).", retryable: (int)resp.StatusCode >= 500);
                var key = await resp.Content.ReadAsByteArrayAsync(ct);
                if (key.Length != 16)
                    throw new MediaProcessException("bad_key", ErrorCategory.Media,
                        $"AES-128 key must be 16 bytes, got {key.Length}.");
                return key;
            }
            catch (MediaProcessException ex) when (ex.Retryable && attempt < MaxSegmentRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), ct);
            }
        }
    }

    /// <summary>Fetches a segment into memory (bounded by HlsCrypt.MaxInMemorySegmentBytes) for decryption.</summary>
    private async Task<byte[]> DownloadSegmentBytesAsync(DownloadTaskContext ctx, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(Nova.Domain.VersionInfo.UserAgent);
                if (ctx.Task.Referer is { } r && Uri.TryCreate(r, UriKind.Absolute, out var ru))
                    req.Headers.Referrer = ru;
                using var resp = await ctx.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                    throw new MediaProcessException("segment_http_" + (int)resp.StatusCode,
                        ErrorCategory.Http, $"Segment download failed ({(int)resp.StatusCode}).", retryable: (int)resp.StatusCode >= 500);
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var buf = new MemoryStream();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var g = ctx.GlobalLimiter();
                    if (g is not null) await g.WaitAsync(64 * 1024, ct);
                    if (ctx.TaskLimiter is not null) await ctx.TaskLimiter.WaitAsync(64 * 1024, ct);
                    var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (n <= 0) break;
                    await buf.WriteAsync(buffer.AsMemory(0, n), ct);
                    ctx.OnBytes(n);
                    if (buf.Length > HlsCrypt.MaxInMemorySegmentBytes)
                        throw new MediaProcessException("segment_too_large", ErrorCategory.Media,
                            "Encrypted segment exceeds the in-memory limit.");
                }
                return buf.ToArray();
            }
            catch (MediaProcessException ex) when (ex.Retryable && attempt < MaxSegmentRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), ct);
            }
            catch (IOException) when (attempt < MaxSegmentRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    // ---- shared -------------------------------------------------------------

    private async Task DownloadSegmentsAsync(DownloadTaskContext ctx, List<string> segments, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(ctx.TempOutputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var fs = new FileStream(ctx.TempOutputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var done = 0;
        foreach (var url in segments)
        {
            await DownloadSegmentToFileAsync(ctx, url, fs, ct);
            done++;
            ctx.OnSegments(done, segments.Count);
        }
    }

    private async Task DownloadSegmentToFileAsync(DownloadTaskContext ctx, string url,
        FileStream fs, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(Nova.Domain.VersionInfo.UserAgent);
                if (ctx.Task.Referer is { } r && Uri.TryCreate(r, UriKind.Absolute, out var ru))
                    req.Headers.Referrer = ru;
                using var resp = await ctx.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                    throw new MediaProcessException("segment_http_" + (int)resp.StatusCode,
                        ErrorCategory.Http, $"Segment download failed ({(int)resp.StatusCode}).", retryable: (int)resp.StatusCode >= 500);
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var g = ctx.GlobalLimiter();
                    if (g is not null) await g.WaitAsync(64 * 1024, ct);
                    if (ctx.TaskLimiter is not null) await ctx.TaskLimiter.WaitAsync(64 * 1024, ct);
                    var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (n <= 0) break;
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    ctx.OnBytes(n);
                }
                await fs.FlushAsync(ct);
                return;
            }
            catch (MediaProcessException ex) when (ex.Retryable && attempt < MaxSegmentRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), ct);
            }
            catch (IOException) when (attempt < MaxSegmentRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private static async Task<string> FetchTextAsync(DownloadTaskContext ctx, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(Nova.Domain.VersionInfo.UserAgent);
        if (ctx.Task.Referer is { } r && Uri.TryCreate(r, UriKind.Absolute, out var ru))
            req.Headers.Referrer = ru;
        using var resp = await ctx.Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new MediaProcessException("manifest_http_" + (int)resp.StatusCode, ErrorCategory.Http,
                $"The playlist could not be fetched ({(int)resp.StatusCode}).", retryable: (int)resp.StatusCode >= 500);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string? FindFfmpeg()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
            catch { /* ignore malformed PATH */ }
        }
        return null;
    }

    private static async Task<int> RunFfmpegAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { proc.Kill(); } catch { } });
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    internal static MediaSelection ParseSelection(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new MediaSelection();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<MediaSelection>(json,
                       new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new MediaSelection();
        }
        catch { return new MediaSelection(); }
    }
}

public sealed class MediaSelection
{
    public string? VariantUrl { get; set; }
    public int? PreferredHeight { get; set; }
    public bool PreferLowest { get; set; }
    public string? VideoRepId { get; set; }
    public string? AudioRepId { get; set; }
    public bool NoAudio { get; set; }
}
