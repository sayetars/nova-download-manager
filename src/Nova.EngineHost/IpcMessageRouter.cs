using System.Text.Json;
using Nova.Domain;
using Nova.Engine;
using Nova.Ipc;
using Nova.Media;
using Nova.Persistence;
using Nova.Scheduler;

namespace Nova.EngineHost;

/// <summary>Maps nipc/1 messages to engine operations and broadcasts events to every client.</summary>
public sealed class IpcMessageRouter : IIpcMessageHandler
{
    private readonly DownloadEngine _engine;
    private readonly SchedulerService _scheduler;
    private readonly ISettingsRepository _settings;
    private readonly IQueueRepository _queues;
    private readonly MediaCandidatesService _mediaProbe;
    private readonly DownloadTaskRepository _taskRepo;
    private readonly string _dataDir;
    private readonly string _downloadsDir;
    private readonly IDisposable _subscription;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public IpcMessageRouter(DownloadEngine engine, SchedulerService scheduler, ISettingsRepository settings,
        IQueueRepository queues, MediaCandidatesService mediaProbe, NovaDatabase db, string dataDir, string downloadsDir)
    {
        _engine = engine;
        _scheduler = scheduler;
        _settings = settings;
        _queues = queues;
        _mediaProbe = mediaProbe;
        _taskRepo = new DownloadTaskRepository(db);
        _dataDir = dataDir;
        _downloadsDir = downloadsDir;

        // engine events → broadcast to all IPC clients (throttled for progress)
        _subscription = engine.Events.Subscribe(new BusSink(this));
    }

    private sealed class BusSink : IEngineEventSink
    {
        private readonly IpcMessageRouter _r;
        public BusSink(IpcMessageRouter r) => _r = r;

        public void OnTaskUpdated(TaskSnapshot s, string? note)
            => _r.Broadcast("task_updated", s);

        public void OnProgress(ProgressSnapshot s)
            => _r.Broadcast("task_progress", s);

        public void OnCompleted(string taskKey, string finalPath, TaskSnapshot s)
        {
            _r.Broadcast("task_completed", new { task_key = taskKey, final_path = finalPath, task = s });
        }

        public void OnFailed(string taskKey, NovaError error, bool retryable, TaskSnapshot s)
        {
            _r.Broadcast("task_failed", new { task_key = taskKey, error = new { code = error.Code, category = error.Category.ToString().ToLowerInvariant(), message = error.Message }, retryable, task = s });
        }
    }

    public IpcServer? Server { private get; set; }

    private void Broadcast(string type, object payload) => Server?.Broadcast(type, payload);

    public async Task<Envelope?> HandleAsync(Envelope env, CancellationToken ct)
    {
        var id = env.Id;
        try
        {
            switch (env.Type)
            {
                case "ping":
                    return Envelope.Ok(id, new { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

                case "engine_status":
                    return Envelope.Ok(id, StatusPayload());

                case "add_download":
                {
                    var req = env.PayloadAs<AddDownloadRequest>() ?? new();
                    var result = _engine.Add(ToSpec(req));
                    if (!result.Ok)
                        return Envelope.Error(id, result.Error!.Code, result.Error.Category.ToString().ToLowerInvariant(), result.Error.Message);
                    var outcome = result.Value!;
                    return Envelope.Ok(id, new AddDownloadResponse
                    {
                        TaskKey = outcome.TaskKey,
                        TaskId = outcome.TaskId,
                        Duplicate = outcome.Duplicate,
                    });
                }

                case "add_downloads":
                {
                    var req = env.PayloadAs<BatchAddRequest>() ?? new();
                    var outcomes = _engine.AddBatch(req.Items.Select(ToSpec));
                    return Envelope.Ok(id, new { tasks = outcomes.Select(o => new { o.TaskKey, o.Duplicate }) });
                }

                case "list_tasks":
                {
                    var req = env.PayloadAs<ListTasksRequest>() ?? new();
                    var all = _engine.All();
                    IEnumerable<Nova.Domain.DownloadTask> filtered = req.Filter switch
                    {
                        "active" => all.Where(t => t.State is DownloadState.Downloading or DownloadState.Analyzing or DownloadState.Verifying or DownloadState.Pausing or DownloadState.Retrying),
                        "queued" => all.Where(t => t.State == DownloadState.Queued),
                        "completed" => all.Where(t => t.State == DownloadState.Completed),
                        "failed" => all.Where(t => t.State is DownloadState.Failed or DownloadState.Interrupted),
                        "paused" => all.Where(t => t.State == DownloadState.Paused),
                        "media" => all.Where(t => t.Kind != DownloadKind.File),
                        _ => all,
                    };
                    if (!string.IsNullOrEmpty(req.Search))
                    {
                        var q = req.Search.Trim();
                        filtered = filtered.Where(t => t.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                       || t.Url.Contains(q, StringComparison.OrdinalIgnoreCase));
                    }
                    var list = filtered.Take(Math.Clamp(req.Limit, 1, 2000)).ToList();
                    return Envelope.Ok(id, new { tasks = list.Select(TaskSnapshot.From) });
                }

                case "get_task":
                {
                    var req = env.PayloadAs<TaskKeyRequest>() ?? new();
                    var t = _engine.Get(req.TaskKey);
                    return t is null
                        ? Envelope.Error(id, "not_found", "not_found", "Task not found.")
                        : Envelope.Ok(id, TaskSnapshot.From(t));
                }

                case "pause_task": return Simple(id, env, _engine.Pause);
                case "resume_task": return Simple(id, env, _engine.Resume);
                case "cancel_task": return Simple(id, env, _engine.Cancel);
                case "retry_task": return Simple(id, env, _engine.Retry);

                case "remove_task":
                {
                    var req = env.PayloadAs<RemoveTaskRequest>() ?? new();
                    var ok = _engine.Remove(req.TaskKey, req.DeleteFile);
                    return ok ? Envelope.Ok(id) : Envelope.Error(id, "not_found", "not_found", "Task not found.");
                }

                case "move_task":
                {
                    var req = env.PayloadAs<MoveTaskRequest>() ?? new();
                    var ok = _engine.Move(req.TaskKey, req.QueueName,
                        req.Priority is { } p ? (TaskPriority)p : null, req.Category);
                    return ok ? Envelope.Ok(id) : Envelope.Error(id, "not_found", "not_found", "Task not found.");
                }

                case "set_destination":
                {
                    var req = env.PayloadAs<SetDestinationRequest>() ?? new();
                    var ok = _engine.SetDestination(req.TaskKey, req.DestinationDir);
                    return ok ? Envelope.Ok(id) : Envelope.Error(id, "busy", "protocol", "Running tasks cannot move; pause first.");
                }

                case "pause_all": _engine.PauseAll(); return Envelope.Ok(id);
                case "resume_all": _engine.ResumeAll(); return Envelope.Ok(id);
                case "cancel_all":
                {
                    foreach (var t in _engine.All().Where(t => !t.IsTerminal)) _engine.Cancel(t.TaskKey);
                    return Envelope.Ok(id);
                }
                case "clear_completed":
                {
                    foreach (var t in _engine.All().Where(t => t.State == DownloadState.Completed))
                        _engine.Remove(t.TaskKey, deleteFiles: false);
                    return Envelope.Ok(id);
                }

                case "purge_history":
                {
                    var req = env.PayloadAs<PurgeHistoryRequest>() ?? new();
                    _engine.PurgeHistory(req.OlderThanDays, req.OnlyCompleted);
                    return Envelope.Ok(id);
                }

                case "list_queues":
                {
                    var all = _queues.GetAll();
                    var tasks = _engine.All();
                    var payloads = all.Select(q => new
                    {
                        name = q.Name,
                        kind = q.Kind.ToString().ToLowerInvariant(),
                        max_concurrent = q.MaxConcurrent,
                        speed_cap_bytes = q.SpeedCapBytes,
                        destination_dir = string.IsNullOrEmpty(q.DestinationDir) ? _downloadsDir : q.DestinationDir,
                        active_count = tasks.Count(t => t.QueueName == q.Name && t.State == DownloadState.Downloading),
                        waiting_count = tasks.Count(t => t.QueueName == q.Name && t.State == DownloadState.Queued),
                        total_count = tasks.Count(t => t.QueueName == q.Name),
                    });
                    return Envelope.Ok(id, new { queues = payloads });
                }

                case "save_queue":
                {
                    var req = env.PayloadAs<SaveQueueRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.Name))
                        return Envelope.Error(id, "bad_request", "protocol", "Queue name is required.");
                    var q = _queues.GetOrCreate(req.Name);
                    if (req.MaxConcurrent is { } mc) q.MaxConcurrent = mc;
                    if (req.SpeedCapBytes is { } sc) q.SpeedCapBytes = sc;
                    if (req.DestinationDir is { } dd && Directory.Exists(dd)) q.DestinationDir = dd;
                    _queues.Save(q);
                    return Envelope.Ok(id);
                }

                case "delete_queue":
                {
                    var req = env.PayloadAs<TaskKeyRequest>() ?? new();
                    if (req.TaskKey == "main")
                        return Envelope.Error(id, "forbidden", "protocol", "The main queue cannot be deleted.");
                    _queues.Delete(req.TaskKey);
                    return Envelope.Ok(id);
                }

                case "get_settings":
                    return Envelope.Ok(id, new { values = _settings.GetAll() });

                case "set_settings":
                {
                    var req = env.PayloadAs<SetSettingsRequest>() ?? new();
                    foreach (var kv in req.Values) _settings.Set(kv.Key, kv.Value);
                    if (req.Values.TryGetValue("engine.global_speed_cap", out var cap) &&
                        long.TryParse(cap, out var capVal))
                        _engine.UpdateGlobalRateCap(capVal > 0 ? capVal : null);
                    if (req.Values.TryGetValue("scheduler.max_concurrent", out var mc) &&
                        int.TryParse(mc, out var mcVal) && mcVal > 0)
                        _settings.Set("scheduler.max_concurrent", mcVal.ToString());
                    return Envelope.Ok(id);
                }

                case "get_schedules":
                    return Envelope.Ok(id, new { rules = SchedulerRulesRead() });

                case "save_schedule":
                {
                    var req = env.PayloadAs<SaveScheduleRequest>() ?? new();
                    var rules = SchedulerRulesRead();
                    var rule = new SchedulerService.RuleDto
                    {
                        Id = req.Id ?? (rules.Count > 0 ? rules.Max(r => r.Id) + 1 : 1),
                        Name = req.Name, DaysOfWeek = req.DaysOfWeek,
                        StartTime = req.StartTime, EndTime = req.EndTime,
                        Enabled = req.Enabled, SpeedCapBytes = req.SpeedCapBytes,
                    };
                    var idx = rules.FindIndex(r => r.Id == rule.Id);
                    if (idx >= 0) rules[idx] = rule; else rules.Add(rule);
                    SchedulerRulesWrite(rules);
                    return Envelope.Ok(id, new { id = rule.Id });
                }

                case "delete_schedule":
                {
                    var req = env.PayloadAs<TaskKeyRequest>() ?? new();
                    var rules = SchedulerRulesRead();
                    var idNum = long.TryParse(req.TaskKey, out var v) ? v : -1;
                    SchedulerRulesWrite(rules.Where(r => r.Id != idNum).ToList());
                    return Envelope.Ok(id);
                }

                case "media_probe":
                {
                    var req = env.PayloadAs<MediaProbeRequest>() ?? new();
                    var resp = await _mediaProbe.AnalyzeAsync(req, ct);
                    return Envelope.Ok(id, resp);
                }

                case "open_file":
                {
                    var req = env.PayloadAs<TaskKeyRequest>() ?? new();
                    var t = _engine.Get(req.TaskKey);
                    if (t is null || t.State != DownloadState.Completed)
                        return Envelope.Error(id, "not_ready", "protocol", "Task is not completed.");
                    return Envelope.Ok(id, new { path = t.FinalPath });
                }

                case "open_folder":
                {
                    var req = env.PayloadAs<TaskKeyRequest>() ?? new();
                    var t = _engine.Get(req.TaskKey);
                    return t is null
                        ? Envelope.Error(id, "not_found", "not_found", "Task not found.")
                        : Envelope.Ok(id, new { path = t.DestinationDir });
                }

                default:
                    return Envelope.Error(id, "unknown_type", "protocol",
                        $"Unknown message type '{env.Type}'.");
            }
        }
        catch (Exception ex)
        {
            EngineLog.Error($"ipc '{env.Type}' failed: {ex.Message}");
            return Envelope.Error(id, "internal_error", "unknown", "The request could not be processed.");
        }
    }

    private Envelope Simple(string id, Envelope env, Func<string, bool> act)
    {
        var req = env.PayloadAs<TaskKeyRequest>() ?? new();
        return act(req.TaskKey)
            ? Envelope.Ok(id)
            : Envelope.Error(id, "not_found", "not_found", "Task not found or wrong state.");
    }

    private object StatusPayload() => new EngineStatusResponse
    {
        ActiveTasks = _engine.All().Count(t => t.State is DownloadState.Downloading or DownloadState.Analyzing or DownloadState.Verifying),
        QueuedTasks = _engine.All().Count(t => t.State == DownloadState.Queued),
        GlobalSpeed = _engine.GlobalSpeedBps,
        UptimeSeconds = (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
        DataDir = _dataDir,
        ProtocolVersion = Protocol.Version,
        Platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    };
    private List<SchedulerService.RuleDto> SchedulerRulesRead()
    {
        var raw = _settings.Get("scheduler.rules");
        if (string.IsNullOrEmpty(raw)) return new();
        try { return JsonSerializer.Deserialize<List<SchedulerService.RuleDto>>(raw) ?? new(); }
        catch { return new(); }
    }

    private void SchedulerRulesWrite(List<SchedulerService.RuleDto> rules) =>
        _settings.Set("scheduler.rules", JsonSerializer.Serialize(rules));

    private NewDownload ToSpec(AddDownloadRequest r) => new()
    {
        Url = r.Url,
        DestinationDir = r.DestinationDir,
        FileName = r.FileName,
        QueueName = r.QueueName,
        Category = r.Category,
        Referer = r.Referer,
        ExpectedSha256 = r.ExpectedSha256,
        MaxConnections = r.MaxConnections,
        Priority = r.Priority is { } p ? (TaskPriority)p : TaskPriority.Normal,
        Kind = r.Kind?.ToLowerInvariant() switch
        {
            "hls" => DownloadKind.Hls,
            "dash" => DownloadKind.Dash,
            _ => null,
        },
        MediaSelection = r.MediaSelection,
        AutoStart = r.AutoStart,
    };
}
