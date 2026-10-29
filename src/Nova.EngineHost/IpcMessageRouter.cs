using System.Text.Json;
using Nova.Domain;
using Nova.Engine;
using Nova.Grabber;
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
    private readonly LoginVault _vault;
    private readonly GrabberService _grabber;
    private readonly HttpClient _http;
    private readonly Action _requestShutdown;
    private readonly ISyncRuleRepository _syncRules;
    private readonly SyncService _syncService;
    private readonly IFeedRuleRepository? _feedRules;
    private readonly FeedService? _feedService;
    private readonly IDisposable _subscription;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly NovaDatabase _db;
    private readonly Nova.Engine.YtDlp.YtDlpBridge _ytdlp;
    private readonly Nova.Engine.ServerProfileService _serverProfiles;
    private readonly Nova.Engine.Security.ClientCertificateService _clientCerts;
    private readonly Nova.Engine.Security.PgpKeyringService _pgpKeys;
    private readonly ClientCertRepository _clientCertRepo;
    private readonly SegmentRepository _segRepo;
    private Nova.Engine.Preview.PreviewServer? _preview;
    private readonly object _previewLock = new();

    // in-flight grabber runs (taskKey → progress), one at a time per engine
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Nova.Grabber.GrabberService.GrabberProgress> _grabberStatus = new();

    public IpcMessageRouter(DownloadEngine engine, SchedulerService scheduler, ISettingsRepository settings,
        IQueueRepository queues, MediaCandidatesService mediaProbe, NovaDatabase db, string dataDir, string downloadsDir,
        LoginVault vault, GrabberService grabber, HttpClient http, Action requestShutdown,
        ISyncRuleRepository? syncRules = null, SyncService? syncService = null,
        IFeedRuleRepository? feedRules = null, FeedService? feedService = null,
        Nova.Engine.YtDlp.YtDlpBridge? ytdlp = null,
        Nova.Engine.ServerProfileService? serverProfiles = null,
        Nova.Engine.Security.ClientCertificateService? clientCerts = null,
        Nova.Engine.Security.PgpKeyringService? pgpKeys = null)
    {
        _engine = engine;
        _scheduler = scheduler;
        _settings = settings;
        _queues = queues;
        _mediaProbe = mediaProbe;
        _taskRepo = new DownloadTaskRepository(db);
        _dataDir = dataDir;
        _downloadsDir = downloadsDir;
        _vault = vault;
        _grabber = grabber;
        _http = http;
        _db = db;
        _requestShutdown = requestShutdown;
        _syncRules = syncRules ?? throw new ArgumentNullException(nameof(syncRules));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _feedRules = feedRules;
        _feedService = feedService;
        _ytdlp = ytdlp ?? new Nova.Engine.YtDlp.YtDlpBridge(k => settings.Get(k));
        _serverProfiles = serverProfiles ?? new Nova.Engine.ServerProfileService();
        _clientCertRepo = new ClientCertRepository(db);
        _clientCerts = clientCerts ?? new Nova.Engine.Security.ClientCertificateService(_clientCertRepo);
        _pgpKeys = pgpKeys ?? new Nova.Engine.Security.PgpKeyringService(new PgpKeyringRepository(db));
        _segRepo = new SegmentRepository(db);

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

                case "quota_state":
                    return Envelope.Ok(id, QuotaStatePayload());

                case "task_segments":
                {
                    var req = env.PayloadAs<TaskKeyRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.TaskKey))
                        return Envelope.Error(id, "bad_request", "protocol", "task_key required.");
                    var views = _engine.GetSegmentViews(req.TaskKey);
                    return Envelope.Ok(id, new
                    {
                        segments = views.Select(v => new
                        {
                            index = v.Index, start = v.Start, end = v.End, received = v.Received,
                            done = v.End >= 0 && v.Received >= v.End - v.Start + 1,
                        }),
                    });
                }

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

                // ---- v1.3.0: feeds, cookies import, WebDAV browser, post-process ----

                case "list_feed_rules":
                {
                    var rules = _feedRules?.GetAll().Select(r => (object)new
                    {
                        id = r.Id, name = r.Name, url = r.Url, filters = r.Filters,
                        destination_dir = r.DestinationDir, category = r.Category,
                        interval_minutes = r.IntervalMinutes, enabled = r.Enabled,
                        last_check_ms = r.LastCheckMs, auto_start = r.AutoStart, max_items = r.MaxItems,
                    }).ToList() ?? new List<object>();
                    return Envelope.Ok(id, new { rules });
                }

                case "save_feed_rule":
                {
                    if (_feedRules is null)
                        return Envelope.Error(id, "unavailable", "protocol", "Feed support is not available.");
                    var req = env.PayloadAs<SaveFeedRuleRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.Name) || !Uri.TryCreate(req.Url, UriKind.Absolute, out var feedUri)
                        || feedUri.Scheme is not ("http" or "https"))
                        return Envelope.Error(id, "bad_request", "protocol", "Feed name and http(s) URL are required.");
                    var rule = new FeedRule
                    {
                        Id = req.Id ?? 0,
                        Name = req.Name.Trim(),
                        Url = req.Url.Trim(),
                        Filters = req.Filters,
                        DestinationDir = req.DestinationDir,
                        Category = string.IsNullOrWhiteSpace(req.Category) ? "other" : req.Category,
                        IntervalMinutes = Math.Clamp(req.IntervalMinutes, 5, 10_080),
                        Enabled = req.Enabled,
                        AutoStart = req.AutoStart,
                        MaxItems = Math.Max(0, req.MaxItems),
                    };
                    if (rule.Id == 0) rule.Id = _feedRules.Insert(rule);
                    else _feedRules.Update(rule);
                    return Envelope.Ok(id, new { id = rule.Id });
                }

                case "delete_feed_rule":
                {
                    if (_feedRules is null)
                        return Envelope.Error(id, "unavailable", "protocol", "Feed support is not available.");
                    var req = env.PayloadAs<DeleteFeedRuleRequest>() ?? new();
                    if (req.Id <= 0) return Envelope.Error(id, "bad_request", "protocol", "Rule id is required.");
                    _feedRules.Delete(req.Id);
                    return Envelope.Ok(id);
                }

                case "run_feed":
                {
                    if (_feedService is null)
                        return Envelope.Error(id, "unavailable", "protocol", "Feed support is not available.");
                    var req = env.PayloadAs<RunFeedRequest>() ?? new();
                    if (req.Id <= 0) return Envelope.Error(id, "bad_request", "protocol", "Rule id is required.");
                    var result = await _feedService.RunRuleAsync(req.Id);
                    return Envelope.Ok(id, new
                    {
                        rule_id = result.RuleId, checked_count = result.Checked,
                        added = result.Added, seen = result.Seen,
                    });
                }

                case "import_cookies":
                {
                    var req = env.PayloadAs<ImportCookiesRequest>() ?? new();
                    var jar = CookieFile.ParseNetscape(req.Content);
                    return Envelope.Ok(id, new { hosts = jar.Select(kv => new { host = kv.Key, cookies = kv.Value }) });
                }

                case "dav_list":
                {
                    var req = env.PayloadAs<FtpListRequest>() ?? new();
                    var creds = string.IsNullOrEmpty(req.AuthUser)
                        ? null : new TaskCredentials(req.AuthUser, req.AuthPassword ?? "");
                    var entries = await Nova.Engine.WebDav.ListAsync(_http, req.Url, creds, ct);
                    return Envelope.Ok(id, new { entries });
                }

                case "task_postprocess":
                {
                    var req = env.PayloadAs<TaskPostProcessRequest>() ?? new();
                    var t = _engine.Get(req.TaskKey);
                    if (t is null || t.State != DownloadState.Completed || !File.Exists(t.FinalPath))
                        return Envelope.Error(id, "not_ready", "protocol", "Task not completed or file missing.");
                    switch (req.Action)
                    {
                        case "extract":
                        {
                            var dir = string.IsNullOrWhiteSpace(req.DestinationDir)
                                ? Path.Combine(t.DestinationDir, Path.GetFileNameWithoutExtension(t.FinalPath) + "_files")
                                : req.DestinationDir;
                            var xr = Nova.Engine.ArchiveTools.Extract(t.FinalPath, dir, ct);
                            return xr.Ok
                                ? Envelope.Ok(id, new { ok = true, tool = xr.Tool, message = xr.Message, entries = xr.Entries, dir })
                                : Envelope.Error(id, "extract_failed", "protocol", xr.Message);
                        }
                        case "mp3":
                        case "remux":
                        {
                            var cr = Nova.Engine.ArchiveTools.Convert(t.FinalPath, req.Action, _settings.Get("ffmpeg.path"), ct);
                            return cr.Ok
                                ? Envelope.Ok(id, new { ok = true, message = cr.Message, output = cr.OutputPath })
                                : Envelope.Error(id, "convert_failed", "protocol", cr.Message);
                        }
                        default:
                            return Envelope.Error(id, "bad_request", "protocol", "Action must be extract, mp3 or remux.");
                    }
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
                        schedule_json = q.ScheduleJson,
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
                    if (req.ScheduleJson is { } sj) q.ScheduleJson = Nova.Scheduler.QueueSchedule.Parse(sj) is { Count: 0 } ? "" : sj;
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
                    if (req.Values.TryGetValue("engine.http2", out var h2)) // v1.8.0 (IDM 6.43 parity), live toggle
                        HttpProtocol.UseHttp2 = h2 != "false";
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

                // ===================== IDM parity additions =====================

                case "update_task":
                {
                    var req = env.PayloadAs<UpdateTaskRequest>() ?? new();
                    var t = _engine.Get(req.TaskKey);
                    if (t is null) return Envelope.Error(id, "not_found", "not_found", "Task not found.");
                    if (t.IsRunning)
                        return Envelope.Error(id, "busy", "protocol", "Pause the task before editing it.");
                    if (req.QueueName is { Length: > 0 }) { t.QueueName = req.QueueName; _queues.GetOrCreate(req.QueueName); }
                    if (req.Category is { Length: > 0 }) t.Category = req.Category;
                    if (req.Referer is not null) t.Referer = req.Referer;
                    if (req.MaxConnections is { } mc) t.MaxConnections = Math.Clamp(mc, 1, 32);
                    if (req.SpeedCapBytes is { } sc) t.SpeedCapBytes = Math.Max(0, sc);
                    if (req.StartAtMs is { } sa) t.StartAtMs = Math.Max(0, sa);
                    if (req.PostAction is not null && PostActionRunner.IsValid(req.PostAction)) t.PostAction = req.PostAction;
                    if (req.Mirrors is not null) t.MirrorUrls = string.Join('\n', req.Mirrors);
                    if (req.Priority is { } p) t.Priority = (TaskPriority)p;
                    if (req.Cookies is not null) t.Cookies = req.Cookies;
                    if (req.ExtraHeaders is not null) t.ExtraHeaders = req.ExtraHeaders;
                    if (req.Sequential is { } sq) t.Sequential = sq;
                    if (req.ExpectedHash is not null) t.ExpectedHash = req.ExpectedHash.Length > 0 ? req.ExpectedHash : null;
                    if (req.ConvertPreset is not null && Nova.Engine.ArchiveTools.IsValidPreset(req.ConvertPreset)) t.ConvertPreset = req.ConvertPreset;
                    _taskRepo.Update(t);
                    _engine.Events.TaskUpdated(t);
                    return Envelope.Ok(id, TaskSnapshot.From(t));
                }

                case "ftp_list":
                {
                    var req = env.PayloadAs<FtpListRequest>() ?? new();
                    var creds = string.IsNullOrEmpty(req.AuthUser)
                        ? null
                        : new TaskCredentials(req.AuthUser, req.AuthPassword ?? "");
                    var entries = await Nova.Engine.FtpTask.ListAsync(req.Url, creds);
                    return Envelope.Ok(id, new { entries });
                }

                case "zip_list":
                {
                    var req = env.PayloadAs<ZipListRequest>() ?? new();
                    List<Nova.Engine.ZipPreview.ZipEntryInfo>? entries = null;
                    string source = "";
                    if (!string.IsNullOrEmpty(req.TaskKey))
                    {
                        var t = _engine.Get(req.TaskKey);
                        if (t is null || t.State != DownloadState.Completed)
                            return Envelope.Error(id, "not_ready", "protocol", "Task not completed.");
                        if (!File.Exists(t.FinalPath))
                            return Envelope.Error(id, "file_missing", "protocol", "The downloaded file no longer exists.");
                        entries = ZipPreview.ListLocal(t.FinalPath);
                        source = t.FinalPath;
                    }
                    else if (!string.IsNullOrEmpty(req.Url))
                    {
                        entries = await ZipPreview.ListRemoteAsync(_http, req.Url, req.Referer, ct);
                        if (entries is null)
                            return Envelope.Error(id, "not_a_zip", "protocol",
                                "The server does not support ranged requests for ZIP preview.");
                        source = req.Url;
                    }
                    else return Envelope.Error(id, "bad_request", "protocol", "task_key or url required.");
                    return Envelope.Ok(id, new { source, entries });
                }

                case "zip_extract":
                {
                    var req = env.PayloadAs<ZipExtractRequest>() ?? new();
                    var t = _engine.Get(req.TaskKey);
                    if (t is null || t.State != DownloadState.Completed || !File.Exists(t.FinalPath))
                        return Envelope.Error(id, "not_ready", "protocol", "Task not completed or file missing.");
                    var dir = string.IsNullOrWhiteSpace(req.DestinationDir)
                        ? Path.Combine(t.DestinationDir, Path.GetFileNameWithoutExtension(t.FileName) + "_files")
                        : req.DestinationDir;
                    try
                    {
                        ZipPreview.ExtractEntries(t.FinalPath, req.Entries, dir);
                        return Envelope.Ok(id, new { extracted = req.Entries.Count, dir });
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        return Envelope.Error(id, "bad_entry", "protocol", ex.Message);
                    }
                }

                case "grabber_start":
                {
                    var req = env.PayloadAs<GrabberStartRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.RootUrl))
                        return Envelope.Error(id, "bad_request", "protocol", "root_url required.");
                    var opts = new Nova.Grabber.GrabberService.GrabberOptions
                    {
                        MaxDepth = Math.Clamp(req.MaxDepth, 0, 10),
                        FollowLinks = req.FollowLinks, Images = req.Images, Css = req.Css,
                        Scripts = req.Scripts, Video = req.Video, RewriteForOffline = req.RewriteForOffline,
                        IncludeFilter = req.IncludeFilter, ExcludeFilter = req.ExcludeFilter,
                        SaveDirectory = string.IsNullOrWhiteSpace(req.SaveDirectory)
                            ? Path.Combine(_downloadsDir, "grabber") : req.SaveDirectory,
                    };
                    var runId = Guid.NewGuid().ToString("N");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var svc = new GrabberService(_db);
                            svc.Progress += p =>
                            {
                                _grabberStatus[runId] = p;
                                Server?.Broadcast("grabber_progress", new { run_id = runId, pages_done = p.PagesDone, pages_queued = p.PagesQueued, files_done = p.FilesDone, bytes_done = p.BytesDone, current = p.Current });
                            };
                            var result = await svc.RunAsync(_http, req.RootUrl, opts, CancellationToken.None);
                            Server?.Broadcast("grabber_done", new { run_id = runId, pages = result.SavedPages.Count, failed = result.FailedPages.Count, files = result.FilesDone });
                        }
                        finally { _grabberStatus.TryRemove(runId, out _); }
                    });
                    return Envelope.Ok(id, new { run_id = runId });
                }

                case "grabber_status":
                    return Envelope.Ok(id, new { runs = _grabberStatus.Select(kv => new { run_id = kv.Key, kv.Value.PagesDone, kv.Value.PagesQueued, kv.Value.FilesDone, kv.Value.BytesDone, kv.Value.Current }) });

                case "grabber_projects":
                    return Envelope.Ok(id, new { projects = _grabber.ListProjects() });

                case "grabber_delete_project":
                {
                    var req = env.PayloadAs<TaskKeyRequest>() ?? new();
                    if (long.TryParse(req.TaskKey, out var pid)) _grabber.DeleteProject(pid);
                    return Envelope.Ok(id);
                }

                case "save_login":
                {
                    var req = env.PayloadAs<SaveLoginRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.Host) || string.IsNullOrWhiteSpace(req.UserName))
                        return Envelope.Error(id, "bad_request", "protocol", "host and user_name required.");
                    _vault.Save(req.Host, req.Realm, req.UserName, req.Password, req.Scheme);
                    return Envelope.Ok(id);
                }

                case "list_logins":
                    return Envelope.Ok(id, new { logins = _vault.ListSafe() });

                case "delete_login":
                {
                    var req = env.PayloadAs<DeleteLoginRequest>() ?? new();
                    _vault.Delete(req.Id);
                    return Envelope.Ok(id);
                }

                case "list_categories":
                {
                    var customs = ListCategoryDefs();
                    return Envelope.Ok(id, new { categories = customs });
                }

                case "save_category":
                {
                    var req = env.PayloadAs<SaveCategoryRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.Name))
                        return Envelope.Error(id, "bad_request", "protocol", "name required.");
                    SaveCategoryDef(req);
                    return Envelope.Ok(id);
                }

                case "delete_category":
                {
                    var req = env.PayloadAs<DeleteCategoryRequest>() ?? new();
                    DeleteCategoryDef(req.Name);
                    return Envelope.Ok(id);
                }

                // v1.8.0: IDM "Save To" tab — "Change folder for … category on last selected"
                case "update_category_dir":
                {
                    var req = env.PayloadAs<UpdateCategoryDirRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.Name))
                        return Envelope.Error(id, "bad_request", "protocol", "name required.");
                    var updated = UpdateCategoryDir(req.Name, req.Dir ?? "");
                    return Envelope.Ok(id, new { updated });
                }

                // v1.8.0: IDM Proxy tab — "Get System" button
                case "detect_system_proxy":
                {
                    var sp = SystemProxy.Read();
                    return Envelope.Ok(id, new
                    {
                        available = sp.Available, mode = sp.Mode, scheme = sp.Scheme,
                        host = sp.Host, port = sp.Port, bypass = sp.Bypass,
                        pac_url = sp.PacUrl, source = sp.Source,
                    });
                }

                // v1.8.0: PAC script smoke test (Settings → Proxy → Test)
                case "test_pac":
                {
                    var req = env.PayloadAs<TestPacRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.Url) || string.IsNullOrWhiteSpace(req.TestUrl))
                        return Envelope.Error(id, "bad_request", "protocol", "url and test_url are required.");
                    try
                    {
                        var script = await PacSource.LoadAsync(req.Url.Trim(), ct);
                        var resolver = new PacResolver(script);
                        var host = Uri.TryCreate(req.TestUrl.Trim(), UriKind.Absolute, out var u) ? u.Host : "";
                        var decision = resolver.Resolve(req.TestUrl.Trim(), host);
                        return Envelope.Ok(id, decision is null
                            ? new { result = "DIRECT" }
                            : new { result = decision.Mode, host = decision.Host, port = decision.Port });
                    }
                    catch (Exception ex)
                    {
                        return Envelope.Error(id, "pac_failed", "network", ex.Message);
                    }
                }

                case "export_tasks":
                {
                    var tasks = _engine.All().Select(TaskSnapshot.From).ToList();
                    return Envelope.Ok(id, new { json = JsonSerializer.Serialize(new { version = 1, exported_at = DateTimeOffset.UtcNow, tasks }) });
                }

                case "import_tasks":
                {
                    var req = env.PayloadAs<ImportTasksRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.Json))
                        return Envelope.Error(id, "bad_request", "protocol", "json required.");
                    List<AddDownloadRequest>? items;
                    try
                    {
                        var doc = JsonSerializer.Deserialize<JsonElement>(req.Json);
                        items = JsonSerializer.Deserialize<List<AddDownloadRequest>>(doc.GetProperty("tasks").GetRawText());
                    }
                    catch (Exception)
                    {
                        return Envelope.Error(id, "bad_json", "protocol", "The import file is not a Nova export.");
                    }
                    if (items is null) items = new();
                    var outcomes = _engine.AddBatch(items.Select(i => ToSpec(i, req.AutoStart)));
                    return Envelope.Ok(id, new { imported = outcomes.Count, tasks = outcomes.Select(o => new { o.TaskKey, o.Duplicate }) });
                }

                case "check_update":
                {
                    var req = env.PayloadAs<CheckUpdateRequest>() ?? new();
                    var url = string.IsNullOrWhiteSpace(req.UpdateUrl) ? _settings.Get("update.url") : req.UpdateUrl;
                    if (string.IsNullOrWhiteSpace(url))
                        return Envelope.Error(id, "disabled", "protocol", "No update endpoint configured (update.url).");
                    try
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                        using var resp = await http.GetAsync(url, ct);
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        // v1.7.0 — structured update manifest: {"version":"1.8.0","channel":"stable","url":"…","notes":"…"}
                        // Compared against VersionInfo (generated from version.json). Falls back to raw passthrough.
                        System.Text.Json.JsonDocument? doc = null;
                        try { doc = System.Text.Json.JsonDocument.Parse(body); } catch { /* legacy endpoint */ }
                        var root = doc?.RootElement;
                        if (root is { ValueKind: System.Text.Json.JsonValueKind.Object } && root.Value.TryGetProperty("version", out var vEl)
                            && vEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var latest = vEl.GetString();
                            var current = Nova.Domain.VersionInfo.Version;
                            var newer = Nova.Domain.SemVersion.TryParse(latest) is { } lv
                                        && Nova.Domain.SemVersion.TryParse(current) is { } cv
                                        && lv > cv;
                            string? dl = null, notes = null;
                            if (root.Value.TryGetProperty("url", out var uEl) && uEl.ValueKind == System.Text.Json.JsonValueKind.String) dl = uEl.GetString();
                            if (root.Value.TryGetProperty("notes", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String) notes = nEl.GetString();
                            return Envelope.Ok(id, new
                            {
                                manifest = true, status_code = (int)resp.StatusCode, update_available = newer,
                                current_version = current, latest_version = latest,
                                channel = Nova.Domain.VersionInfo.Channel, url = dl, notes = notes,
                            });
                        }
                        return Envelope.Ok(id, new { status_code = (int)resp.StatusCode, body = body.Length > 4000 ? body[..4000] : body });
                    }
                    catch (Exception ex)
                    {
                        return Envelope.Error(id, "network", "network", ex.Message);
                    }
                }

                case "cancel_post_action":
                {
                    _settings.Set("engine.post_action", "");
                    return Envelope.Ok(id);
                }

                case "expand_pattern":
                {
                    var req = env.PayloadAs<GrabberStartRequest>() ?? new();   // reuse: RootUrl holds the pattern
                    var urls = Nova.Engine.UrlPatterns.Expand(req.RootUrl);
                    return Envelope.Ok(id, new { urls });
                }

                // ---- IDM parity v1.2.0: sync queues + per-server caps + IDM import ----

                case "list_sync_rules":
                {
                    var rules = _syncRules.GetAll().Select(r => new
                    {
                        id = r.Id, name = r.Name, urls = r.Urls,
                        destination_dir = r.DestinationDir, category = r.Category,
                        interval_minutes = r.IntervalMinutes, enabled = r.Enabled, last_run_ms = r.LastRunMs,
                    });
                    return Envelope.Ok(id, new { rules });
                }

                case "save_sync_rule":
                {
                    var req = env.PayloadAs<SaveSyncRuleRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Urls))
                        return Envelope.Error(id, "bad_request", "protocol", "Rule name and URLs are required.");
                    var rule = new SyncRule
                    {
                        Id = req.Id ?? 0,
                        Name = req.Name.Trim(),
                        Urls = req.Urls,
                        DestinationDir = req.DestinationDir,
                        Category = string.IsNullOrWhiteSpace(req.Category) ? "other" : req.Category,
                        IntervalMinutes = Math.Clamp(req.IntervalMinutes, 1, 100_80),
                        Enabled = req.Enabled,
                    };
                    if (rule.Id == 0) rule.Id = _syncRules.Insert(rule);
                    else _syncRules.Update(rule);
                    return Envelope.Ok(id, new { id = rule.Id });
                }

                case "delete_sync_rule":
                {
                    var req = env.PayloadAs<DeleteSyncRuleRequest>() ?? new();
                    if (req.Id <= 0) return Envelope.Error(id, "bad_request", "protocol", "Rule id is required.");
                    _syncRules.Delete(req.Id);
                    _syncRules.ForgetRule(req.Id);
                    return Envelope.Ok(id);
                }

                case "run_sync":
                {
                    var req = env.PayloadAs<RunSyncRequest>() ?? new();
                    if (req.Id <= 0) return Envelope.Error(id, "bad_request", "protocol", "Rule id is required.");
                    var result = await _syncService.RunRuleAsync(req.Id, req.Force);
                    return Envelope.Ok(id, new
                    {
                        rule_id = result.RuleId, checked_count = result.Checked, added = result.Added,
                        unchanged = result.Unchanged, failed = result.Failed,
                    });
                }

                case "import_idm":
                {
                    var req = env.PayloadAs<ImportIdmRequest>() ?? new();
                    var urls = Nova.Engine.IdmImport.ExtractUrls(req.Content);
                    if (urls.Count == 0)
                        return Envelope.Ok(id, new { imported = 0, duplicates = 0, urls = Array.Empty<string>() });
                    var outcomes = _engine.AddBatch(urls.Select(u => new NewDownload { Url = u, AutoStart = req.AutoStart }));
                    var imported = outcomes.Count(o => !o.Duplicate && !o.TaskKey.StartsWith("error:"));
                    var duplicates = outcomes.Count(o => o.Duplicate);
                    return Envelope.Ok(id, new { imported, duplicates, urls });
                }

                case "per_server_overview":
                {
                    var active = _engine.ActivePerHost();
                    var defaults = Nova.Scheduler.PerServerPolicy.ParseDefault(_settings.Get("scheduler.per_server_max"));
                    var exceptions = Nova.Scheduler.PerServerPolicy.ParseExceptions(_settings.Get("scheduler.server_exceptions"));
                    return Envelope.Ok(id, new
                    {
                        default_max = defaults,
                        active = active,
                        exceptions = exceptions.Select(e => new { host = e.Host, max = e.Max }),
                    });
                }

                // ---- v1.5.0 G-track -----------------------------------------------------

                case "server_profiles":
                {
                    var rows = _serverProfiles.Snapshot();
                    var active = _engine.ActivePerHost();
                    return Envelope.Ok(id, new
                    {
                        profiles = rows.Select(p => new
                        {
                            host = p.Host,
                            ewma_bps = p.EwmaBps,
                            ewma_ttfb_ms = p.EwmaTtfbMs,
                            ok = p.OkCount,
                            fail = p.FailCount,
                            bytes = p.BytesTotal,
                            last_active_ms = p.LastActiveMs,
                            active_now = active.TryGetValue(p.Host, out var n) ? n : 0,
                            score = Math.Round(p.Score, 1),
                        }),
                    });
                }

                case "reset_server_profiles":
                {
                    _serverProfiles.Reset();
                    return Envelope.Ok(id);
                }

                case "preview_start":
                {
                    var req = env.PayloadAs<PreviewStartRequest>() ?? new();
                    var task = _engine.Get(req.TaskKey);
                    if (task is null)
                        return Envelope.Error(id, "not_found", "not_found", "Task not found.");
                    lock (_previewLock)
                    {
                        _preview ??= new Nova.Engine.Preview.PreviewServer(
                            _engine.Get, _engine.GetSegmentViews,
                            taskId => _segRepo.GetForTask(taskId));
                        _preview.Start();
                        return Envelope.Ok(id, new
                        {
                            url = _preview.UrlFor(task),
                            port = _preview.Port,
                            file_name = task.FileName,
                            total_bytes = task.TotalBytes,
                            received_bytes = task.ReceivedBytes,
                            mime = Nova.Engine.Preview.PreviewServer.GuessMime(task.FileName),
                        });
                    }
                }

                case "preview_stop":
                {
                    lock (_previewLock)
                    {
                        _preview?.Dispose();
                        _preview = null;
                    }
                    return Envelope.Ok(id);
                }

                case "ytdlp_status":
                {
                    var path = _ytdlp.BinaryPath();
                    return Envelope.Ok(id, new
                    {
                        available = path is not null,
                        path = path,
                        version = _ytdlp.Version(),
                    });
                }

                case "ytdlp_analyze":
                {
                    var req = env.PayloadAs<YtDlpAnalyzeRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.Url))
                        return Envelope.Error(id, "bad_request", "protocol", "url required.");
                    var info = await _ytdlp.AnalyzeAsync(req.Url, ct);
                    return Envelope.Ok(id, new
                    {
                        extractor = info.Extractor,
                        id = info.Id,
                        title = info.Title,
                        uploader = info.Uploader,
                        duration_seconds = info.DurationSeconds,
                        thumbnail = info.Thumbnail,
                        webpage_url = info.WebpageUrl,
                        formats = info.Formats.Select(f => new
                        {
                            format_id = f.FormatId,
                            ext = f.Ext,
                            resolution = f.Resolution,
                            fps = f.Fps,
                            vcodec = f.Vcodec,
                            acodec = f.Acodec,
                            tbr = f.Tbr,
                            abr = f.Abr,
                            filesize = f.EffectiveSize,
                            protocol = f.Protocol,
                            note = f.Note,
                            direct = f.DirectDownloadable,
                        }),
                    });
                }

                case "ytdlp_download":
                {
                    var req = env.PayloadAs<YtDlpDownloadRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.Url) || string.IsNullOrWhiteSpace(req.FormatId))
                        return Envelope.Error(id, "bad_request", "protocol", "url and format_id required.");

                    // ---- v1.6.0: NATIVE mode — yt-dlp itself downloads (and merges
                    // video+audio / site fragments). The only path for merge-only formats.
                    if (req.Native)
                    {
                        var formatId = string.IsNullOrWhiteSpace(req.AudioFormatId)
                            ? req.FormatId
                            : $"{req.FormatId}+{req.AudioFormatId}";
                        var name = req.FileName;
                        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(req.Title))
                        {
                            var ext = string.IsNullOrWhiteSpace(req.Ext) ? "mp4" : req.Ext;
                            name = $"{FilenamePolicy.SanitizeFileName(req.Title)}.{ext}";
                        }
                        var sel = new Nova.Engine.YtDlp.YtDlpNativeSelection
                        {
                            Page = req.Url,
                            FormatId = formatId,
                            Title = req.Title,
                            Ext = req.Ext,
                            TotalBytes = req.TotalBytes ?? 0,
                        };
                        var nativeOutcome = _engine.Add(new NewDownload
                        {
                            Url = req.Url,             // the page — yt-dlp re-resolves it itself
                            DestinationDir = req.DestinationDir,
                            FileName = name,
                            Referer = req.Referer ?? req.Url,
                            Cookies = req.Cookies,
                            AutoStart = req.AutoStart,
                            MediaSelection = sel.Encode(),
                        });
                        if (!nativeOutcome.Ok)
                            return Envelope.Error(id, nativeOutcome.Error!.Code,
                                nativeOutcome.Error.Category.ToString().ToLowerInvariant(),
                                nativeOutcome.Error.Message);
                        return Envelope.Ok(id, new
                        {
                            task_key = nativeOutcome.Value!.TaskKey,
                            task_id = nativeOutcome.Value.TaskId,
                            duplicate = nativeOutcome.Value.Duplicate,
                            resolved_url = req.Url,
                            native = true,
                        });
                    }

                    var urls = await _ytdlp.ResolveAsync(req.Url, req.FormatId, ct);
                    var direct = urls[0]; // engine downloads the resolved URL (HLS manifests route to the media pipeline)

                    // sensible file name: explicit > title.ext > from-URL
                    var name2 = req.FileName;
                    if (string.IsNullOrWhiteSpace(name2) && !string.IsNullOrWhiteSpace(req.Title))
                    {
                        var ext = string.IsNullOrWhiteSpace(req.Ext)
                            ? (Path.GetExtension(direct.Split('?')[0]) is { Length: > 1 } e ? e.TrimStart('.') : "mp4")
                            : req.Ext;
                        name2 = $"{FilenamePolicy.SanitizeFileName(req.Title)}.{ext}";
                    }

                    var outcome = _engine.Add(new NewDownload
                    {
                        Url = direct,
                        DestinationDir = req.DestinationDir,
                        FileName = name2,
                        Referer = req.Referer ?? req.Url,
                        Cookies = req.Cookies,
                        Sequential = true,   // enables partial preview while it streams in
                        AutoStart = req.AutoStart,
                        MediaSelection = Nova.Engine.YtDlp.Json.Selection(new { source = "ytdlp", page = req.Url, format_id = req.FormatId }),
                    });
                    if (!outcome.Ok)
                        return Envelope.Error(id, outcome.Error!.Code, outcome.Error.Category.ToString().ToLowerInvariant(), outcome.Error.Message);
                    return Envelope.Ok(id, new
                    {
                        task_key = outcome.Value!.TaskKey,
                        task_id = outcome.Value.TaskId,
                        duplicate = outcome.Value.Duplicate,
                        resolved_url = direct,
                        native = false,
                    });
                }

                case "list_client_certs":
                {
                    var rows = _clientCertRepo.List();
                    return Envelope.Ok(id, new
                    {
                        certs = rows.Select(c => new
                        {
                            id = c.Id,
                            host_pattern = c.HostPattern,
                            source = c.Source,
                            cert_path = c.CertPath,
                            key_path = c.KeyPath,
                            store_name = c.StoreName,
                            thumbprint = c.Thumbprint,
                            enabled = c.Enabled,
                        }),
                    });
                }

                case "save_client_cert":
                {
                    var req = env.PayloadAs<SaveClientCertRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.HostPattern) || req.HostPattern.Length > 253)
                        return Envelope.Error(id, "bad_request", "protocol", "host_pattern required.");
                    var binding = new ClientCertBinding
                    {
                        HostPattern = req.HostPattern,
                        Source = string.IsNullOrWhiteSpace(req.Source) ? "pfx" : req.Source,
                        CertPath = req.CertPath ?? "",
                        KeyPath = req.KeyPath ?? "",
                        StoreName = string.IsNullOrWhiteSpace(req.StoreName) ? "My" : req.StoreName,
                        Thumbprint = req.Thumbprint ?? "",
                        Enabled = req.Enabled,
                    };
                    var error = _clientCerts.Validate(binding, req.Passphrase ?? "");
                    if (error is not null)
                        return Envelope.Error(id, "cert_invalid", "tls", error);
                    var id2 = _clientCertRepo.Save(binding, req.Passphrase ?? "");
                    return Envelope.Ok(id, new { id = id2 });
                }

                case "delete_client_cert":
                {
                    var req = env.PayloadAs<DeleteClientCertRequest>() ?? new();
                    return _clientCertRepo.Delete(req.Id)
                        ? Envelope.Ok(id)
                        : Envelope.Error(id, "not_found", "not_found", "Binding not found.");
                }

                case "list_pgp_keys":
                {
                    var rows = _pgpKeys.List();
                    return Envelope.Ok(id, new
                    {
                        keys = rows.Select(k => new
                        {
                            id = k.Id,
                            label = k.Label,
                            fingerprint = k.Fingerprint,
                            created_at_ms = k.CreatedAt.ToUnixTimeMilliseconds(),
                        }),
                    });
                }

                case "save_pgp_key":
                {
                    var req = env.PayloadAs<SavePgpKeyRequest>() ?? new();
                    if (string.IsNullOrWhiteSpace(req.ArmoredKey))
                        return Envelope.Error(id, "bad_request", "protocol", "armored_key required.");
                    var error = _pgpKeys.Import(req.Label ?? "", req.ArmoredKey);
                    if (error is not null)
                        return Envelope.Error(id, "pgp_invalid", "pgp", error);
                    return Envelope.Ok(id, new { fingerprint = Nova.Engine.Security.PgpVerifier.FingerprintOf(req.ArmoredKey) });
                }

                case "delete_pgp_key":
                {
                    var req = env.PayloadAs<DeletePgpKeyRequest>() ?? new();
                    return _pgpKeys.Delete(req.Id)
                        ? Envelope.Ok(id)
                        : Envelope.Error(id, "not_found", "not_found", "Key not found.");
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
        EngineVersion = Nova.Domain.VersionInfo.Version,
        Channel = Nova.Domain.VersionInfo.Channel,
        DbSchemaVersion = _db.SchemaVersion,
        MinEngineForExtension = Nova.Domain.VersionInfo.MinEngineForExtension,
        ActiveTasks = _engine.All().Count(t => t.State is DownloadState.Downloading or DownloadState.Analyzing or DownloadState.Verifying),
        QueuedTasks = _engine.All().Count(t => t.State == DownloadState.Queued),
        GlobalSpeed = _engine.GlobalSpeedBps,
        UptimeSeconds = (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
        DataDir = _dataDir,
        ProtocolVersion = Protocol.Version,
        Platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        Quota = QuotaStatePayload(),
    };

    /// <summary>Byte-quota window state for the UI (IDM parity).</summary>
    private QuotaStateDto QuotaStatePayload()
    {
        var q = _engine.Quota?.Snapshot();
        return q is null
            ? new QuotaStateDto()
            : new QuotaStateDto
            {
                Enabled = q.Enabled, CapBytes = q.CapBytes, WindowMinutes = q.WindowMinutes,
                UsedBytes = q.UsedBytes, Paused = q.Paused, ResumeInMs = q.ResumeInMs,
            };
    }
    private List<SchedulerService.RuleDto> SchedulerRulesRead()
    {
        var raw = _settings.Get("scheduler.rules");
        if (string.IsNullOrEmpty(raw)) return new();
        try { return JsonSerializer.Deserialize<List<SchedulerService.RuleDto>>(raw) ?? new(); }
        catch { return new(); }
    }

    private void SchedulerRulesWrite(List<SchedulerService.RuleDto> rules) =>
        _settings.Set("scheduler.rules", JsonSerializer.Serialize(rules));

    private NewDownload ToSpec(AddDownloadRequest r) => ToSpec(r, r.AutoStart);

    private static NewDownload ToSpec(AddDownloadRequest r, bool autoStart) => new()
    {
        Url = r.Url,
        DestinationDir = r.DestinationDir,
        FileName = r.FileName,
        QueueName = r.QueueName,
        Category = r.Category,
        Referer = r.Referer,
        ExpectedSha256 = r.ExpectedSha256,
        MaxConnections = r.MaxConnections,
        SpeedCapBytes = 0,
        Priority = r.Priority is { } p ? (TaskPriority)p : TaskPriority.Normal,
        Kind = r.Kind?.ToLowerInvariant() switch
        {
            "hls" => DownloadKind.Hls,
            "dash" => DownloadKind.Dash,
            "torrent" => DownloadKind.Torrent,
            _ => null,
        },
        MediaSelection = r.MediaSelection,
        AutoStart = autoStart,
        Mirrors = r.Mirrors,
        StartAtMs = r.StartAtMs,
        PostAction = r.PostAction,
        Cookies = r.Cookies,
        ExtraHeaders = r.ExtraHeaders,
        Sequential = r.Sequential,
        ExpectedHash = r.ExpectedHash,
        PieceHashes = r.PieceHashes,
        ConvertPreset = r.ConvertPreset,
        SeedAfter = r.SeedAfter,
        SelectedFiles = r.SelectedFiles,
        SuperSeed = r.SuperSeed,
    };

    // ---- custom categories (category_def table) ----------------------------
    private List<object> ListCategoryDefs()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name, patterns, destination_dir, icon FROM category_def ORDER BY name";
            using var r = cmd.ExecuteReader();
            var list = new List<object>();
            while (r.Read())
                list.Add(new { name = r.GetString(0), patterns = r.GetString(1), destination_dir = r.GetString(2), icon = r.GetString(3) });
            return list;
        }
    }

    private void SaveCategoryDef(SaveCategoryRequest req)
    {
        _db.InTransaction<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO category_def(name, patterns, destination_dir, icon) VALUES(@n,@p,@d,@i)
                ON CONFLICT(name) DO UPDATE SET patterns=@p, destination_dir=@d, icon=@i
                """;
            cmd.Parameters.AddValue("@n", req.Name.Trim().ToLowerInvariant());
            cmd.Parameters.AddValue("@p", req.Patterns);
            cmd.Parameters.AddValue("@d", req.DestinationDir);
            cmd.Parameters.AddValue("@i", string.IsNullOrWhiteSpace(req.Icon) ? "folder" : req.Icon);
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    private void DeleteCategoryDef(string name)
    {
        _db.InTransaction<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM category_def WHERE name=@n";
            cmd.Parameters.AddValue("@n", name);
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    /// <summary>v1.8.0: IDM "change folder for category on last selected" — persists the new default dir.</summary>
    private int UpdateCategoryDir(string name, string dir)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE category_def SET destination_dir=@d WHERE name=@n";
            cmd.Parameters.AddValue("@d", dir);
            cmd.Parameters.AddValue("@n", name.Trim().ToLowerInvariant());
            return cmd.ExecuteNonQuery();
        }
    }
}
