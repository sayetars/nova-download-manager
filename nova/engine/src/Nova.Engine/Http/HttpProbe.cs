using System.Net;
using System.Net.Sockets;
using Nova.Core.Models;

namespace Nova.Engine.Http;

/// <summary>
/// URL analyzer: HEAD with Range probe, filename extraction, auth challenge
/// detection, protocol capability discovery (accept-ranges / hls / dash),
/// and ETag capture for safe resume. This is the "Analyzing" phase of every
/// task — IDM's "file information" dialog equivalent.
/// </summary>
public sealed class HttpProbe
{
    private readonly HttpClient _http;
    private readonly SettingsBridge _settings;

    public HttpProbe(HttpClient http, SettingsBridge settings)
    {
        _http = http;
        _settings = settings;
    }

    public sealed record ProbeResult
    {
        public bool Ok { get; init; }
        public string? FinalUrl { get; init; }
        public string FileName { get; init; } = "";
        public long? TotalBytes { get; init; }
        public bool AcceptRanges { get; init; }
        public string? ETag { get; init; }
        public string? LastModified { get; init; }
        public string ContentType { get; init; } = "";
        public bool IsHls { get; init; }
        public bool IsDash { get; init; }
        public FailureReason Failure { get; init; }
        public string? Error { get; init; }
        public Dictionary<string, string> Headers { get; init; } = new();
        public HttpStatusCode? StatusCode { get; init; }
    }

    public async Task<ProbeResult> ProbeAsync(DownloadTask task, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, task.Url);
            // Some servers reject HEAD — probe with a 1-byte GET Range instead
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
            ApplyHeaders(req, task);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (resp.StatusCode == HttpStatusCode.MethodNotAllowed)
                return await GetProbeAsync(task, ct);

            var (total, ranges) = ReadContentLength(resp);
            return new ProbeResult
            {
                Ok = resp.IsSuccessStatusCode,
                FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? task.Url,
                FileName = GuessFileName(resp, task),
                TotalBytes = total,
                AcceptRanges = ranges,
                ETag = resp.Headers.ETag?.Tag,
                LastModified = resp.Content.Headers.LastModified?.ToString(),
                ContentType = resp.Content.Headers.ContentType?.MediaType ?? "",
                IsHls = IsPlaylist(resp, ".m3u8"),
                IsDash = IsPlaylist(resp, ".mpd"),
                Failure = MapFailure(resp.StatusCode),
                StatusCode = resp.StatusCode,
                Error = resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}",
                Headers = CollectHeaders(resp)
            };
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult { Ok = false, Failure = Classify(ex), Error = ex.Message };
        }
        catch (TaskCanceledException)
        {
            return new ProbeResult { Ok = false, Failure = FailureReason.Timeout, Error = "probe timed out" };
        }
    }

    private async Task<ProbeResult> GetProbeAsync(DownloadTask task, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, task.Url);
        req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
        ApplyHeaders(req, task);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var (total, ranges) = ReadContentLength(resp);
        return new ProbeResult
        {
            Ok = resp.IsSuccessStatusCode,
            FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? task.Url,
            FileName = GuessFileName(resp, task),
            TotalBytes = total,
            AcceptRanges = ranges,
            ETag = resp.Headers.ETag?.Tag,
            LastModified = resp.Content.Headers.LastModified?.ToString(),
            ContentType = resp.Content.Headers.ContentType?.MediaType ?? "",
            IsHls = IsPlaylist(resp, ".m3u8"),
            IsDash = IsPlaylist(resp, ".mpd"),
            Failure = MapFailure(resp.StatusCode),
            Error = resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}",
            Headers = CollectHeaders(resp)
        };
    }

    // ------------------------------------------------------------------

    private static (long?, bool) ReadContentLength(HttpResponseMessage resp)
    {
        var rangeHeader = resp.Content.Headers.ContentRange?.ToString();
        if (!string.IsNullOrEmpty(rangeHeader))
        {
            // bytes 0-1/12345678
            var slash = rangeHeader.LastIndexOf('/');
            if (slash >= 0 && long.TryParse(rangeHeader[(slash + 1)..], out var total) && total > 0)
                return (total, true);
        }
        var len = resp.Content.Headers.ContentLength;
        return (len is > 0 ? len : null, false);
    }

    private static bool IsPlaylist(HttpResponseMessage resp, string ext)
    {
        var url = resp.RequestMessage?.RequestUri?.ToString() ?? "";
        var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
        return url.Contains(ext, StringComparison.OrdinalIgnoreCase)
            || ct.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) && ext == ".m3u8"
            || ct.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) && ext == ".mpd";
    }

    internal static string GuessFileName(HttpResponseMessage resp, DownloadTask task)
    {
        // 1. Content-Disposition
        var cd = resp.Content.Headers.ContentDisposition;
        var fromCd = cd?.FileNameStar ?? cd?.FileName;
        if (!string.IsNullOrWhiteSpace(fromCd))
            return Sanitize(fromCd.Trim('"', '\''));

        // 2. URL path
        var url = resp.RequestMessage?.RequestUri ?? new Uri(task.Url);
        var last = Uri.UnescapeDataString(url.Segments.LastOrDefault() ?? "");
        if (!string.IsNullOrWhiteSpace(last) && last.Contains('.'))
            return Sanitize(last);

        // 3. From task hint
        if (!string.IsNullOrWhiteSpace(task.FileName)) return task.FileName;

        // 4. Content type fallback
        var mime = resp.Content.Headers.ContentType?.MediaType ?? "";
        var ext = mime switch
        {
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            "video/mp4" => ".mp4",
            "audio/mpeg" => ".mp3",
            "text/html" => ".html",
            _ => ".bin"
        };
        return $"nova-{DateTime.UtcNow:yyyyMMdd-HHmmss}{ext}";
    }

    internal static string Sanitize(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static void ApplyHeaders(HttpRequestMessage req, DownloadTask task)
    {
        if (!string.IsNullOrEmpty(task.ReferringUrl))
            req.Headers.TryAddWithoutValidation("Referer", task.ReferringUrl);
        if (task.Headers.TryGetValue("User-Agent", out var ua) && !string.IsNullOrEmpty(ua))
            req.Headers.TryAddWithoutValidation("User-Agent", ua);
        foreach (var (k, v) in task.Headers)
        {
            if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) || k.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                continue;
            req.Headers.TryAddWithoutValidation(k, v);
        }
        if (task.Username is not null)
        {
            switch (task.AuthType)
            {
                case "basic":
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{task.Username}:{task.Secret}")));
                    break;
                case "bearer":
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer", task.Secret);
                    break;
            }
        }
    }

    private static Dictionary<string, string> CollectHeaders(HttpResponseMessage resp)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in resp.Headers) dict[k] = string.Join(",", v);
        foreach (var (k, v) in resp.Content.Headers) dict[k] = string.Join(",", v);
        return dict;
    }

    internal static FailureReason MapFailure(HttpStatusCode sc) => sc switch
    {
        HttpStatusCode.Forbidden => FailureReason.Forbidden,
        HttpStatusCode.Unauthorized => FailureReason.Forbidden,
        HttpStatusCode.NotFound => FailureReason.NotFound,
        HttpStatusCode.RequestedRangeNotSatisfiable => FailureReason.RangeNotSupported,
        >= HttpStatusCode.InternalServerError => FailureReason.ServerError,
        _ => FailureReason.Unknown
    };

    internal static FailureReason Classify(HttpRequestException ex)
    {
        if (ex.InnerException is SocketException se)
        {
            return se.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => FailureReason.ConnectionRefused,
                SocketError.TimedOut => FailureReason.Timeout,
                SocketError.HostNotFound => FailureReason.DnsFailure,
                _ => FailureReason.NetworkUnavailable
            };
        }
        return ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase)
            ? FailureReason.TlsError
            : FailureReason.NetworkUnavailable;
    }
}
