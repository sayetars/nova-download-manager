namespace Nova.Core.Events;

/// <summary>
/// In-process event bus. The engine raises domain events; IPC layer, scheduler
/// and UI-side bridges subscribe and forward them. No external broker needed.
/// Thread-safe, ordered by publication within a single producer.
/// </summary>
public sealed class EventBus
{
    public static EventBus Instance { get; } = new();

    private readonly object _gate = new();
    private readonly List<Subscription> _subs = new();

    private sealed class Subscription : IDisposable
    {
        public required Type EventType { get; init; }
        public required Delegate Handler { get; init; }
        public required EventBus Owner { get; init; }
        public void Dispose() { lock (Owner._gate) Owner._subs.Remove(this); }
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        var sub = new Subscription { EventType = typeof(TEvent), Handler = handler, Owner = this };
        lock (_gate) _subs.Add(sub);
        return sub;
    }

    public void Publish<TEvent>(TEvent evt)
    {
        if (evt is null) return;
        Subscription[] snapshot;
        lock (_gate) snapshot = _subs.Where(s => s.EventType == typeof(TEvent)).ToArray();
        foreach (var s in snapshot)
        {
            try { ((Action<TEvent>)s.Handler)(evt); }
            catch { /* subscriber must not take the engine down */ }
        }
    }
}

// ---------------------------------------------------------------------------
// Domain events
// ---------------------------------------------------------------------------

public sealed record TaskStateChanged(string TaskId, Models.DownloadState From, Models.DownloadState To,
    Models.FailureReason Reason = Models.FailureReason.None, string? Error = null,
    long? UnixMs = null)
{
    public TaskStateChanged() : this("", default, default) { }
}

public sealed record TaskProgressUpdated(Models.TaskProgress Progress)
{
    public TaskProgressUpdated() : this(new Models.TaskProgress()) { }
}

public sealed record TaskAdded(Models.DownloadTask Task);

public sealed record TaskRemoved(string TaskId, bool DeletedFile);

public sealed record VideoDetectedEvent(Models.VideoCandidate Candidate)
{
    public VideoDetectedEvent() : this(new Models.VideoCandidate()) { }
}

public sealed record GlobalSpeedEvent(long DownloadBytesPerSec, long UploadBytesPerSec, int ActiveTasks, int ActiveSegments);

public sealed record GrabberFileFound(string TaskId, string Url, string AbsolutePath, long SizeBytes, bool Saved);

public sealed record ShutdownRequestedEvent(bool IsError);
