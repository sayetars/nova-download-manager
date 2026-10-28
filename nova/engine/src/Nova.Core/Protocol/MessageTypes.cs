namespace Nova.Core.Protocol;

/// <summary>
/// Every command/response/event type crossing IPC. One enum, one place —
/// extensions speak a subset via the native host which translates to the
/// same envelope types.
/// </summary>
public static class MessageTypes
{
    // --- lifecycle ---
    public const string Hello = "hello";                 // version negotiation
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Shutdown = "shutdown";

    // --- task CRUD ---
    public const string AddUrl = "task.addUrl";           // payload: AddUrlRequest
    public const string AddBatch = "task.addBatch";        // payload: AddBatchRequest
    public const string AddVideo = "task.addVideo";        // payload: VideoCandidate + track selection
    public const string AddMetalink = "task.addMetalink";
    public const string AddTorrent = "task.addTorrent";
    public const string List = "task.list";
    public const string Get = "task.get";
    public const string Pause = "task.pause";
    public const string Resume = "task.resume";
    public const string Cancel = "task.cancel";
    public const string Retry = "task.retry";
    public const string Remove = "task.remove";
    public const string MoveQueue = "task.moveQueue";
    public const string SetPriority = "task.setPriority";
    public const string OpenFile = "task.openFile";
    public const string PreviewStream = "task.previewStream"; // sequential preview of incomplete video

    // --- video detection (from extensions) ---
    public const string VideoDetected = "video.detected";
    public const string ResolveTracks = "video.resolveTracks"; // ask engine to parse m3u8/mpd

    // --- speed / limits ---
    public const string SetGlobalSpeedLimit = "speed.setGlobal";
    public const string GetGlobalSpeedLimit = "speed.getGlobal";
    public const string SetTaskSpeedLimit = "speed.setTask";

    // --- queue / scheduler ---
    public const string QueueList = "queue.list";
    public const string QueueStartAll = "queue.startAll";
    public const string QueuePauseAll = "queue.pauseAll";
    public const string ScheduleList = "schedule.list";
    public const string ScheduleUpsert = "schedule.upsert";
    public const string ScheduleDelete = "schedule.delete";

    // --- settings / credentials / proxies ---
    public const string SettingsGet = "settings.get";
    public const string SettingsSet = "settings.set";
    public const string CredentialPut = "cred.put";
    public const string CredentialDelete = "cred.delete";
    public const string CredentialList = "cred.list";
    public const string ProxyTest = "proxy.test";

    // --- site grabber ---
    public const string GrabSite = "grabber.start";
    public const string GrabberProgress = "grabber.progress";

    // --- debrid / containers ---
    public const string DebridUnrestrict = "debrid.unrestrict";
    public const string ContainerOpen = "container.open"; // DLC/RSDF/CCF/Metalink file

    // --- events engine -> UI ---
    public const string EvTaskAdded = "ev.task.added";
    public const string EvProgress = "ev.task.progress";
    public const string EvStateChanged = "ev.task.state";
    public const string EvRemoved = "ev.task.removed";
    public const string EvGlobalSpeed = "ev.speed.global";
    public const string EvVideoDetected = "ev.video.detected";
    public const string EvShutdownAfterComplete = "ev.system.shutdown";
    public const string EvGrabberFile = "ev.grabber.file";
    public const string EvToast = "ev.ui.toast";

    // --- responses ---
    public const string Ok = "ok";
    public const string Error = "error";
}

// ---------------------------------------------------------------------------
// Request/response payload records
// ---------------------------------------------------------------------------

public sealed record AddUrlRequest(
    string Url,
    string? FileName = null,
    string? SaveDirectory = null,
    string Category = "general",
    string Queue = "main",
    int Segments = 8,
    string? Username = null,
    string? Password = null,
    string? ProxyJson = null,
    Dictionary<string, string>? Headers = null,
    string? ScheduledStart = null,
    bool SequentialPreview = false,
    int Priority = 0);

public sealed record AddBatchRequest(List<string> Urls, string? SaveDirectory = null, string Category = "general");

public sealed record AddVideoRequest(
    string PageUrl,
    string MediaUrl,
    string Title,
    string Protocol,            // "hls" | "dash" | "direct"
    string? VideoTrackId = null,
    string? AudioTrackId = null,
    string? SaveDirectory = null,
    Dictionary<string, string>? Headers = null,
    bool IsLive = false,
    string? DetectMethod = null);

public sealed record AddTorrentRequest(string FilePathOrUrl, string? SaveDirectory = null);

public sealed record AddMetalinkRequest(string FilePathOrUrl, string? SaveDirectory = null);

public sealed record GrabSiteRequest(
    string StartUrl,
    int MaxDepth,
    string? IncludeFilter,       // "*.jpg;*.png"
    string? ExcludeFilter,
    bool FollowOtherDomains = false,
    string? SaveDirectory = null);

public sealed record DebridUnrestrictRequest(string Url, string Provider, string ApiKey);

public sealed record ScheduleUpsertRequest(
    string? Id,
    string Name,
    string Kind,                 // "start" | "pause" | "limit" | "shutdown"
    string TimeExpr,             // ISO local time or Jalali "1404/08/12 21:30"
    string? DaysOfWeek,          // "mon,tue,wed"
    long? SpeedLimitBytes = null,
    string? QueueName = null,
    bool Enabled = true,
    string? Calendar = "gregorian"); // "gregorian" | "jalali"

public sealed record SettingsDocument(
    string DefaultSaveDirectory = "",
    int MaxConcurrentTasks = 3,
    int MaxSegmentsPerTask = 16,
    int MaxGlobalSegments = 64,
    long SpeedLimitBytesPerSec = 0,          // 0 = unlimited
    bool ClipboardMonitoring = true,
    bool AutoStartOnAdd = true,
    bool AntivirusScanOnComplete = false,    // Windows Defender via CLI
    string AntivirusCommand = "",
    bool ShowDownloadBasket = true,
    string Theme = "system",                 // system | light | dark
    string Language = "en",                  // en | fa
    bool SequentialPreviewEnabled = true,
    string DuplicatePolicy = "rename",       // rename | overwrite | skip | ask
    long MinDiskFreeBytes = 512L * 1024 * 1024,
    int ProtocolHttp3 = 1,                   // 0 off | 1 auto | 2 force
    int TcpConnectTimeoutSec = 30,
    int IdleTimeoutSec = 30,
    int RetryBackoffBaseSec = 2,
    int MaxRetries = 10,
    bool YtDlpEnabled = true,
    string YtDlpPath = "",
    string FfmpegPath = "",
    bool SniffHls = true,
    bool SniffDash = true,
    bool SniffDirectMedia = true,
    bool SniffDomVideo = true);
