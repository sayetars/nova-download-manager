using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// In-process event bus: engine → subscribers (IPC broadcast, notifications, tests).
/// Progress events are throttled; state changes are immediate.
/// </summary>
public sealed class EngineEventBus
{
    private readonly object _lock = new();
    private readonly List<IEngineEventSink> _sinks = new();
    private readonly Dictionary<string, long> _lastProgressAt = new();

    public IDisposable Subscribe(IEngineEventSink sink)
    {
        lock (_lock) _sinks.Add(sink);
        return new Unsub(() => { lock (_lock) _sinks.Remove(sink); });
    }

    public void Emit(DownloadTask task, DownloadState state, string? note)
    {
        IEngineEventSink[] sinks;
        lock (_lock) sinks = _sinks.ToArray();
        foreach (var s in sinks)
            s.OnTaskUpdated(TaskSnapshot.From(task), note);
    }

    public void TaskUpdated(DownloadTask task) => Emit(task, task.State, null);

    public void Progress(DownloadTask task, long speedBps)
    {
        var snap = TaskSnapshot.From(task);
        var eta = EstimateEta(task, speedBps);
        IEngineEventSink[] sinks;
        lock (_lock) sinks = _sinks.ToArray();
        foreach (var s in sinks)
            s.OnProgress(new ProgressSnapshot
            {
                TaskKey = task.TaskKey,
                ReceivedBytes = snap.ReceivedBytes,
                TotalBytes = snap.TotalBytes,
                SpeedBps = speedBps,
                EtaSeconds = eta,
                SegmentsDone = task.SegmentsDone,
                SegmentsTotal = task.SegmentsTotal,
                State = task.State.ToString().ToLowerInvariant(),
            });
    }

    /// <summary>High-frequency progress with per-task throttle (default 500ms).</summary>
    public void MaybeProgress(DownloadTask task, long speedBps, int throttleMs = 500)
    {
        var now = Environment.TickCount64;
        lock (_lock)
        {
            if (_lastProgressAt.TryGetValue(task.TaskKey, out var last) && now - last < throttleMs)
                return;
            _lastProgressAt[task.TaskKey] = now;
        }
        Progress(task, speedBps);
    }

    public void Completed(DownloadTask task, string finalPath)
    {
        IEngineEventSink[] sinks;
        lock (_lock) sinks = _sinks.ToArray();
        foreach (var s in sinks) s.OnCompleted(task.TaskKey, finalPath, TaskSnapshot.From(task));
    }

    public void Failed(DownloadTask task, NovaError error, bool retryable)
    {
        IEngineEventSink[] sinks;
        lock (_lock) sinks = _sinks.ToArray();
        foreach (var s in sinks) s.OnFailed(task.TaskKey, error, retryable, TaskSnapshot.From(task));
    }

    private static long EstimateEta(DownloadTask task, long speedBps)
    {
        if (speedBps <= 0 || task.TotalBytes < 0) return -1;
        var remaining = task.TotalBytes - task.ReceivedBytes;
        if (remaining <= 0) return 0;
        return remaining / speedBps;
    }

    private sealed class Unsub : IDisposable
    {
        private readonly Action _act;
        public Unsub(Action act) => _act = act;
        public void Dispose() => _act();
    }
}

public interface IEngineEventSink
{
    void OnTaskUpdated(TaskSnapshot snapshot, string? note);
    void OnProgress(ProgressSnapshot snapshot);
    void OnCompleted(string taskKey, string finalPath, TaskSnapshot snapshot);
    void OnFailed(string taskKey, NovaError error, bool retryable, TaskSnapshot snapshot);
}

/// <summary>Immutable wire-ready snapshot of a task.</summary>
public sealed record TaskSnapshot
{
    public long TaskId { get; init; }
    public string TaskKey { get; init; } = "";
    public string State { get; init; } = "";
    public string Kind { get; init; } = "file";
    public string FileName { get; init; } = "";
    public string Url { get; init; } = "";
    public string? EffectiveUrl { get; init; }
    public string DestinationDir { get; init; } = "";
    public string FinalPath { get; init; } = "";
    public string QueueName { get; init; } = "main";
    public string Category { get; init; } = "other";
    public long TotalBytes { get; init; } = -1;
    public long ReceivedBytes { get; init; }
    public int SegmentCount { get; init; }
    public int SegmentsDone { get; init; }
    public int SegmentsTotal { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCategory { get; init; }
    public string? Validation { get; init; }
    public int Priority { get; init; }
    public int RetryCount { get; init; }
    public long CreatedAtMs { get; init; }
    public long? StartedAtMs { get; init; }
    public long? CompletedAtMs { get; init; }
    // ---- v1.3.0 additions ----
    /// <summary>Comma-separated mirror list (wire convenience; empty when none).</summary>
    public string? Mirrors { get; init; }
    /// <summary>Algo-qualified expected hash ("sha256:hex") when one is set.</summary>
    public string? ExpectedHash { get; init; }
    /// <summary>Task-level cookie header is set (boolean only — value is sensitive).</summary>
    public bool HasCookies { get; init; }
    /// <summary>Sequential (in-order) segment allocation.</summary>
    public bool Sequential { get; init; }
    /// <summary>Post-download conversion preset.</summary>
    public string ConvertPreset { get; init; } = "";
    /// <summary>Number of multi-source URLs (1 = primary only).</summary>
    public int SourceCount { get; init; }
    // ---- v1.4.0 additions ----
    /// <summary>BitTorrent info-hash (hex) when known.</summary>
    public string? BtInfoHash { get; init; }
    /// <summary>BitTorrent: keeps serving after completion (seeding).</summary>
    public bool SeedAfter { get; init; }

    public static TaskSnapshot From(DownloadTask t) => new()
    {
        TaskId = t.Id,
        TaskKey = t.TaskKey,
        State = t.State.ToString().ToLowerInvariant(),
        Kind = t.Kind.ToString().ToLowerInvariant(),
        FileName = t.FileName,
        Url = t.Url,
        EffectiveUrl = t.EffectiveUrl,
        DestinationDir = t.DestinationDir,
        FinalPath = t.FinalPath,
        QueueName = t.QueueName,
        Category = t.Category,
        TotalBytes = t.TotalBytes,
        ReceivedBytes = t.ReceivedBytes,
        SegmentCount = t.SegmentCount,
        SegmentsDone = t.SegmentsDone,
        SegmentsTotal = t.SegmentsTotal,
        ErrorCode = t.ErrorCode,
        ErrorMessage = t.ErrorMessage,
        ErrorCategory = t.ErrorCategory?.ToString().ToLowerInvariant(),
        Validation = t.Validation.ToString().ToLowerInvariant(),
        Priority = (int)t.Priority,
        RetryCount = t.RetryCount,
        CreatedAtMs = t.CreatedAt.ToUnixTimeMilliseconds(),
        StartedAtMs = t.StartedAt?.ToUnixTimeMilliseconds(),
        CompletedAtMs = t.CompletedAt?.ToUnixTimeMilliseconds(),
        Mirrors = t.MirrorUrls.Length > 0 ? string.Join(",", t.Mirrors) : null,
        ExpectedHash = t.ExpectedHash is { Length: > 0 } ? t.ExpectedHash
            : string.IsNullOrEmpty(t.ExpectedSha256) ? null : "sha256:" + t.ExpectedSha256,
        HasCookies = !string.IsNullOrEmpty(t.Cookies),
        Sequential = t.Sequential,
        ConvertPreset = t.ConvertPreset ?? "",
        SourceCount = 1 + t.Mirrors.Count(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)),
        BtInfoHash = string.IsNullOrEmpty(t.BtInfoHash) ? null : t.BtInfoHash,
        SeedAfter = t.SeedAfter,
    };
}

public sealed record ProgressSnapshot
{
    public string TaskKey { get; init; } = "";
    public long ReceivedBytes { get; init; }
    public long TotalBytes { get; init; } = -1;
    public long SpeedBps { get; init; }
    public long EtaSeconds { get; init; } = -1;
    public int SegmentsDone { get; init; }
    public int SegmentsTotal { get; init; }
    public string State { get; init; } = "";
}
