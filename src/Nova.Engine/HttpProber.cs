using System.Net;
using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// HTTP probe: discovers size / range support / validators / filename without downloading the body.
/// Uses a ranged GET (Range: bytes=0-0) because many servers answer HEAD poorly — 206 also proves ranges.
/// </summary>
public sealed class HttpProber
{
    private readonly HttpClient _http;

    public HttpProber(HttpClient http)
    {
        _http = http;
    }

    /// <exception cref="NovaHttpException">structured, categorized failure.</exception>
    public async Task<HttpProbeResult> ProbeAsync(string url, string? referer, CancellationToken ct, DownloadTask? task = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        AddDefaultHeaders(req, referer);
        if (task is not null) TaskRunner.ApplyTaskHeaders(req, task);

        using var resp = await SendGuardedAsync(req, ct);

        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
            throw NovaHttpException.FromStatus(resp.StatusCode, url);

        var effective = resp.RequestMessage?.RequestUri?.ToString() ?? url;

        // Validator compatibility: ETag wins; strong preferred. Strip weak prefix for stability.
        var etag = resp.Headers.ETag?.Tag;
        if (etag != null && etag.StartsWith("W/")) etag = etag[2..];

        long length = -1;
        if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            // Content-Range: bytes 0-0/12345
            var cr = resp.Content.Headers.ContentRange;
            if (cr?.HasLength == true) length = cr.Length.Value;
        }
        else if (resp.Content.Headers.ContentLength is > 0)
        {
            length = resp.Content.Headers.ContentLength.Value;
        }

        var fileName =
            FilenamePolicy.FromContentDisposition(resp.Content.Headers.ContentDisposition?.ToString())
            ?? FilenamePolicy.FromUrl(effective);

        return new HttpProbeResult
        {
            FinalUrl = effective,
            ContentLength = length,
            AcceptRanges = string.Equals(resp.Headers.AcceptRanges?.ToString(), "bytes", StringComparison.OrdinalIgnoreCase)
                           || resp.StatusCode == HttpStatusCode.PartialContent,
            Etag = etag,
            LastModified = resp.Content.Headers.LastModified?.ToString("R"),
            ContentType = resp.Content.Headers.ContentType?.MediaType,
            SuggestedFileName = fileName,
            StatusCode = (int)resp.StatusCode,
            LinkHeaders = resp.Headers.TryGetValues("Link", out var links) ? links.ToList() : new List<string>(),
            DigestHeader = resp.Headers.TryGetValues("Digest", out var digests) ? string.Join(", ", digests) : null,
        };
    }

    internal static void AddDefaultHeaders(HttpRequestMessage req, string? referer)
    {
        HttpProtocol.Apply(req); // v1.8.0: offer HTTP/2 on https (covers prober + grabber requests)
        req.Headers.UserAgent.ParseAdd(Nova.Domain.VersionInfo.UserAgent);
        req.Headers.Accept.ParseAdd("*/*");
        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
            req.Headers.Referrer = refUri;
    }

    /// <summary>Sends with timeouts and maps low-level failures to structured Nova errors.</summary>
    internal async Task<HttpResponseMessage> SendGuardedAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException se)
        {
            throw se.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.HostUnreachable
                    => new NovaHttpException("dns_failure", ErrorCategory.Dns, "Could not resolve the server address.", url: req.RequestUri?.ToString()),
                _ => new NovaHttpException("network_unreachable", ErrorCategory.Network, "The server could not be reached.", details: se.SocketErrorCode.ToString(), url: req.RequestUri?.ToString()),
            };
        }
        catch (HttpRequestException ex)
        {
            throw ex.InnerException?.GetType().Name switch
            {
                nameof(System.Security.Authentication.AuthenticationException)
                    => new NovaHttpException("tls_error", ErrorCategory.Tls, "Secure connection could not be established.", url: req.RequestUri?.ToString()),
                _ => new NovaHttpException("network_error", ErrorCategory.Network, ex.Message, url: req.RequestUri?.ToString()),
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NovaHttpException("timeout", ErrorCategory.Network, "The server took too long to respond.", url: req.RequestUri?.ToString());
        }
    }
}

/// <summary>Structured HTTP failure surfaced by the engine (never raw exceptions to UI).</summary>
public sealed class NovaHttpException : Exception
{
    public string Code { get; }
    public ErrorCategory Category { get; }
    public bool Retryable { get; }
    public Dictionary<string, string> Details { get; }

    public NovaHttpException(string code, ErrorCategory category, string message,
        string? url = null, string? details = null, bool retryable = true) : base(message)
    {
        Code = code;
        Category = category;
        Retryable = retryable;
        Details = new Dictionary<string, string>();
        if (url is not null) Details["url"] = url;
        if (details is not null) Details["detail"] = details;
    }

    public static NovaHttpException FromStatus(HttpStatusCode status, string url)
    {
        var (code, cat, retry) = status switch
        {
            HttpStatusCode.Unauthorized => ("http_401", ErrorCategory.Auth, false),
            HttpStatusCode.Forbidden => ("http_403", ErrorCategory.Forbidden, false),
            HttpStatusCode.NotFound => ("http_404", ErrorCategory.NotFound, false),
            HttpStatusCode.RequestTimeout => ("http_408", ErrorCategory.Network, true),
            HttpStatusCode.Gone => ("http_410", ErrorCategory.NotFound, false),
            HttpStatusCode.RequestedRangeNotSatisfiable => ("http_416", ErrorCategory.Http, false),
            HttpStatusCode.TooManyRequests => ("http_429", ErrorCategory.RateLimited, true),
            >= HttpStatusCode.InternalServerError => ("http_5xx", ErrorCategory.Server, true),
            _ => ($"http_{(int)status}", ErrorCategory.Http, true),
        };
        return new NovaHttpException(code, cat, $"Server returned {(int)status} {status}.", url, retry.ToString(), retryable: retry);
    }
}
