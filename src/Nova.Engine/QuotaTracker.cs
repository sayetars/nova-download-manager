using Nova.Domain;
using Nova.Persistence;

namespace Nova.Engine;

/// <summary>
/// Byte-quota windows (IDM parity): caps how many bytes may be downloaded inside a
/// rolling window (e.g. "500MB per 60 minutes"). When the cap is hit, the engine
/// pauses all running transfers and auto-resumes them once the oldest samples age
/// out of the window. Checkpoints are persisted (byte_quota_log) so the quota
/// survives restarts; persistence granularity is a few seconds (advisory feature).
/// </summary>
public sealed class QuotaTracker
{
    private readonly ISettingsRepository _settings;
    private readonly Func<long> _nowMs;
    private readonly NovaDatabase? _db;
    private readonly object _lock = new();
    private readonly Queue<(long AtMs, long Bytes)> _samples = new();
    private long _windowBytes;
    private long _unpersistedBytes;
    private long _lastPersistMs;
    private bool _paused;

    public bool Paused { get { lock (_lock) return _paused; } }
    public event Action<bool>? PausedChanged;

    public QuotaTracker(ISettingsRepository settings, Func<long>? nowMs = null, NovaDatabase? db = null)
    {
        _settings = settings;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _db = db;
        try { LoadPersisted(); }
        catch { /* quota is advisory — never block engine boot */ }
    }

    public bool Enabled =>
        string.Equals(_settings.Get("quota.enabled"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cap in bytes; 0 = disabled. Accepts "500MB", "2gb", plain bytes.</summary>
    public long CapBytes => ParseSize(_settings.Get("quota.max_bytes")) ?? 0;

    /// <summary>Rolling window length in minutes (min 1).</summary>
    public long WindowMinutes => Math.Max(1, ParseLong(_settings.Get("quota.window_minutes")) ?? 60);

    public long WindowMs => WindowMinutes * 60_000L;

    /// <summary>Feed downloaded bytes. Thread-safe; called from segment workers.</summary>
    public void OnBytes(long delta)
    {
        if (delta <= 0) return;
        lock (_lock)
        {
            var now = _nowMs();
            _samples.Enqueue((now, delta));
            _windowBytes += delta;
            _unpersistedBytes += delta;
            PruneNoLock();
        }
    }

    /// <summary>
    /// Re-evaluates the pause state (called by the engine's 1s housekeeping tick).
    /// Returns true when the paused flag CHANGED.
    /// </summary>
    public bool Tick()
    {
        bool changed;
        lock (_lock)
        {
            PruneNoLock();
            var cap = CapBytes;
            var shouldPause = Enabled && cap > 0 && _windowBytes >= cap;
            changed = shouldPause != _paused;
            _paused = shouldPause;
            PersistNoLock();
        }
        if (changed) PausedChanged?.Invoke(_paused);
        return changed;
    }

    public QuotaSnapshot Snapshot()
    {
        lock (_lock)
        {
            PruneNoLock();
            long resumeInMs = 0;
            if (_paused && _samples.Count > 0)
                resumeInMs = Math.Max(0, _samples.Peek().AtMs + WindowMs - _nowMs());
            return new QuotaSnapshot(Enabled, CapBytes, WindowMinutes, _windowBytes, _paused, resumeInMs);
        }
    }

    private void PruneNoLock()
    {
        var cutoff = _nowMs() - WindowMs;
        while (_samples.Count > 0 && _samples.Peek().AtMs <= cutoff)
            _windowBytes -= _samples.Dequeue().Bytes;
        if (_windowBytes < 0) _windowBytes = 0;
    }

    // ---- persistence (coarse checkpoints, restart-safe) ------------------------

    private void LoadPersisted()
    {
        if (_db is null) return;
        var cutoff = _nowMs() - WindowMs;
        foreach (var (atMs, bytes) in _db.QuotaSamplesSince(cutoff))
        {
            _samples.Enqueue((atMs, bytes));
            _windowBytes += bytes;
        }
    }

    private void PersistNoLock()
    {
        if (_db is null) return;
        var now = _nowMs();
        if (now - _lastPersistMs < 5_000 || _unpersistedBytes <= 0) return;
        _lastPersistMs = now;
        _db.QuotaAppend(now, _unpersistedBytes);
        _unpersistedBytes = 0;
        _db.QuotaPruneBefore(now - Math.Max(WindowMs * 4, 30 * 60_000L)); // keep bounded
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_db is null || _unpersistedBytes <= 0) return;
            _db.QuotaAppend(_nowMs(), _unpersistedBytes);
            _unpersistedBytes = 0;
        }
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>Parses "500MB"/"2gb"/"1g"/"1024" into bytes (null when invalid/absent).</summary>
    public static long? ParseSize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().ToLowerInvariant();
        double mult = 1;
        if (s.EndsWith("gb")) { mult = 1L << 30; s = s[..^2].Trim(); }
        else if (s.EndsWith("mb")) { mult = 1L << 20; s = s[..^2].Trim(); }
        else if (s.EndsWith("kb")) { mult = 1L << 10; s = s[..^2].Trim(); }
        else if (s.EndsWith('g')) { mult = 1L << 30; s = s[..^1].Trim(); }
        else if (s.EndsWith('m')) { mult = 1L << 20; s = s[..^1].Trim(); }
        else if (s.EndsWith('k')) { mult = 1L << 10; s = s[..^1].Trim(); }
        return long.TryParse(s, out var v) && v > 0 ? (long)(v * mult) : (long?)null;
    }

    private static long? ParseLong(string? s) => long.TryParse(s, out var v) && v > 0 ? v : null;
}

/// <summary>Wire shape for IPC <c>quota_state</c> and engine_status.</summary>
public sealed record QuotaSnapshot(
    bool Enabled, long CapBytes, long WindowMinutes, long UsedBytes, bool Paused, long ResumeInMs)
{
    public static QuotaSnapshot Disabled { get; } = new(false, 0, 60, 0, false, 0);
}
