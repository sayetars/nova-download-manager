using System.Collections.Concurrent;
using Nova.Domain;
using Nova.Persistence;

namespace Nova.Engine;

/// <summary>
/// Central coordinator — the ONE authority for task state. All clients (Flutter, extensions,
/// CLI) mutate state only through here. Retries, dedupe and recovery included.
/// The Scheduler component decides WHEN queued tasks start (admission control).
/// </summary>
public sealed class DownloadEngine : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _tasks = new();
    private readonly IDownloadTaskRepository _repo;
    private readonly ISegmentRepository _segs;
    private readonly IQueueRepository _queues;
    private readonly ISettingsRepository _settings;
    private readonly EngineEventBus _events;
    private readonly IMediaPipeline? _media;
    private readonly HttpClient _http;
    private readonly HttpProber _prober;
    private readonly string _defaultDir;
    private readonly int _defaultConnections;
    private readonly LoginVault? _vault;
    private readonly Antivirus? _antivirus;
    private readonly string? _tempRoot;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly System.Timers.Timer _retryTimer;
    private readonly List<string> _quotaPausedKeys = new();
    private readonly ConcurrentDictionary<string, SegmentView[]> _livePlans = new();
    private RateLimiter? _globalLimiter;

    public EngineEventBus Events => _events;
    public IClock Clock { get; set; } = SystemClock.Instance;
    public string DefaultDir => _defaultDir;
    /// <summary>Byte-quota window enforcement (IDM parity); null when not configured.</summary>
    public QuotaTracker? Quota { get; }

    private sealed class Entry
    {
        public required DownloadTask Task;
        public TaskRunner? Runner;
        public Task? RunTask;
        public long NextRetryAtMs;
        public int BackoffAttempt;
        public string? RetryAfterHeader;
        public RateLimiter? TaskLimiter;
        public CancellationTokenSource CancelCts = new();
    }

    public DownloadEngine(IDownloadTaskRepository repo, ISegmentRepository segs,
        IQueueRepository queues, ISettingsRepository settings, EngineEventBus events,
        HttpClient http, string defaultDir, int defaultConnections = 4,
        IMediaPipeline? media = null, LoginVault? vault = null,
        Antivirus? antivirus = null, string? tempRoot = null,
        QuotaTracker? quota = null)
    {
        _repo = repo;
        _segs = segs;
        _queues = queues;
        _settings = settings;
        _events = events;
        _media = media;
        _http = http;
        _defaultDir = defaultDir;
        _defaultConnections = defaultConnections;
        _vault = vault;
        _antivirus = antivirus;
        _tempRoot = string.IsNullOrWhiteSpace(tempRoot) ? null : tempRoot;
        Quota = quota;
        _prober = new HttpProber(http);
        UpdateGlobalRateCap(ParseLong(settings.Get("engine.global_speed_cap")));
        _events.Subscribe(new EngineSelfSink(this));

        _retryTimer = new System.Timers.Timer(1000) { AutoReset = true };
        _retryTimer.Elapsed += (_, _) => ProcessRetryBackoffs();
        _retryTimer.Start();

        RecoverTasks();
    }

    /// <summary>Internal sink: converts task failures into retry scheduling inside the engine.</summary>
    private sealed class EngineSelfSink : IEngineEventSink
    {
        private readonly DownloadEngine _engine;
        public EngineSelfSink(DownloadEngine engine) => _engine = engine;
        public void OnTaskUpdated(TaskSnapshot snapshot, string? note) { }
        public void OnProgress(ProgressSnapshot snapshot) { }
        public void OnCompleted(string taskKey, string finalPath, TaskSnapshot snapshot) { }
        public void OnFailed(string taskKey, NovaError error, bool retryable, TaskSnapshot snapshot)
        {
            if (_engine._tasks.TryGetValue(taskKey, out var e))
                _engine.HandleFailure(e, error, retryable);
        }
    }

    // ---- lifecycle / recovery ----------------------------------------------

    /// <summary>Engine start: mark orphans and optionally resume them.</summary>
    private void RecoverTasks()
    {
        var autoResume = _settings.Get("engine.resume_on_start") != "false";
        foreach (var t in _repo.GetAll())
        {
            var entry = new Entry { Task = t };
            _tasks[t.TaskKey] = entry;

            if (t.State is DownloadState.Analyzing or DownloadState.Downloading
                or DownloadState.Pausing or DownloadState.Retrying or DownloadState.Verifying
                or DownloadState.Interrupted)
            {
                t.State = DownloadState.Interrupted;
                _repo.UpdateState(t.Id, DownloadState.Interrupted, null, "Recovery pass", null);
                _events.TaskUpdated(t);
                if (autoResume && t.ReceivedBytes >= 0)
                {
                    // interrupted tasks with a validator + partial data (or fresh) resume automatically
                    t.State = DownloadState.Queued;
                    _repo.UpdateState(t.Id, DownloadState.Queued, null, null, null);
                    _events.TaskUpdated(t);
                }
            }
        }
    }

    public void UpdateGlobalRateCap(long? maxBps)
    {
        _globalLimiter = maxBps is > 0 ? new RateLimiter(maxBps.Value) : null;
        _settings.Set("engine.global_speed_cap", maxBps?.ToString() ?? "0");
    }

    public RateLimiter? GlobalLimiter => _globalLimiter;

    // ---- add ---------------------------------------------------------------

    public Result<AddDownloadOutcome> Add(NewDownload spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Url) || !Uri.TryCreate(spec.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "ftp" && uri.Scheme != "ftps"))
            return Result<AddDownloadOutcome>.Fail("bad_url", ErrorCategory.Protocol,
                "The link is not a valid HTTP(S)/FTP(S) URL.");

        // dedupe: same URL already tracked & not terminal-failed
        var dup = _tasks.Values.FirstOrDefault(e =>
            string.Equals(e.Task.Url, spec.Url, StringComparison.OrdinalIgnoreCase)
            && e.Task.State is not (DownloadState.Failed or DownloadState.Cancelled));
        if (dup is not null)
            return Result<AddDownloadOutcome>.Success(new AddDownloadOutcome(dup.Task.TaskKey, dup.Task.Id, Duplicate: true));

        var kind = spec.Kind ?? DetectKind(spec.Url);
        var task = new DownloadTask
        {
            TaskKey = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Url = spec.Url,
            FileName = FilenamePolicy.SanitizeFileName(spec.FileName ?? FilenamePolicy.FromUrl(spec.Url) ?? "download"),
            DestinationDir = Path.GetFullPath(spec.DestinationDir ?? _defaultDir),
            QueueName = spec.QueueName ?? "main",
            Category = spec.Category ?? Disk.GuessCategory(spec.FileName ?? spec.Url, null),
            Referer = spec.Referer,
            ExpectedSha256 = spec.ExpectedSha256,
            MaxConnections = Math.Clamp(spec.MaxConnections ?? _defaultConnections, 1, 32),
            SpeedCapBytes = spec.SpeedCapBytes,
            Priority = spec.Priority,
            MediaSelection = spec.MediaSelection,
            MirrorUrls = string.Join('\n', spec.Mirrors ?? Array.Empty<string>()),
            StartAtMs = spec.StartAtMs,
            PostAction = spec.PostAction ?? "",
            State = DownloadState.Created,
        };
        task.Id = _repo.Insert(task);
        _queues.GetOrCreate(task.QueueName);
        _tasks[task.TaskKey] = new Entry { Task = task };

        if (spec.AutoStart)
        {
            task.State = DownloadState.Queued;
            _repo.UpdateState(task.Id, DownloadState.Queued, null, null, null);
            _events.TaskUpdated(task);
        }
        else
        {
            _events.TaskUpdated(task);
        }
        return Result<AddDownloadOutcome>.Success(new AddDownloadOutcome(task.TaskKey, task.Id, Duplicate: false));
    }

    public IReadOnlyList<AddDownloadOutcome> AddBatch(IEnumerable<NewDownload> specs)
        => specs.Select(s => Add(s)).Select(r => r.Ok ? r.Value! : new AddDownloadOutcome("error:" + r.Error!.Code, -1, false)).ToList();

    public static DownloadKind DetectKind(string url)
    {
        var path = url.Split('?')[0];
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return DownloadKind.Hls;
        if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)) return DownloadKind.Dash;
        return DownloadKind.File;
    }

    // ---- control -----------------------------------------------------------

    public bool Pause(string taskKey)
    {
        if (!_tasks.TryGetValue(taskKey, out var e)) return false;
        if (e.Runner is not null)
        {
            e.Task.State = DownloadState.Pausing;
            _events.TaskUpdated(e.Task);
            e.Runner.Pause.Cancel();
        }
        else if (e.Task.State is DownloadState.Queued or DownloadState.Interrupted or DownloadState.Retrying)
        {
            e.Task.State = DownloadState.Paused;
            _repo.UpdateState(e.Task.Id, DownloadState.Paused, null, null, null);
            _events.TaskUpdated(e.Task);
        }
        return true;
    }

    public bool PauseAll(IEnumerable<string>? keys = null)
    {
        var targets = keys ?? _tasks.Keys.ToArray();
        foreach (var k in targets) Pause(k);
        return true;
    }

    public bool Resume(string taskKey)
    {
        if (!_tasks.TryGetValue(taskKey, out var e)) return false;
        if (e.Runner is not null) return false;
        if (e.Task.State is not (DownloadState.Paused or DownloadState.Failed
            or DownloadState.Interrupted or DownloadState.Cancelled or DownloadState.Queued or DownloadState.Created))
            return false;
        e.Task.ErrorCode = null;
        e.Task.ErrorMessage = null;
        e.Task.ErrorCategory = null;
        e.BackoffAttempt = 0;
        e.Task.State = DownloadState.Queued;
        _repo.UpdateState(e.Task.Id, DownloadState.Queued, null, null, null);
        _events.TaskUpdated(e.Task);
        return true;
    }

    public bool ResumeAll(IEnumerable<string>? keys = null)
    {
        var targets = keys ?? _tasks.Keys.ToArray();
        foreach (var k in targets) Resume(k);
        return true;
    }

    public bool Cancel(string taskKey)
    {
        if (!_tasks.TryGetValue(taskKey, out var e)) return false;
        if (e.Runner is not null)
        {
            e.CancelCts.Cancel(); // runner observes → cancels temp file and reports Cancelled
        }
        else if (!e.Task.IsTerminal)
        {
            e.Task.State = DownloadState.Cancelled;
            _repo.UpdateState(e.Task.Id, DownloadState.Cancelled, null, null, null);
            _events.TaskUpdated(e.Task);
            TryDeleteIfExists(e.Task.FinalPath + DownloadDataFile.TempSuffix);
        }
        return true;
    }

    public bool Remove(string taskKey, bool deleteFiles)
    {
        if (!_tasks.TryGetValue(taskKey, out var e)) return false;
        Cancel(taskKey);
        try { e.RunTask?.Wait(3000); } catch { /* ignore */ }

        if (deleteFiles)
        {
            TryDeleteIfExists(e.Task.FinalPath);
            TryDeleteIfExists(e.Task.FinalPath + DownloadDataFile.TempSuffix);
        }
        _tasks.TryRemove(taskKey, out _);
        _repo.Delete(e.Task.Id);
        return true;
    }

    public bool Move(string taskKey, string? queueName, TaskPriority? priority, string? category)
    {
        if (!_tasks.TryGetValue(taskKey, out var e)) return false;
        if (queueName is not null && queueName.Length > 0) { e.Task.QueueName = queueName; _queues.GetOrCreate(queueName); }
        if (priority.HasValue) e.Task.Priority = priority.Value;
        if (category is not null && category.Length > 0) e.Task.Category = category;
        _repo.Update(e.Task);
        _events.TaskUpdated(e.Task);
        return true;
    }

    public bool SetDestination(string taskKey, string dir)
    {
        if (!_tasks.TryGetValue(taskKey, out var e)) return false;
        if (e.Runner is not null) return false; // running tasks can't move
        var full = Path.GetFullPath(dir);
        if (!Directory.Exists(full)) return false;
        var tempOld = e.Task.FinalPath + DownloadDataFile.TempSuffix;
        var tempNew = Path.Combine(full, e.Task.FileName) + DownloadDataFile.TempSuffix;
        if (File.Exists(tempOld)) File.Move(tempOld, tempNew);
        e.Task.DestinationDir = full;
        _repo.Update(e.Task);
        _events.TaskUpdated(e.Task);
        return true;
    }

    public bool Retry(string taskKey)
    {
        if (!_tasks.TryGetValue(taskKey, out var e)) return false;
        if (e.Task.State != DownloadState.Failed) return false;
        e.Task.RetryCount = 0;
        return Resume(taskKey);
    }

    // ---- execution ---------------------------------------------------------

    /// <summary>Queued (and re-ready) tasks in scheduler-friendly priority order — honoring per-task start-at times.</summary>
    public IReadOnlyList<DownloadTask> GetQueued()
        => _tasks.Values.Where(e => e.Task.State == DownloadState.Queued)
            .Select(e => e.Task)
            .Where(t => t.StartAtMs <= 0 || t.StartAtMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .OrderByDescending(t => t.Priority).ThenBy(t => t.Id)
            .ToList();

    public bool IsRunning(string taskKey) =>
        _tasks.TryGetValue(taskKey, out var e) && e.Runner is not null;

    public int ActiveCount => _tasks.Values.Count(e => e.Runner is not null);

    /// <summary>Running tasks per normalized host (per-server cap admission input).</summary>
    public IReadOnlyDictionary<string, int> ActivePerHost()
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _tasks.Values)
        {
            if (e.Runner is null) continue;
            var host = UrlHost.Of(e.Task.EffectiveUrl ?? e.Task.Url);
            if (host.Length == 0) continue;
            dict[host] = dict.GetValueOrDefault(host, 0) + 1;
        }
        return dict;
    }

    public long GlobalSpeedBps => _tasks.Values
        .Where(e => e.Runner is not null)
        .Sum(e => e.Runner!.Speed.SpeedBps);

    /// <summary>Starts ONE queued task. Called by the Scheduler when a slot is free.</summary>
    public bool TryStartNext(Func<DownloadTask, bool>? predicate = null)
    {
        foreach (var e in _tasks.Values.Where(x => x.Task.State == DownloadState.Queued
                     && (x.Task.StartAtMs <= 0 || x.Task.StartAtMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                     .OrderByDescending(x => x.Task.Priority).ThenBy(x => x.Task.Id))
        {
            if (predicate is not null && !predicate(e.Task)) continue;
            StartEntry(e);
            return true;
        }
        return false;
    }

    /// <summary>Starts a specific queued task (scheduler/aging-controlled ordering).</summary>
    public bool TryStartTask(string taskKey)
    {
        if (!_tasks.TryGetValue(taskKey, out var e)) return false;
        if (e.Task.State != DownloadState.Queued || e.Runner is not null) return false;
        StartEntry(e);
        return true;
    }

    private void StartEntry(Entry e)
    {
        if (e.Runner is not null) return;
        if (e.CancelCts.IsCancellationRequested) e.CancelCts = new CancellationTokenSource();
        e.TaskLimiter = e.Task.SpeedCapBytes > 0 ? new RateLimiter(e.Task.SpeedCapBytes) : null;

        var runner = new TaskRunner(_http, _prober, _repo, _segs, _events, _media,
            () => _globalLimiter,
            () => e.TaskLimiter,
            (t, plan) =>
            {
                _segs.ReplaceSegments(t.Id, plan);
                _repo.Update(t);
            },
            t => _repo.Update(t),
            t => _repo.UpdateProgress(t.Id, t.State, t.ReceivedBytes, t.SegmentsDone, t.SegmentsTotal),
            _vault, _antivirus, _tempRoot,
            n => Quota?.OnBytes(n),
            (t, plan) => _livePlans[t.TaskKey] = plan.Select(s => new SegmentView(s.Index, s.Start, s.End, s.Received)).ToArray());

        e.Runner = runner;
        _livePlans.TryRemove(e.Task.TaskKey, out _);
        var runTask = Task.Run(() => runner.RunAsync(e.Task, e.CancelCts.Token, _shutdown.Token));
        e.RunTask = runTask;
        _ = runTask.ContinueWith(prev =>
        {
            e.Runner = null;
            e.RunTask = null;
            if (prev.IsFaulted)
            {
                EngineLog.Error($"runner crashed: {prev.Exception!.GetBaseException().Message}");
                HandleFailure(e, new NovaError("runner_crashed", ErrorCategory.Unknown,
                    "The download engine hit an unexpected error."), retryable: true);
            }
        });
    }

    private void HandleFailure(Entry e, NovaError error, bool retryable)
    {
        e.Task.ErrorCode = error.Code;
        e.Task.ErrorMessage = error.Message;
        e.Task.ErrorCategory = error.Category;
        if (retryable && e.Task.RetryCount < e.Task.MaxRetries)
        {
            e.Task.RetryCount++;
            e.BackoffAttempt++;
            e.Task.State = DownloadState.Retrying;
            e.NextRetryAtMs = Environment.TickCount64 + (long)Backoff.Compute(e.BackoffAttempt, e.RetryAfterHeader).TotalMilliseconds;
            _repo.Update(e.Task);
            _events.TaskUpdated(e.Task);
            return;
        }

        // permanent failure (or retries exhausted) → mirror rotation (IDM parity)
        if (!retryable || e.Task.RetryCount >= e.Task.MaxRetries)
        {
            var next = NextMirror(e.Task);
            if (next is not null)
            {
                EngineLog.Info($"mirror failover: {e.Task.Url} → {next}");
                e.Task.EffectiveUrl = null;
                e.Task.Url = next;
                e.Task.RetryCount = 0;
                e.BackoffAttempt = 0;
                e.Task.ErrorCode = null;
                e.Task.ErrorMessage = null;
                e.Task.State = DownloadState.Queued;
                _repo.Update(e.Task);
                _events.TaskUpdated(e.Task);
                return;
            }
        }

        e.Task.State = DownloadState.Failed;
        _repo.Update(e.Task);
        _events.TaskUpdated(e.Task);
    }

    /// <summary>First mirror not currently in use, or null when exhausted.</summary>
    private static string? NextMirror(DownloadTask t)
    {
        var current = t.EffectiveUrl ?? t.Url;
        foreach (var m in t.Mirrors)
            if (!string.Equals(m, current, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    private void ProcessRetryBackoffs()
    {
        var now = Environment.TickCount64;
        foreach (var e in _tasks.Values.Where(x => x.Task.State == DownloadState.Retrying && x.Runner is null))
        {
            if (now >= e.NextRetryAtMs)
            {
                e.Task.State = DownloadState.Queued;
                _repo.UpdateState(e.Task.Id, DownloadState.Queued, null, null, null);
                _events.TaskUpdated(e.Task);
            }
        }
        TickQuota();
    }

    /// <summary>
    /// IDM parity — byte-quota windows: when the rolling cap is hit, pause every running
    /// task (remembering which ones WE paused); when the window frees up, resume them.
    /// Runs once a second on the housekeeping timer.
    /// </summary>
    private void TickQuota()
    {
        if (Quota is null || !Quota.Tick()) return;
        if (Quota.Paused)
        {
            _quotaPausedKeys.Clear();
            _quotaPausedKeys.AddRange(_tasks.Values.Where(e => e.Runner is not null).Select(e => e.Task.TaskKey));
            PauseAll(_quotaPausedKeys);
            EngineLog.Info($"quota: cap reached — paused {_quotaPausedKeys.Count} task(s)");
        }
        else
        {
            var keys = _quotaPausedKeys.Where(k => _tasks.ContainsKey(k)).ToList();
            _quotaPausedKeys.Clear();
            foreach (var k in keys) Resume(k);
            if (keys.Count > 0)
                EngineLog.Info($"quota: window freed — resumed {keys.Count} task(s)");
        }
    }
    // ---- queries -----------------------------------------------------------

    public IReadOnlyList<DownloadTask> All() => _tasks.Values.Select(e => e.Task)
        .OrderByDescending(t => t.Id).ToList();

    public DownloadTask? Get(string taskKey) =>
        _tasks.TryGetValue(taskKey, out var e) ? e.Task : _repo.GetByKey(taskKey);

    /// <summary>Live per-segment progress for the UI (empty when not running).</summary>
    public SegmentView[] GetSegmentViews(string taskKey) =>
        _livePlans.TryGetValue(taskKey, out var v) ? v : Array.Empty<SegmentView>();

    public void PurgeHistory(int olderThanDays, bool onlyCompleted)
        => _repo.PurgeHistory(DateTimeOffset.UtcNow.AddDays(-olderThanDays), onlyCompleted);

    private static void TryDeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { EngineLog.Warn($"delete failed: {ex.Message}"); }
    }

    private static long? ParseLong(string? s) =>
        long.TryParse(s, out var v) && v > 0 ? v : null;

    public async ValueTask DisposeAsync()
    {
        _retryTimer.Stop();
        _retryTimer.Dispose();
        Quota?.Flush();
        // Hard-stop only: runners mark tasks Interrupted (recoverable) — NOT user-cancelled.
        _shutdown.Cancel();
        foreach (var e in _tasks.Values)
        {
            try { if (e.RunTask is not null) await e.RunTask.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* runners persist what they have */ }
        }
    }
}

/// <summary>Live per-segment progress view for the UI (task_segments IPC).</summary>
public sealed record SegmentView(int Index, long Start, long End, long Received);

public sealed record NewDownload
{
    public string Url { get; init; } = "";
    public string? DestinationDir { get; init; }
    public string? FileName { get; init; }
    public string? QueueName { get; init; }
    public string? Category { get; init; }
    public string? Referer { get; init; }
    public string? ExpectedSha256 { get; init; }
    public int? MaxConnections { get; init; }
    public long SpeedCapBytes { get; init; }
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public DownloadKind? Kind { get; init; }
    public string? MediaSelection { get; init; }
    public bool AutoStart { get; init; } = true;
    /// <summary>Fallback URLs tried in order on permanent failure (IDM mirrors).</summary>
    public IReadOnlyList<string>? Mirrors { get; init; }
    /// <summary>Epoch-ms: the scheduler starts this task only after this time (0 = now).</summary>
    public long StartAtMs { get; init; }
    /// <summary>Per-task post-completion action ("" | exit | shutdown | hibernate | sleep | logoff).</summary>
    public string? PostAction { get; init; }
}

public sealed record AddDownloadOutcome(string TaskKey, long TaskId, bool Duplicate);
