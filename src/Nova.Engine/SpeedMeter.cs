using System.Diagnostics;

namespace Nova.Engine;

/// <summary>Sliding-window byte meter: instantaneous speed + robust ETA.</summary>
public sealed class SpeedMeter
{
    private readonly long _windowMs;
    private readonly Queue<(long ts, long bytes)> _samples = new();
    private long _total;
    private readonly object _lock = new();

    public SpeedMeter(long windowMs = 4000) => _windowMs = windowMs;

    public void Add(long bytes)
    {
        if (bytes <= 0) return;
        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _samples.Enqueue((now, bytes));
            _total += bytes;
            Trim(now);
        }
    }

    /// <summary>Bytes/sec over the window (0 when idle).</summary>
    public long SpeedBps
    {
        get
        {
            lock (_lock)
            {
                Trim(Stopwatch.GetTimestamp());
                if (_samples.Count == 0) return 0;
                return (long)(_total * 1000.0 / _windowMs);
            }
        }
    }

    public long TotalBytes { get { lock (_lock) return _total; } }

    public void Reset() { lock (_lock) { _samples.Clear(); _total = 0; } }

    private void Trim(long now)
    {
        var cutoff = now - _windowMs * Stopwatch.Frequency / 1000;
        while (_samples.Count > 0 && _samples.Peek().ts < cutoff)
        {
            _total -= _samples.Dequeue().bytes;
        }
    }
}

/// <summary>
/// Token-bucket rate limiter (global + per-task caps). Await-consumes budget; fair across awaiters.
/// </summary>
public sealed class RateLimiter
{
    private readonly long _capacityBytes;      // burst (also the refill quantum granularity)
    private long _tokens;                      // available bytes
    private long _lastRefillTicks;
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
    private readonly long _maxRateBps;         // 0 = unlimited

    public RateLimiter(long maxRateBps, long capacityBytes = 256 * 1024)
    {
        _maxRateBps = maxRateBps;
        _capacityBytes = capacityBytes;
        _tokens = capacityBytes;
        _lastRefillTicks = Stopwatch.GetTimestamp();
    }

    public long MaxRateBps => _maxRateBps;

    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        if (_maxRateBps <= 0 || bytes <= 0) return;
        while (true)
        {
            TaskCompletionSource<bool>? waiter = null;
            lock (_lock)
            {
                Refill();
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(waiter);
            }
            using var reg = ct.Register(() => waiter.TrySetCanceled(ct));
            try { await waiter.Task; }
            catch (OperationCanceledException) { throw; }
        }
    }

    /// <summary>Attempts to reduce available budget; used by async pre-check to be fair.</summary>
    public void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = now - _lastRefillTicks;
        if (elapsed <= 0) return;
        var add = (long)((double)elapsed / Stopwatch.Frequency * _maxRateBps);
        if (add > 0)
        {
            _tokens = Math.Min(_capacityBytes, _tokens + add);
            _lastRefillTicks = now;
        }
        // Wake one waiter if budget became available; it will re-check and wake the next.
        if (_waiters.Count > 0 && _tokens > 0)
            if (_waiters.TryDequeue(out var w)) w.TrySetResult(true);
    }
}

/// <summary>Exponential backoff with jitter, honoring Retry-After when supplied.</summary>
public static class Backoff
{
    public static TimeSpan Compute(int attempt, string? retryAfterHeader)
    {
        if (retryAfterHeader is not null &&
            int.TryParse(retryAfterHeader.Trim(), out var secs) && secs is >= 0 and <= 3600)
            return TimeSpan.FromSeconds(secs);

        var cap = TimeSpan.FromMinutes(5);
        var @base = TimeSpan.FromSeconds(1.5);
        var max = Math.Min(attempt, 8);
        var seconds = Math.Min(@base.TotalSeconds * Math.Pow(2, max), cap.TotalSeconds);
        var jitter = Random.Shared.NextDouble() * 0.3 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }
}
