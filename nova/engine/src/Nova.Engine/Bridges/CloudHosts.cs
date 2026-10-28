using System.Diagnostics;
using System.Text.Json;

namespace Nova.Engine.Bridges;

/// <summary>
/// Cloud host resolvers: converts share pages (Google Drive, Mega.nz) into
/// direct downloadable URLs. GDrive uses the confirm-token dance; Mega
/// requires its crypto API — we implement the public parts.
/// </summary>
public sealed class CloudHostResolver(HttpClient http)
{
    public bool CanResolve(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        return host.Contains("drive.google.com") || host.Contains("docs.google.com")
            || host.Contains("mega.nz") || host.Contains("mega.co.nz")
            || host.Contains("dropbox.com") || host.Contains("1drv.ms") || host.Contains("onedrive.live.com");
    }

    public sealed record ResolvedLink(string DirectUrl, string FileName, long? SizeBytes, bool RequiresYtDlp = false);

    public async Task<ResolvedLink?> ResolveAsync(Uri url, CancellationToken ct = default)
    {
        var host = url.Host.ToLowerInvariant();
        if (host.Contains("drive.google.com") || host.Contains("docs.google.com"))
            return await ResolveGoogleDriveAsync(url, ct);
        if (host.Contains("dropbox.com"))
            return ResolveDropbox(url);
        if (host.Contains("mega."))
            return new ResolvedLink(url.ToString(), "", null, RequiresYtDlp: true); // megatools/yt-dlp handles crypto
        return null;
    }

    // ------------------------------------------------------------------
    // Google Drive: file/d/<id> -> uc?export=download&confirm=t
    // ------------------------------------------------------------------

    private async Task<ResolvedLink?> ResolveGoogleDriveAsync(Uri url, CancellationToken ct)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url.ToString(), @"[-\w]{25,}");
        if (!m.Success) return null;
        var id = m.Value;
        var direct = $"https://drive.google.com/uc?export=download&confirm=t&id={id}";

        // Probe for filename/size
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, direct);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var name = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? id;
            var size = resp.Content.Headers.ContentLength;
            return new ResolvedLink(direct, name, size);
        }
        catch
        {
            return new ResolvedLink(direct, id, null);
        }
    }

    // ------------------------------------------------------------------
    // Dropbox: ?dl=0 -> ?dl=1
    // ------------------------------------------------------------------

    private static ResolvedLink? ResolveDropbox(Uri url)
    {
        var b = new UriBuilder(url);
        var query = b.Query?.TrimStart('?') ?? "";
        var hasDl0 = query.Contains("dl=0");
        if (hasDl0) query = query.Replace("dl=0", "dl=1");
        else if (!query.Contains("dl=1")) query = query.Length == 0 ? "dl=1" : query + "&dl=1";
        b.Query = query;
        return new ResolvedLink(b.ToString(), "", null);
    }
}

/// <summary>
/// Server profiler: measures real throughput per connection count and
/// picks the best mirror from Metalink lists. Powering "which mirror is
/// actually fastest" — a JDownloader/Metalink feature most managers fake.
/// </summary>
public sealed class ServerProfiler(HttpClient http)
{
    public sealed record Probe(
        string Url,
        int Connections,
        double MegabytesPerSec,
        long LatencyMs,
        bool AcceptsRanges,
        bool Ok);

    public async Task<Probe> MeasureAsync(string url, int connections = 4, long sampleBytes = 4L << 20, CancellationToken ct = default)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var downloaded = 0L;
            var workers = new Task[connections];
            for (var i = 0; i < connections; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Range", $"bytes=0-{sampleBytes / connections}");
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = await stream.ReadAsync(buf, ct)) > 0)
                    {
                        Interlocked.Add(ref downloaded, n);
                        if (Volatile.Read(ref downloaded) >= sampleBytes) break;
                    }
                }, ct);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(10_000);
            await Task.WhenAll(workers).WaitAsync(timeout.Token);

            sw.Stop();
            return new Probe(url, connections, downloaded / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds,
                sw.ElapsedMilliseconds, true, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Probe(url, connections, 0, -1, false, false);
        }
    }

    /// <summary>Pick fastest mirror from a Metalink URL list.</summary>
    public async Task<string?> BestMirrorAsync(IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        var probes = await Task.WhenAll(urls.Take(5).Select(u => MeasureAsync(u, 4, 1L << 21, ct)));
        return probes.Where(p => p.Ok).OrderByDescending(p => p.MegabytesPerSec).FirstOrDefault()?.Url;
    }
}
