using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace Nova.DownloadEngine;

/// <summary>
/// Core download engine: multi-segment (HTTP Range) downloads with pause/resume,
/// crash recovery, retry with exponential backoff, and SQLite persistence.
/// </summary>
public sealed class DownloadEngine : IAsyncDisposable
{
    const long MultiSegmentThreshold = 1 << 20;   // 1 MiB
    const int DefaultSegments = 8;
    const int MaxRetries = 5;
    const int BufferSize = 64 * 1024;

    readonly SqliteStore _store;
    readonly HttpClient _http;
    readonly ConcurrentDictionary<string, RunningJob> _running = new();
    readonly SemaphoreSlim _mutex = new(1, 1);

    public string DefaultDownloadDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    sealed class RunningJob
    {
        public required DownloadTask Task { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Worker { get; init; }
        public DateTime LastTickUtc { get; set; }
        public long LastBytes { get; set; }
        public double SpeedBps { get; set; }
    }

    public DownloadEngine(SqliteStore store)
    {
        _store = store;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,   // raw bytes — required for ranges
            MaxConnectionsPerServer = 64,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NovaDownloadManager/0.1");
        Directory.CreateDirectory(DefaultDownloadDir);
    }

    // ------------------------------------------------------------- public API

    public async Task<DownloadTask> AddAsync(string url, string? filePath, int? segments = null)
    {
        var task = new DownloadTask
        {
            Url = url,
            FilePath = Path.GetFullPath(filePath ?? Path.Combine(DefaultDownloadDir, GuessFileName(url))),
            State = DownloadState.Analyzing,
        };
        _store.Save(task);
        try
        {
            var probe = await ProbeAsync(url);
            task.TotalSize = probe.Size;
            task.ETag = probe.ETag;
            task.LastModified = probe.LastModified;
            task.SegmentCount = probe.SupportsRanges && probe.Size >= MultiSegmentThreshold
                ? Math.Clamp(segments ?? DefaultSegments, 1, 32)
                : 1;
            BuildSegments(task);
            task.State = DownloadState.Queued;
            task.UpdatedAt = DateTime.UtcNow;
            _store.Save(task);
            StartWorker(task);
        }
        catch (Exception ex)
        {
            Fail(task, ex.Message);
        }
        return task;
    }

    public async Task<DownloadTask> PauseAsync(string id)
    {
        await StopAsync(id, DownloadState.Pausing);
        return GetTask(id) ?? throw new KeyNotFoundException($"unknown task '{id}'");
    }

    public async Task<DownloadTask> ResumeAsync(string id)
    {
        var task = GetTask(id) ?? throw new KeyNotFoundException($"unknown task '{id}'");
        if (task.State is not (DownloadState.Paused or DownloadState.Interrupted or DownloadState.Failed))
            throw new InvalidOperationException($"task {id} is '{task.State}' — cannot resume");
        await ValidateRemoteAsync(task);
        task.State = DownloadState.Queued;
        task.Error = null;
        task.UpdatedAt = DateTime.UtcNow;
        _store.Save(task);
        StartWorker(task);
        return task;
    }

    public async Task<DownloadTask> CancelAsync(string id)
    {
        await StopAsync(id, DownloadState.Cancelled);
        var task = GetTask(id) ?? throw new KeyNotFoundException($"unknown task '{id}'");
        if (task.State is not (DownloadState.Completed or DownloadState.Failed))
        {
            task.State = DownloadState.Cancelled;
            task.UpdatedAt = DateTime.UtcNow;
            _store.Save(task);
        }
        return task;
    }

    public DownloadTask? GetTask(string id)
    {
        if (_running.TryGetValue(id, out var job)) return job.Task;
        return _store.LoadAll().FirstOrDefault(t => t.Id == id);
    }

    public List<Protocol.TaskInfo> List()
    {
        var tasks = _store.LoadAll();
        var now = DateTime.UtcNow;
        foreach (var t in tasks)
        {
            if (_running.TryGetValue(t.Id, out var job))
            {
                var dt = (now - job.LastTickUtc).TotalSeconds;
                if (dt >= 0.5)
                {
                    job.SpeedBps = Math.Max(0, (t.Received - job.LastBytes) / dt);
                    job.LastTickUtc = now;
                    job.LastBytes = t.Received;
                }
                t.SpeedBps = job.SpeedBps;
            }
        }
        return tasks.Select(ToInfo).ToList();
    }

    /// <summary>Crash recovery: auto-resume interrupted tasks (validated against the remote).</summary>
    public async Task RecoverAsync()
    {
        foreach (var t in _store.LoadAll())
        {
            if (t.State is not (DownloadState.Queued or DownloadState.Downloading or DownloadState.Pausing
                                or DownloadState.Retrying or DownloadState.Interrupted or DownloadState.Analyzing))
                continue;
            try
            {
                await ValidateRemoteAsync(t);
                t.State = DownloadState.Queued;
                t.UpdatedAt = DateTime.UtcNow;
                _store.Save(t);
                StartWorker(t);
            }
            catch (Exception ex)
            {
                Fail(t, ex.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var job in _running.Values)
            try { job.Cts.Cancel(); } catch { }
        try { await Task.WhenAll(_running.Values.Select(j => j.Worker)).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* timeout — process is shutting down anyway */ }
        _http.Dispose();
    }

    // ---------------------------------------------------------------- worker

    async Task StopAsync(string id, DownloadState state)
    {
        RunningJob? job = null;
        await _mutex.WaitAsync();
        try
        {
            if (_running.TryGetValue(id, out job))
            {
                job.Task.State = state;
                job.Cts.Cancel();
                Log($"{id[..8]} stop requested -> {state}");
            }
        }
        finally { _mutex.Release(); }
        if (job is not null)
        {
            try { await job.Worker; } catch { /* workers never throw */ }
        }
    }

    void StartWorker(DownloadTask task)
    {
        var cts = new CancellationTokenSource();
        var worker = Task.Run(() => RunWithRetryAsync(task, cts.Token));
        _running[task.Id] = new RunningJob
        {
            Task = task,
            Cts = cts,
            Worker = worker,
            LastTickUtc = DateTime.UtcNow,
            LastBytes = task.Received,
        };
        Log($"{task.Id[..8]} worker started ({task.SegmentCount} segments, {task.Received} already)");
        _ = worker.ContinueWith(_t => _running.TryRemove(task.Id, out _), TaskScheduler.Default);
    }

    async Task RunWithRetryAsync(DownloadTask task, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                task.State = DownloadState.Downloading;
                task.UpdatedAt = DateTime.UtcNow;
                _store.Save(task);

                await DownloadCoreAsync(task, ct);

                task.State = DownloadState.Verifying;
                _store.Save(task);
                if (task.TotalSize >= 0 && task.Received != task.TotalSize)
                    throw new InvalidDataException($"size mismatch — received {task.Received}, expected {task.TotalSize}");

                task.State = DownloadState.Completed;
                task.Error = null;
                task.UpdatedAt = DateTime.UtcNow;
                _store.Save(task);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task.State = task.State == DownloadState.Pausing
                    ? DownloadState.Paused
                    : DownloadState.Cancelled;
                task.UpdatedAt = DateTime.UtcNow;
                _store.Save(task);
                Log($"{task.Id[..8]} cancelled -> {task.State} @ {task.Received}");
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                if (attempt > MaxRetries)
                {
                    Fail(task, ex.Message);
                    return;
                }
                task.State = DownloadState.Retrying;
                task.Error = $"{ex.Message} — retrying ({attempt}/{MaxRetries})";
                _store.Save(task);
                Log($"{task.Id[..8]} error: {ex.Message} (attempt {attempt})");
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 1 << attempt)), ct); }
                catch (OperationCanceledException)
                {
                    task.State = task.State == DownloadState.Pausing
                        ? DownloadState.Paused
                        : DownloadState.Cancelled;
                    _store.Save(task);
                    return;
                }
            }
        }
    }

    async Task DownloadCoreAsync(DownloadTask task, CancellationToken ct)
    {
        using var handle = File.OpenHandle(task.FilePath, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.Read, FileOptions.Asynchronous);

        if (task.TotalSize >= 0)
        {
            RandomAccess.SetLength(handle, task.TotalSize);
            var pending = task.Segments.Where(s => !s.Completed).ToList();
            if (pending.Count == 0) return;
            await Task.WhenAll(pending.Select(s => DownloadSegmentAsync(task, s, handle, ct)));
        }
        else
        {
            await DownloadSegmentAsync(task, task.Segments[0], handle, ct);
        }
    }

    async Task DownloadSegmentAsync(DownloadTask task, DownloadSegment seg, SafeFileHandle handle, CancellationToken ct)
    {
        long from = seg.Start + seg.Downloaded;
        bool multi = task.SegmentCount > 1;
        bool resuming = seg.Downloaded > 0;
        if (!seg.IsOpenEnded && from > seg.End) return;   // segment already done

        using var resp = await SendRangeRequestAsync(task, from, seg, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} (segment {seg.Id})");

        if (resp.StatusCode == HttpStatusCode.OK && (multi || resuming))
        {
            if (multi)
                throw new InvalidOperationException("server ignored the Range request — multi-segment download not possible");
            seg.Downloaded = 0;                            // single segment: restart from scratch
            from = seg.Start;
        }

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[BufferSize];
        var sw = Stopwatch.StartNew();
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await RandomAccess.WriteAsync(handle, buf.AsMemory(0, n), from, ct);
            from += n;
            seg.Downloaded += n;
            if (sw.Elapsed.TotalSeconds > 2)
            {
                sw.Restart();
                task.UpdatedAt = DateTime.UtcNow;
                _store.Save(task);                        // crash-recovery checkpoint
            }
        }

        if (seg.IsOpenEnded)
        {
            seg.End = from - 1;
            if (task.TotalSize < 0) task.TotalSize = from;
            RandomAccess.SetLength(handle, from);         // trim any stale tail
        }
    }

    async Task<HttpResponseMessage> SendRangeRequestAsync(DownloadTask task, long from, DownloadSegment seg, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, task.Url);
        bool wantRange = task.SegmentCount > 1 || from > 0;
        if (wantRange)
        {
            req.Headers.Range = new RangeHeaderValue(from, seg.IsOpenEnded ? null : seg.End);
            if (!string.IsNullOrEmpty(task.ETag))
                req.Headers.IfRange = new RangeConditionHeaderValue(task.ETag);
        }
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    // ------------------------------------------------------------ HTTP probe

    sealed record ProbeResult(long Size, bool SupportsRanges, string? ETag, string? LastModified);

    async Task<ProbeResult> ProbeAsync(string url)
    {
        long size = -1;
        bool ranges = false;
        string? etag = null;
        string? lm = null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            size = resp.Content.Headers.ContentLength ?? -1;
            ranges = resp.Headers.AcceptRanges.Any(v => string.Equals(v, "bytes", StringComparison.OrdinalIgnoreCase));
            etag = resp.Headers.ETag?.Tag;
            lm = resp.Content.Headers.LastModified?.ToString();
        }
        catch (HttpRequestException)
        {
            // server may not support HEAD — fall through to a ranged GET probe
        }

        if (size < 0 || !ranges)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(0, 0) } };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var cr = resp.Content.Headers.ContentRange;
            if (cr is not null && cr.Length.HasValue)
            {
                size = cr.Length.Value;
                ranges = true;
                etag ??= resp.Headers.ETag?.Tag;
                lm ??= resp.Content.Headers.LastModified?.ToString();
            }
            else
            {
                size = resp.Content.Headers.ContentLength ?? -1;
            }
        }
        return new ProbeResult(size, ranges, etag, lm);
    }

    /// <summary>If the remote file changed (size/ETag), reset segments and restart cleanly.</summary>
    async Task ValidateRemoteAsync(DownloadTask task)
    {
        var probe = await ProbeAsync(task.Url);
        bool changed = probe.Size != task.TotalSize
                       || (probe.ETag is not null && task.ETag is not null
                           && !string.Equals(probe.ETag, task.ETag, StringComparison.Ordinal));
        if (!changed) return;
        task.TotalSize = probe.Size;
        task.ETag = probe.ETag;
        task.LastModified = probe.LastModified;
        task.SegmentCount = probe.SupportsRanges && probe.Size >= MultiSegmentThreshold ? DefaultSegments : 1;
        BuildSegments(task);
    }

    // --------------------------------------------------------------- helpers

    static void BuildSegments(DownloadTask t)
    {
        t.Segments.Clear();
        if (t.TotalSize < 0)
        {
            t.Segments.Add(new DownloadSegment { Id = 0, Start = 0, End = -1 });
            t.SegmentCount = 1;
            return;
        }
        int n = Math.Max(1, t.SegmentCount);
        long per = t.TotalSize / n, rem = t.TotalSize % n, pos = 0;
        for (int i = 0; i < n; i++)
        {
            long len = per + (i < rem ? 1 : 0);
            t.Segments.Add(new DownloadSegment { Id = i, Start = pos, End = pos + len - 1 });
            pos += len;
        }
        t.SegmentCount = n;
    }

    void Fail(DownloadTask task, string message)
    {
        task.State = DownloadState.Failed;
        task.Error = message;
        task.UpdatedAt = DateTime.UtcNow;
        _store.Save(task);
    }

    static Protocol.TaskInfo ToInfo(DownloadTask t) => new()
    {
        Id = t.Id,
        Url = t.Url,
        FilePath = t.FilePath,
        State = t.State.ToString(),
        Received = t.Received,
        Total = t.TotalSize,
        SpeedBytesPerSec = t.SpeedBps,
        Error = t.Error,
    };

    static string GuessFileName(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var name = Path.GetFileName(Uri.UnescapeDataString(path));
            return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
        }
        catch
        {
            return "download.bin";
        }
    }

    static void Log(string message)
    {
        if (Environment.GetEnvironmentVariable("NOVA_LOG") == "1")
            Console.Error.WriteLine($"[engine] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }
}
