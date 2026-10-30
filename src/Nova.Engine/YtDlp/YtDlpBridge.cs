using System.Diagnostics;
using System.Text.Json;
using Nova.Domain;

namespace Nova.Engine.YtDlp;

/// <summary>One selectable yt-dlp format row (UI quality panel).</summary>
public sealed class YtDlpFormat
{
    public string FormatId { get; set; } = "";
    public string Ext { get; set; } = "";
    public string? Resolution { get; set; }
    public string? Fps { get; set; }
    public string? Vcodec { get; set; }
    public string? Acodec { get; set; }
    public double? Tbr { get; set; }          // total bitrate kbps
    public double? Abr { get; set; }          // audio bitrate kbps
    public long? FileSize { get; set; }
    public long? FileSizeApprox { get; set; }
    public string Protocol { get; set; } = "https";
    public string? Note { get; set; }
    /// <summary>Set when the format needs yt-dlp itself (merge/m3u8 fragments) rather than a plain engine download.</summary>
    public bool DirectDownloadable { get; set; }

    public long EffectiveSize => FileSize ?? FileSizeApprox ?? 0;
}

/// <summary>Result of `yt-dlp -J` analysis.</summary>
public sealed class YtDlpMediaInfo
{
    public string Extractor { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Uploader { get; set; }
    public double DurationSeconds { get; set; }
    public string? Thumbnail { get; set; }
    public bool IsLive { get; set; }
    public string? WebpageUrl { get; set; }
    public List<YtDlpFormat> Formats { get; set; } = new();
}

/// <summary>Structured, categorized failure (UI-safe message).</summary>
public sealed class YtDlpException : Exception
{
    public string Code { get; }
    public ErrorCategory Category { get; }
    public YtDlpException(string code, ErrorCategory category, string message) : base(message)
    {
        Code = code;
        Category = category;
    }
}

/// <summary>Small JSON helper for MediaSelection payloads.</summary>
public static class Json
{
    public static string Selection(object o) => System.Text.Json.JsonSerializer.Serialize(o);
}

/// <summary>
/// v1.5.0 (G28): optional yt-dlp bridge. Nova never bundles or downloads yt-dlp — the user
/// points the engine at a binary (Settings → yt-dlp). Then any URL yt-dlp understands can
/// be analyzed (format list) and downloaded: single-file formats resolve to a direct URL
/// and go through the NORMAL engine pipeline (multi-segment, resume, mirrors); HLS/DASH
/// URLs route to the engine media pipeline. No DRM circumvention: DRM-tagged and live
/// streams are refused (invariant #3).
/// </summary>
public sealed class YtDlpBridge
{
    private readonly string? _explicitPath;
    private readonly Func<string, string?>? _settings;
    private string? _cachedPath;
    private string? _cachedVersion;
    private long _checkedAtMs = long.MinValue;

    public YtDlpBridge(Func<string, string?>? settings = null, string? explicitPath = null)
    {
        _settings = settings;
        _explicitPath = explicitPath;
    }

    /// <summary>Locates the binary: setting &gt; explicit &gt; PATH probe. Null when unavailable.</summary>
    public string? BinaryPath()
    {
        var now = Environment.TickCount64;
        if (_cachedPath is not null && now - Interlocked.Read(ref _checkedAtMs) < 30_000)
            return _cachedPath;
        _cachedPath = Discover();
        Interlocked.Exchange(ref _checkedAtMs, now);
        return _cachedPath;
    }

    private string? Discover()
    {
        var candidates = new List<string>();
        var fromSetting = _settings?.Invoke("ytdlp.path");
        if (!string.IsNullOrWhiteSpace(fromSetting)) candidates.Add(fromSetting.Trim());
        if (!string.IsNullOrWhiteSpace(_explicitPath)) candidates.Add(_explicitPath.Trim());
        candidates.AddRange(new[] { "yt-dlp", "yt-dlp_linux", "youtube-dl", "yt-dlp.exe" });

        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c))
                    return Path.GetFullPath(c);
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = c,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (p is null) continue;
                var version = p.StandardOutput.ReadToEnd().Trim();
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } continue; }
                if (p.ExitCode == 0 && version.Length > 0)
                    return c;
            }
            catch { /* try the next candidate */ }
        }
        return null;
    }

    /// <summary>Cached version string, or null when unavailable.</summary>
    public string? Version()
    {
        if (BinaryPath() is not { } bin) return null;
        if (_cachedVersion is not null) return _cachedVersion;
        try
        {
            var (stdout, _, code) = Run(bin, "--version", timeoutMs: 8000);
            if (code == 0)
            {
                _cachedVersion = stdout.Trim();
                return _cachedVersion;
            }
        }
        catch { /* leave null */ }
        return null;
    }

    /// <summary>Analyzes a page URL into a format list. Refuses DRM and live streams.</summary>
    public async Task<YtDlpMediaInfo> AnalyzeAsync(string url, CancellationToken ct)
    {
        var bin = BinaryPath() ?? throw new YtDlpException("ytdlp_missing", ErrorCategory.Media,
            "yt-dlp was not found. Install it and set its path in Settings → yt-dlp.");

        var (stdout, stderr, code) = await RunAsync(bin,
            $"-J --no-playlist --no-warnings {Quote(url)}", timeoutMs: 90_000, ct);

        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new YtDlpException("ytdlp_analyze_failed", ErrorCategory.Media,
                MapStderrForUi(stderr, "The page could not be analyzed."));

        YtDlpMediaInfo info;
        try { info = ParseInfo(stdout); }
        catch (JsonException) { throw new YtDlpException("ytdlp_bad_json", ErrorCategory.Media,
            "yt-dlp returned data Nova could not parse (version mismatch?)."); }

        if (info.IsLive)
            throw new YtDlpException("live_stream", ErrorCategory.Media,
                "This is a live stream — Nova does not download live media.");

        var drm = info.Formats.FirstOrDefault(f => f.Protocol.Contains("drm", StringComparison.OrdinalIgnoreCase));
        if (drm is not null)
            throw new YtDlpException("drm_protected", ErrorCategory.Media,
                "This media is DRM-protected and cannot be downloaded.");

        return info;
    }

    /// <summary>Resolves a format to the direct URL(s) via `yt-dlp -g`.</summary>
    public async Task<IReadOnlyList<string>> ResolveAsync(string url, string formatId, CancellationToken ct)
    {
        var bin = BinaryPath() ?? throw new YtDlpException("ytdlp_missing", ErrorCategory.Media,
            "yt-dlp was not found. Install it and set its path in Settings → yt-dlp.");

        var (stdout, stderr, code) = await RunAsync(bin,
            $"-f {Quote(formatId)} -g --no-playlist --no-warnings {Quote(url)}", timeoutMs: 90_000, ct);

        if (code != 0)
            throw new YtDlpException("ytdlp_resolve_failed", ErrorCategory.Media,
                MapStderrForUi(stderr, "The selected format could not be resolved to a direct URL."));

        var urls = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (urls.Count == 0)
            throw new YtDlpException("ytdlp_resolve_failed", ErrorCategory.Media,
                "yt-dlp did not return a direct URL for this format.");
        return urls;
    }

    // ---- parsing ---------------------------------------------------------------

    public static YtDlpMediaInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var info = new YtDlpMediaInfo
        {
            Extractor = GetString(root, "extractor_key") ?? GetString(root, "extractor") ?? "",
            Id = GetString(root, "id") ?? "",
            Title = GetString(root, "title") ?? "",
            Uploader = GetString(root, "uploader") ?? GetString(root, "channel"),
            DurationSeconds = GetDouble(root, "duration") ?? 0,
            Thumbnail = GetString(root, "thumbnail"),
            IsLive = GetBool(root, "is_live") == true,
            WebpageUrl = GetString(root, "webpage_url") ?? GetString(root, "original_url"),
        };

        if (root.TryGetProperty("formats", out var formatsEl) && formatsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formatsEl.EnumerateArray())
            {
                var format = new YtDlpFormat
                {
                    FormatId = GetString(f, "format_id") ?? "",
                    Ext = GetString(f, "ext") ?? "",
                    Resolution = GetString(f, "resolution"),
                    Fps = GetString(f, "fps"),
                    Vcodec = GetString(f, "vcodec"),
                    Acodec = GetString(f, "acodec"),
                    Tbr = GetDouble(f, "tbr"),
                    Abr = GetDouble(f, "abr"),
                    FileSize = GetLong(f, "filesize"),
                    FileSizeApprox = GetLong(f, "filesize_approx"),
                    Protocol = GetString(f, "protocol") ?? "https",
                    Note = GetString(f, "format_note"),
                };
                if (format.FormatId.Length == 0) continue;
                if (format.Vcodec?.StartsWith("drm", StringComparison.OrdinalIgnoreCase) == true ||
                    format.Acodec?.StartsWith("drm", StringComparison.OrdinalIgnoreCase) == true ||
                    format.Protocol.Contains("drm", StringComparison.OrdinalIgnoreCase))
                    continue; // never surface DRM formats (invariant #3)

                var hasVideo = format.Vcodec is not null && format.Vcodec != "none";
                var hasAudio = format.Acodec is not null && format.Acodec != "none";
                var progressiveHttp = hasVideo && hasAudio &&
                                      format.Protocol is "https" or "http" or "mms" or "rtmps";
                var completeViaMediaPipeline = hasVideo && hasAudio &&
                                                format.Protocol is "m3u8_native" or "m3u8" or "dash";
                var audioOnlyHttp = !hasVideo && hasAudio && format.Protocol is "https" or "http";
                format.DirectDownloadable = progressiveHttp || completeViaMediaPipeline || audioOnlyHttp;
                var mediaStream = format.Protocol is "m3u8_native" or "m3u8" or "dash" or "https" or "http";
                if (!mediaStream && !format.DirectDownloadable) continue;
                info.Formats.Add(format);
            }
        }

        // storyboards (jpg sheets) are noise for a downloader UI
        info.Formats.RemoveAll(f => f.Ext == "mhtml" || f.Vcodec == "none" && f.Acodec == "none" && f.Protocol == "mhtml");
        return info;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetDouble() : null;

    private static long? GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? (long)v.GetDouble() : null;

    private static bool? GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    // ---- process plumbing --------------------------------------------------------

    /// <summary>Quotes an argument for the raw Arguments string. T-026: on Windows
    /// we ALWAYS quote — cmd.exe (and any .cmd/.bat wrapper around yt-dlp) splits
    /// unquoted arguments on '=', ';' and ',' too, which mangled URLs like
    /// "...watch?v=abc123" into two argv entries. CRT-parsed binaries (yt-dlp.exe)
    /// accept quoted args identically, so this is safe everywhere on Windows.</summary>
    private static string Quote(string s) =>
        OperatingSystem.IsWindows() || s.Contains(' ') || s.Contains('\"')
            ? "\"" + s.Replace("\"", "\\\"") + "\""
            : s;

    private static (string Stdout, string Stderr, int ExitCode) Run(string bin, string args, int timeoutMs)
        => RunAsync(bin, args, timeoutMs, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(
        string bin, string args, int timeoutMs, CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = bin,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new YtDlpException("ytdlp_start_failed", ErrorCategory.Media, "yt-dlp could not be started.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return (await stdoutTask, await stderrTask, p.ExitCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            throw new YtDlpException("ytdlp_timeout", ErrorCategory.Media, "yt-dlp took too long and was stopped.");
        }
    }

    /// <summary>Maps yt-dlp stderr to a short UI-safe reason (no raw dumps).</summary>
    public static string MapStderrForUi(string stderr, string fallback)
    {
        var firstReal = stderr.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase));
        if (firstReal is null) return fallback;
        if (firstReal.Contains("Private video", StringComparison.OrdinalIgnoreCase))
            return "This video is private.";
        if (firstReal.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
            firstReal.Contains("cookies", StringComparison.OrdinalIgnoreCase))
            return "This site requires login — add cookies to the download's headers.";
        if (firstReal.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase))
            return "yt-dlp does not recognize this URL.";
        return fallback;
    }
}
