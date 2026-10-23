using System.Text;
using Xunit;
using Nova.Domain;
using Nova.Engine;
using Nova.Persistence;

namespace Nova.Engine.Tests;

/// <summary>Boots a real engine stack (SQLite temp + scheduler optional) against test dirs.</summary>
public sealed class EngineFixture : IAsyncDisposable
{
    public string DataDir { get; }
    public string DownloadsDir { get; }
    public NovaDatabase Db { get; }
    public EngineEventBus Bus { get; } = new();
    public DownloadEngine Engine { get; }
    public HttpClient Http { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = true })
    { Timeout = Timeout.InfiniteTimeSpan };

    public EngineFixture(bool withScheduler = false, int maxConcurrent = 3, string? existingDataDir = null,
        Nova.Engine.IMediaPipeline? media = null)
    {
        DataDir = existingDataDir ?? Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));
        DownloadsDir = Path.Combine(DataDir, "downloads");
        Directory.CreateDirectory(DownloadsDir);
        Db = new NovaDatabase(Path.Combine(DataDir, "nova.db"));
        Engine = new DownloadEngine(
            new DownloadTaskRepository(Db), new SegmentRepository(Db),
            new QueueRepository(Db), new SettingsRepository(Db), Bus,
            Http, DownloadsDir, 4, media);
    }

    public static Task<EngineFixture> CreateAsync(Nova.Engine.IMediaPipeline? media = null) => Task.FromResult(new EngineFixture(media: media));

    public static Task<EngineFixture> ReopenAsync(string dataDir) => Task.FromResult(new EngineFixture(existingDataDir: dataDir));

    public bool DeleteOnDispose { get; set; } = true;

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Db.DisposeAsync();
        if (DeleteOnDispose)
        {
            try { Directory.Delete(DataDir, true); } catch { }
        }
        Http.Dispose();
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> predicate, int timeoutMs = 15000, int intervalMs = 100)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(intervalMs);
        Assert.True(predicate(), $"Condition not met within {timeoutMs}ms");
    }
}

public sealed class TestSink : IEngineEventSink
{
    public List<TaskSnapshot> Updates { get; } = new();
    public List<ProgressSnapshot> Progress { get; } = new();
    public List<(string key, string path)> Completed { get; } = new();
    public List<(string key, NovaError error, bool retryable)> Failed { get; } = new();
    private readonly object _lock = new();

    public IReadOnlyList<TaskSnapshot> SnapshotsOf(string key)
    { lock (_lock) return Updates.Where(u => u.TaskKey == key).ToList(); }

    public TaskSnapshot? LastOf(string key)
    { lock (_lock) return Updates.LastOrDefault(u => u.TaskKey == key); }

    public TaskSnapshot? WaitState(string key, DownloadState state, int timeoutMs = 15000)
    {
        Wait.UntilAsync(() => LastOf(key)?.State == state.ToString().ToLowerInvariant(), timeoutMs).GetAwaiter().GetResult();
        return LastOf(key);
    }

    /// <summary>Waits for a state while pumping the engine (drives Retrying→Queued→start, like the scheduler).</summary>
    public TaskSnapshot? WaitStatePumped(string key, DownloadState state, DownloadEngine engine, int timeoutMs = 30000)
    {
        Wait.UntilAsync(() =>
        {
            engine.TryStartNext();
            return LastOf(key)?.State == state.ToString().ToLowerInvariant();
        }, timeoutMs, 150).GetAwaiter().GetResult();
        return LastOf(key);
    }

    public void OnTaskUpdated(TaskSnapshot snapshot, string? note) { lock (_lock) Updates.Add(snapshot); }
    public void OnProgress(ProgressSnapshot snapshot) { lock (_lock) Progress.Add(snapshot); }
    public void OnCompleted(string taskKey, string finalPath, TaskSnapshot snapshot) { lock (_lock) Completed.Add((taskKey, finalPath)); }
    public void OnFailed(string taskKey, NovaError error, bool retryable, TaskSnapshot snapshot) { lock (_lock) Failed.Add((taskKey, error, retryable)); }
}

public static class TestBytes
{
    public static byte[] Pattern(long length, int seed = 7)
    {
        var data = new byte[length];
        var x = (uint)seed;
        for (long i = 0; i < length; i += 4)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            var b = BitConverter.GetBytes(x);
            for (var j = 0; j < 4 && i + j < length; j++)
                data[i + j] = b[j];
        }
        return data;
    }
}
