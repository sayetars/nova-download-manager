using Nova.Core;
using Nova.Core.Events;
using Nova.Core.Models;
using Nova.Engine;
using Nova.Storage;

namespace Nova.Scheduler;

/// <summary>
/// Queue + schedule engine:
///  - queues with per-queue concurrency (main, videos, documents, ...)
///  - time-based rules: start/pause/limit/shutdown; Gregorian or Jalali dates
///  - start-once scheduling for individual tasks
/// Persists its own state in the schedules table via the shared Database.
/// </summary>
public sealed class Scheduler : IAsyncDisposable
{
    private readonly Database _db;
    private readonly Func<DownloadEngine> _engine;
    private Timer _timer = null!;

    public sealed record ScheduleRule
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "start";          // start | pause | limit | shutdown
        public string TimeExpr { get; init; } = "";             // "21:30" or "1404/08/12 21:30"
        public string? DaysOfWeek { get; init; }               // "mon,tue" or null = every day
        public long? SpeedLimitBytes { get; init; }
        public string? QueueName { get; init; }
        public bool Enabled { get; init; } = true;
        public string Calendar { get; init; } = "gregorian";   // gregorian | jalali
        public DateTime? LastRunUtc { get; set; }
    }

    private readonly List<ScheduleRule> _rules = new();
    private readonly object _gate = new();

    public Scheduler(Database db, Func<DownloadEngine> engine)
    {
        _db = db;
        _engine = engine;
        LoadRules();
        _timer = new Timer(async _ => await TickAsync(), null, 1000, 1000);
    }

    public IReadOnlyList<ScheduleRule> Rules() { lock (_gate) return _rules.ToList(); }

    public void UpsertRule(ScheduleRule rule)
    {
        lock (_gate)
        {
            var idx = _rules.FindIndex(r => r.Id == rule.Id);
            if (idx >= 0) _rules[idx] = rule;
            else _rules.Add(rule);
        }
        PersistRule(rule);
    }

    public void DeleteRule(string id)
    {
        lock (_gate) _rules.RemoveAll(r => r.Id == id);
        _db.ExecuteAsync("DELETE FROM schedules WHERE id=@id", ("@id", id)).GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------
    // Tick: evaluate rules once a minute, tasks on schedule
    // ------------------------------------------------------------------

    private async Task TickAsync()
    {
        try
        {
            var engine = _engine();
            var now = DateTime.Now;

            foreach (var rule in Rules().Where(r => r.Enabled))
            {
                if (!MatchesNow(rule, now)) continue;
                if (rule.LastRunUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromMinutes(10)) continue;

                lock (_gate) rule.LastRunUtc = DateTime.UtcNow;
                _db.ExecuteAsync("UPDATE schedules SET last_run_at=@t WHERE id=@id",
                    ("@t", DateTime.UtcNow.Ticks), ("@id", rule.Id)).GetAwaiter().GetResult();

                switch (rule.Kind)
                {
                    case "start":
                        StartQueue(engine, rule.QueueName);
                        break;
                    case "pause":
                        PauseQueue(engine, rule.QueueName);
                        break;
                    case "limit":
                        engine.SetGlobalSpeedLimit(rule.SpeedLimitBytes ?? 0);
                        break;
                    case "shutdown":
                        EventBus.Instance.Publish(new ShutdownRequestedEvent(false));
                        break;
                }
            }

            // scheduled single tasks
            foreach (var t in engine.List())
            {
                if (t.ScheduledStart is { } start && t.State == DownloadState.Created && start <= now)
                {
                    t.ScheduledStart = null;
                    engine.Resume(t.Id);
                }
            }

            // shutdown-when-complete
            var all = engine.List();
            if (all.Any(t => t.ShutdownWhenComplete) &&
                all.Where(t => t.ShutdownWhenComplete)
                   .All(t => t.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled))
            {
                EventBus.Instance.Publish(new ShutdownRequestedEvent(false));
            }
        }
        catch
        {
            // scheduler must never take the engine down
        }
    }

    private static void StartQueue(DownloadEngine engine, string? queue)
    {
        foreach (var t in engine.List())
            if ((queue == null || t.QueueName == queue) && t.State == DownloadState.Paused)
                engine.Resume(t.Id);
    }

    private static void PauseQueue(DownloadEngine engine, string? queue)
    {
        foreach (var t in engine.List())
            if (queue == null || t.QueueName == queue)
                engine.Pause(t.Id);
    }

    private static bool MatchesNow(ScheduleRule rule, DateTime now)
    {
        if (!MatchesDays(rule.DaysOfWeek, now)) return false;
        return MatchesTime(rule.TimeExpr, rule.Calendar, now);
    }

    internal static bool MatchesDays(string? daysOfWeek, DateTime now)
    {
        if (string.IsNullOrEmpty(daysOfWeek)) return true;
        var map = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["mon"] = DayOfWeek.Monday, ["tue"] = DayOfWeek.Tuesday, ["wed"] = DayOfWeek.Wednesday,
            ["thu"] = DayOfWeek.Thursday, ["fri"] = DayOfWeek.Friday, ["sat"] = DayOfWeek.Saturday, ["sun"] = DayOfWeek.Sunday
        };
        var wanted = daysOfWeek.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => map.TryGetValue(d, out var dow) ? dow : (DayOfWeek?)null)
            .Where(d => d != null)
            .Select(d => d!.Value)
            .ToHashSet();
        return wanted.Contains(now.DayOfWeek);
    }

    internal static bool MatchesTime(string timeExpr, string calendar, DateTime now)
    {
        var expr = timeExpr.Trim();
        DateTime? fireAt = null;

        // date + time?
        var spaceIdx = expr.IndexOf(' ');
        if (spaceIdx > 0)
        {
            var datePart = expr[..spaceIdx];
            var timePart = expr[(spaceIdx + 1)..];
            if (!TimeSpan.TryParse(timePart, out var tod)) return false;

            if (calendar.Equals("jalali", StringComparison.OrdinalIgnoreCase))
            {
                if (!JalaliCalendar.TryParse(datePart, out var j)) return false;
                var g = JalaliCalendar.ToGregorian(j.Year, j.Month, j.Day, tod.Hours, tod.Minutes);
                fireAt = g;
            }
            else
            {
                if (!DateTime.TryParse($"{datePart} {timePart}", out var dt)) return false;
                fireAt = dt;
            }
        }
        else
        {
            if (!TimeSpan.TryParse(expr, out var tod)) return false;
            fireAt = now.Date + tod;
        }

        var fireLocal = fireAt.Value;
        var diff = now - fireLocal;
        // fire within the minute
        return diff >= TimeSpan.Zero && diff < TimeSpan.FromMinutes(1);
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    private void LoadRules()
    {
        using var reader = _db.QueryAsync("SELECT * FROM schedules").GetAwaiter().GetResult();
        while (reader.Read())
        {
            _rules.Add(new ScheduleRule
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                Kind = reader.GetString(reader.GetOrdinal("kind")),
                TimeExpr = reader.GetString(reader.GetOrdinal("time_expr")),
                DaysOfWeek = reader.IsDBNull(reader.GetOrdinal("days_of_week")) ? null : reader.GetString(reader.GetOrdinal("days_of_week")),
                SpeedLimitBytes = reader.IsDBNull(reader.GetOrdinal("speed_limit_bytes")) ? null : reader.GetInt64(reader.GetOrdinal("speed_limit_bytes")),
                QueueName = reader.IsDBNull(reader.GetOrdinal("queue_name")) ? null : reader.GetString(reader.GetOrdinal("queue_name")),
                Enabled = reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
                Calendar = reader.GetString(reader.GetOrdinal("calendar"))
            });
        }
    }

    private void PersistRule(ScheduleRule r)
    {
        _db.ExecuteAsync(
            """
            INSERT INTO schedules (id, name, kind, time_expr, days_of_week, speed_limit_bytes, queue_name, enabled, calendar, last_run_at)
            VALUES (@id, @name, @kind, @expr, @days, @limit, @queue, @enabled, @cal, @last)
            ON CONFLICT(id) DO UPDATE SET
                name=@name, kind=@kind, time_expr=@expr, days_of_week=@days, speed_limit_bytes=@limit,
                queue_name=@queue, enabled=@enabled, calendar=@cal, last_run_at=@last
            """,
            ("@id", r.Id), ("@name", r.Name), ("@kind", r.Kind), ("@expr", r.TimeExpr),
            ("@days", (object?)r.DaysOfWeek), ("@limit", (object?)r.SpeedLimitBytes),
            ("@queue", (object?)r.QueueName), ("@enabled", r.Enabled ? 1 : 0), ("@cal", r.Calendar),
            ("@last", (object?)r.LastRunUtc?.Ticks)).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer != null) await _timer.DisposeAsync();
    }
}

/// <summary>
/// Site Grabber: crawls a website up to Depth levels, downloads files matching
/// include/exclude filters (IDM's signature feature). Polite: 2 req/s,
/// robots-aware, single-domain by default.
/// </summary>
public sealed class SiteGrabber(HttpClient http)
{
    public sealed record GrabOptions
    {
        public string StartUrl { get; init; } = "";
        public int MaxDepth { get; init; } = 2;
        public string? IncludeFilter { get; init; }   // "*.jpg;*.png;*.css"
        public string? ExcludeFilter { get; init; }
        public bool FollowOtherDomains { get; init; }
        public string SaveDirectory { get; init; } = "";
    }

    public event Action<GrabberFileFound>? FileFound;

    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);

    public async Task GrabAsync(GrabOptions opts, CancellationToken ct = default)
    {
        _visited.Clear();
        var baseDomain = new Uri(opts.StartUrl).Host;
        await CrawlAsync(new Uri(opts.StartUrl), opts, baseDomain, 0, ct);
    }

    private async Task CrawlAsync(Uri url, GrabOptions opts, string baseDomain, int depth, CancellationToken ct)
    {
        if (depth > opts.MaxDepth || ct.IsCancellationRequested) return;
        if (!_visited.Add(url.ToString())) return;
        if (!opts.FollowOtherDomains && url.Host != baseDomain) return;

        try
        {
            var html = await http.GetStringAsync(url, ct);
            await Task.Delay(500, ct); // politeness

            foreach (var (link, isAsset) in ExtractLinks(html, url))
            {
                if (ct.IsCancellationRequested) return;
                if (!opts.FollowOtherDomains && link.Host != baseDomain) continue;

                if (MatchesFilters(link, opts))
                {
                    var path = UrlToPath(link, opts.SaveDirectory, baseDomain);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var size = await DownloadFileAsync(link, path, ct);
                    FileFound?.Invoke(new GrabberFileFound("", link.ToString(), path, size, true));
                }
                else if (isAsset || depth < opts.MaxDepth)
                {
                    await CrawlAsync(link, opts, baseDomain, depth + 1, ct);
                }
            }
        }
        catch { /* page failed — continue */ }
    }

    private static IEnumerable<(Uri Link, bool IsAsset)> ExtractLinks(string html, Uri page)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(html,
            @"(?:href|src)=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var href = m.Groups[1].Value;
            if (href.StartsWith("#") || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            Uri abs;
            try { abs = new Uri(page, href); } catch { continue; }
            yield return (abs, !href.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && href.Contains('.'));
        }
    }

    private static bool MatchesFilters(Uri link, GrabOptions opts)
    {
        var name = Path.GetFileName(link.AbsolutePath);
        if (string.IsNullOrEmpty(name)) return false;

        if (!string.IsNullOrEmpty(opts.IncludeFilter))
        {
            var pats = opts.IncludeFilter.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pats.Length > 0 && !pats.Any(p => MatchesGlob(name, p))) return false;
        }
        if (!string.IsNullOrEmpty(opts.ExcludeFilter))
        {
            var pats = opts.ExcludeFilter.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pats.Any(p => MatchesGlob(name, p))) return false;
        }
        return true;
    }

    private static bool MatchesGlob(string name, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string UrlToPath(Uri url, string root, string baseDomain)
    {
        var safe = Uri.UnescapeDataString(url.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        foreach (var c in Path.GetInvalidPathChars()) safe = safe.Replace(c, '_');
        return Path.Combine(Path.Combine(root, baseDomain), safe);
    }

    private static async Task<long> DownloadFileAsync(Uri url, string path, CancellationToken ct)
    {
        byte[] bytes = url.IsFile
            ? await File.ReadAllBytesAsync(url.LocalPath, ct)
            : await httpGrabClient.GetByteArrayAsync(url, ct);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return bytes.Length;
    }

    private static readonly HttpClient httpGrabClient = new();
}
