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
    public HttpClient Http { get; } = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        // Loopback-only test traffic must never depend on (or travel through) the
        // machine's system proxy — CI runners may have one configured (T-026).
        // Production keeps its own policy (system proxy honored, IDM-style).
        UseProxy = false,
        // Match production: a wedged TCP connect fails in 15s instead of hanging
        // the runner until the job-level timeout (T-026 hang hardening).
        ConnectTimeout = TimeSpan.FromSeconds(15),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public EngineFixture(bool withScheduler = false, int maxConcurrent = 3, string? existingDataDir = null,
        Nova.Engine.IMediaPipeline? media = null, LoginVault? vault = null)
    {
        DataDir = existingDataDir ?? Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));
        DownloadsDir = Path.Combine(DataDir, "downloads");
        Directory.CreateDirectory(DownloadsDir);
        Db = new NovaDatabase(Path.Combine(DataDir, "nova.db"));
        Vault = vault;
        Engine = new DownloadEngine(
            new DownloadTaskRepository(Db), new SegmentRepository(Db),
            new QueueRepository(Db), new SettingsRepository(Db), Bus,
            Http, DownloadsDir, 4, media, vault);
    }

    /// <summary>v1.8.0: optional login vault wired into the engine (auth tests).</summary>
    public LoginVault? Vault { get; }

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
    /// <summary>CI (esp. windows runners) is slower than dev boxes; NOVA_TEST_TIMEOUT_SCALE
    /// multiplies every wait timeout without touching per-test code. Default 1.</summary>
    public static readonly int Scale = ParseScale();

    private static int ParseScale()
    {
        var raw = Environment.GetEnvironmentVariable("NOVA_TEST_TIMEOUT_SCALE");
        return int.TryParse(raw, out var s) && s >= 1 ? s : 1;
    }

    public static async Task UntilAsync(Func<bool> predicate, int timeoutMs = 15000, int intervalMs = 100,
        Func<string?>? context = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var budget = timeoutMs * Scale;
        while (!predicate() && sw.ElapsedMilliseconds < budget)
            await Task.Delay(intervalMs);
        Assert.True(predicate(), $"Condition not met within {budget}ms"
            + (context is null ? "" : $"\n--- diagnostics ---\n{context()}"));
    }
}

public sealed class TestSink : IEngineEventSink
{
    public List<TaskSnapshot> Updates { get; } = new();
    public List<ProgressSnapshot> Progress { get; } = new();
    public List<(string key, string path)> Completed { get; } = new();
    public List<(string key, NovaError error, bool retryable)> Failed { get; } = new();
    /// <summary>Engine notes (retry reasons, mirrors, notes) — diagnostic gold on CI (T-026).</summary>
    public List<(string key, string? note)> Notes { get; } = new();
    private readonly object _lock = new();

    public IReadOnlyList<TaskSnapshot> SnapshotsOf(string key)
    { lock (_lock) return Updates.Where(u => u.TaskKey == key).ToList(); }

    public TaskSnapshot? LastOf(string key)
    { lock (_lock) return Updates.LastOrDefault(u => u.TaskKey == key); }

    public TaskSnapshot? WaitState(string key, DownloadState state, int timeoutMs = 15000)
    {
        Wait.UntilAsync(() => LastOf(key)?.State == state.ToString().ToLowerInvariant(), timeoutMs,
            context: () => Diagnose(key)).GetAwaiter().GetResult();
        return LastOf(key);
    }

    /// <summary>Waits for a state while pumping the engine (drives Retrying→Queued→start, like the scheduler).</summary>
    public TaskSnapshot? WaitStatePumped(string key, DownloadState state, DownloadEngine engine, int timeoutMs = 30000)
    {
        Wait.UntilAsync(() =>
        {
            engine.TryStartNext();
            return LastOf(key)?.State == state.ToString().ToLowerInvariant();
        }, timeoutMs, 150, context: () => Diagnose(key, engine)).GetAwaiter().GetResult();
        return LastOf(key);
    }

    /// <summary>Last snapshot + last recorded failure — turns a bare "condition not met"
    /// into the actual engine-side reason (state, error, retries, byte counts).
    /// Used by the CI diagnosis lane (windows-build.yml blame/diagnostics round).</summary>
    private string Diagnose(string key, DownloadEngine? engine = null)
    {
        try
        {
            var last = LastOf(key);
            var fail = Failed.LastOrDefault(f => f.key == key);
            var sb = new StringBuilder($"task {key}: ");
            if (last is null) sb.Append("no snapshot yet (engine never started it?)");
            else sb.Append($"state={last.State} received={last.ReceivedBytes}/{last.TotalBytes} " +
                           $"segments={last.SegmentsDone}/{last.SegmentsTotal} retries={last.RetryCount} " +
                           $"validation={last.Validation ?? "-"}");
            if (fail != default)
                sb.Append($"\nlast failure: code={fail.error.Code} category={fail.error.Category} " +
                           $"retryable={fail.retryable} msg={fail.error.Message}");
            var note = Notes.LastOrDefault(n => n.key == key);
            if (note != default) sb.Append($"\nlast note: {note.note}");
            if (engine != null) sb.Append($"\nengine running={CountRunning(engine)}");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"(diagnostics failed: {ex.Message})";
        }
    }

    private static int CountRunning(DownloadEngine engine)
    {
        try { return engine.All().Count(t => t.State is DownloadState.Downloading or DownloadState.Analyzing); }
        catch { return -1; }
    }

    public void OnTaskUpdated(TaskSnapshot snapshot, string? note)
    {
        lock (_lock)
        {
            Updates.Add(snapshot);
            if (note is not null) Notes.Add((snapshot.TaskKey, note));
        }
    }
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
