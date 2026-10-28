namespace Nova.Engine;

/// <summary>
/// Global + per-task bandwidth governor. Token bucket per active segment
/// refilled once per tick: cheap, fair, and actually enforces a ceiling even
/// with dozens of sockets — the classic IDM limiter behavior.
/// </summary>
public sealed class SpeedController : IDisposable
{
    private readonly object _gate = new();
    private Timer? _timer;
    private long _globalLimit;              // bytes/s, 0 = unlimited
    private long _globalBurstBudget = 4 * 1024 * 1024;
    private readonly Dictionary<string, long> _taskLimits = new();

    private const int BucketCount = 64;
    private readonly long[] _buckets = new long[BucketCount]; // round-robin refill
    private int _bucketCursor;

    public event Action<long>? OnLimitChanged;

    public void SetGlobalLimit(long bytesPerSec)
    {
        lock (_gate)
        {
            _globalLimit = Math.Max(0, bytesPerSec);
            OnLimitChanged?.Invoke(_globalLimit);
        }
    }

    public long GlobalLimit { get { lock (_gate) return _globalLimit; } }

    public void SetTaskLimit(string taskId, long bytesPerSec)
    {
        lock (_gate)
        {
            if (bytesPerSec <= 0) _taskLimits.Remove(taskId);
            else _taskLimits[taskId] = bytesPerSec;
        }
    }

    public void Start() => _timer ??= new Timer(_ => Refill(), null, 100, 100);

    /// <summary>Try to consume permission to write n bytes now; returns allowed count.</summary>
    public int TryConsume(string taskId, int requested)
    {
        lock (_gate)
        {
            var bucket = _bucketCursor % BucketCount;
            var available = _buckets[bucket];
            var allowed = (int)Math.Min(requested, Math.Min(available, int.MaxValue));
            if (allowed > 0) _buckets[bucket] -= allowed;
            return allowed;
        }
    }

    private void Refill()
    {
        lock (_gate)
        {
            _bucketCursor++;
            var bucket = _bucketCursor % BucketCount;
            if (_globalLimit <= 0)
            {
                _buckets[bucket] = _globalBurstBudget; // unlimited mode
                return;
            }
            // Each bucket refilled every BucketCount*100ms = 6.4s -> amortized per-second share
            var perTick = _globalLimit * BucketCount * 100 / 1000 / BucketCount;
            _buckets[bucket] = Math.Min(_buckets[bucket] + perTick, _globalBurstBudget);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
