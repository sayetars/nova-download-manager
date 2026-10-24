using System.Collections.Concurrent;
using Nova.Domain;
using Nova.Persistence;

namespace Nova.Engine;

/// <summary>
/// v1.5.0 (G27): per-server performance profiles. Every probe / slice request feeds
/// EWMA throughput + TTFB and success counters; snapshots persist to SQLite every few
/// seconds so profiles survive restarts. Mirror ordering (within the same metalink
/// priority bucket) uses the blended Score — fast, reliable, low-latency servers first.
/// Thread-safe: called concurrently by all segment workers.
/// </summary>
public sealed class ServerProfileService : IDisposable
{
    private const double Alpha = 0.25;           // EWMA smoothing
    private const long PersistIntervalMs = 5000;
    private const long MinSpeedSampleBytes = 256 * 1024;   // samples below this are noisy
    private const int MaxHosts = 512;

    private readonly ConcurrentDictionary<string, State> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServerProfileRepository? _repo;
    private readonly object _persistLock = new();
    private long _lastPersistMs;

    private sealed class State
    {
        public long EwmaBps;
        public long EwmaTtfbMs = -1;
        public long Ok;
        public long Fail;
        public long Bytes;
        public long LastActiveMs;
    }

    public ServerProfileService(ServerProfileRepository? repo = null)
    {
        _repo = repo;
        if (_repo is not null)
        {
            foreach (var p in _repo.GetAll())
                _hosts[p.Host] = new State
                {
                    EwmaBps = p.EwmaBps,
                    EwmaTtfbMs = p.EwmaTtfbMs,
                    Ok = p.OkCount,
                    Fail = p.FailCount,
                    Bytes = p.BytesTotal,
                    LastActiveMs = p.LastActiveMs,
                };
        }
    }

    // ---- sampling ------------------------------------------------------------

    /// <summary>Records one throughput observation (bytes over elapsedMs) for the host.</summary>
    public void RecordSpeed(string url, long bytes, long elapsedMs)
    {
        if (bytes < MinSpeedSampleBytes || elapsedMs <= 0) return;
        var bps = (long)Math.Min(bytes * 1000L / elapsedMs, 4L * 1024 * 1024 * 1024); // sanity cap
        var st = Get(url);
        lock (st)
        {
            st.EwmaBps = st.EwmaBps <= 0 ? bps : (long)(Alpha * bps + (1 - Alpha) * st.EwmaBps);
            st.Bytes += bytes;
            st.Ok++;   // a completed transfer sample is also a success sample
            Touch(st);
        }
        MaybePersist();
    }

    /// <summary>Records time-to-first-byte (ms) — probe or first response headers.</summary>
    public void RecordTtfb(string url, long elapsedMs)
    {
        if (elapsedMs < 0 || elapsedMs > 60_000) return; // outliers (stalls) are noise
        var st = Get(url);
        lock (st)
        {
            st.EwmaTtfbMs = st.EwmaTtfbMs < 0 ? elapsedMs : (long)(Alpha * elapsedMs + (1 - Alpha) * st.EwmaTtfbMs);
            Touch(st);
        }
        MaybePersist();
    }

    /// <summary>Records request outcome (probe/slice success or transport/HTTP failure).</summary>
    public void RecordResult(string url, bool ok)
    {
        var st = Get(url);
        lock (st)
        {
            if (ok) st.Ok++; else st.Fail++;
            Touch(st);
        }
        MaybePersist();
    }

    private static void Touch(State st) => st.LastActiveMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private State Get(string url)
    {
        var host = UrlHost.Of(url);
        if (host.Length == 0) host = "(unknown)";
        return _hosts.GetOrAdd(host, _ => new State());
    }

    // ---- queries -------------------------------------------------------------

    /// <summary>Snapshot for the UI (sorted by last-active then host).</summary>
    public IReadOnlyList<ServerProfile> Snapshot() =>
        _hosts.Select(kv =>
            {
                var s = kv.Value;
                lock (s)
                {
                    return new ServerProfile
                    {
                        Host = kv.Key,
                        EwmaBps = Math.Max(0, s.EwmaBps),
                        EwmaTtfbMs = Math.Max(0, s.EwmaTtfbMs),
                        OkCount = s.Ok,
                        FailCount = s.Fail,
                        BytesTotal = s.Bytes,
                        LastActiveMs = s.LastActiveMs,
                    };
                }
            })
            .OrderByDescending(p => p.LastActiveMs)
            .ThenBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Blend score used for mirror ordering (higher = better). 0 for unknown hosts.</summary>
    public double Score(string url)
    {
        var host = UrlHost.Of(url);
        if (host.Length == 0 || !_hosts.TryGetValue(host, out var s)) return 0;
        lock (s)
        {
            var total = s.Ok + s.Fail;
            if (total == 0) return 0;
            var successRate = (double)s.Ok / total;
            return Math.Max(0, s.EwmaBps) * successRate * successRate / (1.0 + Math.Max(0, s.EwmaTtfbMs) / 1000.0);
        }
    }

    // ---- mirror ordering (G27 core payoff) -------------------------------------

    /// <summary>
    /// Stable mirror ordering: metalink priority is authoritative, but mirrors sharing
    /// the same priority are re-ordered by descending profile score (unknown = 0 → last).
    /// </summary>
    public List<string> OrderMirrors(IEnumerable<string> urls)
    {
        // stable sort by score descending (List.Sort is unstable → decorate-sort-undecorate)
        return urls.Select((u, i) => (Url: u, Score: Score(u), Idx: i))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Idx)
            .Select(x => x.Url)
            .ToList();
    }

    // ---- persistence ----------------------------------------------------------

    private void MaybePersist()
    {
        if (_repo is null) return;
        var now = Environment.TickCount64;
        bool taken = Monitor.TryEnter(_persistLock);
        if (!taken) return; // another thread is persisting
        try
        {
            if (now - Interlocked.Read(ref _lastPersistMs) < PersistIntervalMs) return;
            Interlocked.Exchange(ref _lastPersistMs, now);
            foreach (var p in Snapshot()) _repo.Upsert(p);
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"server-profiles persist failed: {ex.Message}");
        }
        finally { Monitor.Exit(_persistLock); }
    }

    /// <summary>Flushes current state to SQLite (engine shutdown).</summary>
    public void Flush()
    {
        if (_repo is null) return;
        lock (_persistLock)
        {
            foreach (var p in Snapshot()) _repo.Upsert(p);
        }
    }

    public void Reset()
    {
        _hosts.Clear();
        _repo?.DeleteAll();
    }

    public void Dispose() => Flush();
}
