using Nova.Domain;
using Nova.Engine;
using Nova.Persistence;

namespace Nova.Scheduler;

/// <summary>
/// IDM parity — periodic synchronization queues: re-downloads URLs whose content
/// changed since the last run (ETag / Last-Modified comparison). A timer runs the
/// due rules automatically; "run now" forces a run regardless of interval.
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly DownloadEngine _engine;
    private readonly ISyncRuleRepository _repo;
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly System.Timers.Timer _timer;
    private int _busy;

    public SyncService(DownloadEngine engine, ISyncRuleRepository repo, HttpClient http, IClock? clock = null)
    {
        _engine = engine;
        _repo = repo;
        _http = http;
        _clock = clock ?? SystemClock.Instance;
        _timer = new System.Timers.Timer(30_000) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = CheckDueAsync();
        _timer.Start();
    }

    /// <summary>Runs every rule whose interval has elapsed. Safe to call concurrently (no-ops).</summary>
    public async Task CheckDueAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            foreach (var rule in _repo.GetAll())
            {
                if (!rule.Enabled) continue;
                if (rule.IntervalMinutes <= 0) continue;
                var dueAt = rule.LastRunMs + (long)rule.IntervalMinutes * 60_000;
                if (_clock.UnixMs < dueAt) continue;
                try { await RunRuleAsync(rule.Id, force: false); }
                catch (Exception ex) { EngineLog.Warn($"sync rule {rule.Id} failed: {ex.Message}"); }
            }
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Runs one rule: probes validators, adds tasks for new/changed URLs, persists state.</summary>
    public async Task<SyncRunResult> RunRuleAsync(long ruleId, bool force)
    {
        var rule = _repo.Get(ruleId)
            ?? throw new InvalidOperationException($"sync rule {ruleId} not found");

        var urls = rule.Urls.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var known = _repo.GetValidators(ruleId);
        int added = 0, unchanged = 0, failed = 0;

        foreach (var url in urls)
        {
            (string? etag, string? lastModified) probe;
            try { probe = await ProbeAsync(url); }
            catch { probe = (null, null); }

            known.TryGetValue(url, out var prev);
            var isChanged = probe.etag is not null || probe.lastModified is not null;
            if (isChanged && prev != (probe.etag, probe.lastModified))
            {
                var result = _engine.Add(new NewDownload
                {
                    Url = url,
                    DestinationDir = rule.DestinationDir.Length > 0 ? rule.DestinationDir : null,
                    Category = rule.Category,
                    AutoStart = true,
                });
                if (result.Ok) added++;
                else failed++;
            }
            else if (isChanged)
            {
                unchanged++;
            }
            else
            {
                // no validators available: treat as unchanged unless never seen
                if (prev == (null, null) && !known.ContainsKey(url)) { /* first sighting without validators: skip re-download */ }
                unchanged++;
            }

            if (probe.etag is not null || probe.lastModified is not null)
                _repo.SaveValidator(ruleId, url, probe.etag, probe.lastModified);
        }

        _repo.SetLastRun(ruleId, _clock.UnixMs);
        EngineLog.Info($"sync '{rule.Name}': {added} added, {unchanged} unchanged, {failed} failed");
        return new SyncRunResult(ruleId, urls.Count, added, unchanged, failed);
    }

    /// <summary>HEAD probe (ETag / Last-Modified); falls back to a ranged GET on 405/501.</summary>
    private async Task<(string? ETag, string? LastModified)> ProbeAsync(string url)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        head.Headers.ConnectionClose = false;
        using var resp = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead);
        if (resp.StatusCode is System.Net.HttpStatusCode.MethodNotAllowed
            or System.Net.HttpStatusCode.NotImplemented)
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var r2 = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
            return (Trim(r2.Headers.ETag?.Tag), r2.Content.Headers.LastModified?.ToString("R"));
        }
        return (Trim(resp.Headers.ETag?.Tag), resp.Content.Headers.LastModified?.ToString("R"));
    }

    private static string? Trim(string? etag) =>
        string.IsNullOrEmpty(etag) ? etag : etag.Trim('"');

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
