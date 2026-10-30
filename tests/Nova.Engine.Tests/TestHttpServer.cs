using System.Net;
using System.Text;

namespace Nova.Engine.Tests;

/// <summary>
/// Programmable local HTTP server (HttpListener) covering the scenario matrix of §18.1:
/// range support toggle, failures (403/404/408/429/5xx), redirects, throttled bodies.
/// </summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public Uri BaseUri { get; }
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    // scenario config — tests mutate before issuing requests
    public bool SupportsRanges { get; set; } = true;
    /// <summary>Next N requests fail with this status code (0 = none). Honored as the real code.</summary>
    public int FailNextWith { get; set; }
    public int FailCount { get; private set; }
    public string? RedirectNextTo { get; set; }
    public string EtagValue { get; set; } = "\"test-etag-1\"";
    /// <summary>When > 0, bodies stream at ~this many bytes/sec (lets pause/cancel race the transfer).</summary>
    public int ThrottleBytesPerSec { get; set; }
    public HashSet<string> UnknownLengthFor { get; } = new();
    public Dictionary<string, byte[]> Files { get; } = new();
    public List<string> RequestsSeen { get; } = new();
    /// <summary>v1.3.0 test hook: observed (path, Cookie, X-Nova-Test) request headers.</summary>
    public List<(string Path, string? Cookie, string? Custom)> HeaderObservations { get; } = new();
    /// <summary>v1.8.0 test hook: when set, requests without this Basic auth pair get 401.</summary>
    public (string user, string pass)? RequireBasicAuth { get; set; }
    /// <summary>v1.8.0 test hook: Authorization headers observed (for auth-retry assertions).</summary>
    public List<string?> AuthorizationSeen { get; } = new();

    public TestHttpServer()
    {
        var port = Random.Shared.Next(20000, 40000);
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _loop = Task.Run(ServeLoopAsync);
    }

    public string UrlFor(string name) => new Uri(BaseUri, name).ToString();

    public void AddFile(string name, byte[] content) => Files[name.TrimStart('/')] = content;
    public void AddText(string name, string content) => AddFile(name, Encoding.UTF8.GetBytes(content));

    private async Task ServeLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }
        catch (Exception) { /* listener closed */ }
    }

    private void HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            RequestsSeen.Add($"{req.HttpMethod} {req.Url!.AbsolutePath} {req.Headers["Range"]}");
            HeaderObservations.Add((req.Url.AbsolutePath, req.Headers["Cookie"], req.Headers["X-Nova-Test"]));

            if (RedirectNextTo is { } target && req.Url.AbsolutePath.EndsWith("/redirect-me"))
            {
                res.StatusCode = 302;
                res.RedirectLocation = target;
                res.OutputStream.Close();
                return;
            }

            // v1.8.0 auth hook: 401 until a correct Basic Authorization arrives
            AuthorizationSeen.Add(req.Headers["Authorization"]);
            if (RequireBasicAuth is { } auth)
            {
                var expect = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(auth.user + ":" + auth.pass));
                if (!string.Equals(req.Headers["Authorization"], expect, StringComparison.Ordinal))
                {
                    res.StatusCode = 401;
                    res.Headers["WWW-Authenticate"] = "Basic realm=\"nova-test\"";
                    res.Close();
                    return;
                }
            }

            if (FailNextWith > 0)
            {
                var code = FailNextWith; // intended status code
                FailNextWith = 0;       // one-shot: only the NEXT request fails
                FailCount++;
                res.StatusCode = code is >= 400 and < 600 ? code : 500;
                res.Close();
                return;
            }

            var name = req.Url.AbsolutePath.TrimStart('/');
            if (!Files.TryGetValue(Uri.UnescapeDataString(name), out var data) &&
                !Files.TryGetValue(name, out data))
            {
                res.StatusCode = 404;
                var nf = Encoding.UTF8.GetBytes("not found");
                res.ContentLength64 = nf.Length;
                res.OutputStream.Write(nf);
                res.OutputStream.Close();
                return;
            }

            var rangeHeader = req.Headers["Range"];
            long from = 0, to = data.Length - 1;
            var isRange = SupportsRanges && rangeHeader is not null && rangeHeader.StartsWith("bytes=");

            res.Headers["ETag"] = EtagValue;
            res.Headers["Last-Modified"] = "Wed, 21 Oct 2026 07:28:00 GMT";

            if (isRange)
            {
                var spec = rangeHeader["bytes=".Length..];
                var dash = spec.IndexOf('-');
                long.TryParse(spec[..dash], out from);
                if (dash < spec.Length - 1) long.TryParse(spec[(dash + 1)..], out to);
                to = Math.Min(to, data.Length - 1);
                if (from > to || from >= data.Length)
                {
                    res.StatusCode = 416;
                    res.Close();
                    return;
                }
                res.StatusCode = 206;
                res.Headers["Content-Range"] = $"bytes {from}-{to}/{data.Length}";
                res.Headers["Accept-Ranges"] = "bytes";
                WriteThrottled(res, data, (int)from, (int)(to - from + 1));
            }
            else
            {
                res.StatusCode = 200;
                res.Headers["Accept-Ranges"] = SupportsRanges ? "bytes" : "none";
                if (!UnknownLengthFor.Contains(name))
                    res.ContentLength64 = data.Length;
                WriteThrottled(res, data, 0, data.Length);
            }
        }
        catch (Exception) { /* client dropped mid-body (cancel tests) */ }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    private void WriteThrottled(HttpListenerResponse res, byte[] data, int offset, int count)
    {
        var chunk = 32 * 1024;
        var delayPerChunk = ThrottleBytesPerSec > 0 ? chunk * 1000 / ThrottleBytesPerSec : 0;
        var i = offset;
        var end = offset + count;
        while (i < end)
        {
            var n = Math.Min(chunk, end - i);
            res.OutputStream.Write(data, i, n);
            if (delayPerChunk > 0) Thread.Sleep(delayPerChunk);
            i += n;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        try { _loop?.Wait(1000); } catch { }
    }
}
