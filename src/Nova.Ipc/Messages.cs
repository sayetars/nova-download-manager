namespace Nova.Ipc;

// ---- Requests (client → engine) -------------------------------------------

public sealed class HelloRequest
{
    public string Client { get; set; } = "unknown";
    public int ProtocolVersion { get; set; } = Protocol.Version;
    public string? ClientVersion { get; set; }
}

public sealed class AddDownloadRequest
{
    public string Url { get; set; } = string.Empty;
    public string? DestinationDir { get; set; }
    public string? FileName { get; set; }
    public string? QueueName { get; set; }
    public string? Category { get; set; }
    public string? Referer { get; set; }
    public string? ExpectedSha256 { get; set; }
    public int? MaxConnections { get; set; }
    public int? Priority { get; set; }
    /// <summary>File | hls | dash — engine re-detects when omitted.</summary>
    public string? Kind { get; set; }
    /// <summary>Opaque media selection (variant URL / representation id / audio id).</summary>
    public string? MediaSelection { get; set; }
    /// <summary>true → auto-start immediately (default) vs. only queue.</summary>
    public bool AutoStart { get; set; } = true;
    /// <summary>IDM parity: fallback URLs tried in order on permanent failure.</summary>
    public List<string>? Mirrors { get; set; }
    /// <summary>IDM parity: per-task credentials (basic auth).</summary>
    public string? AuthUser { get; set; }
    public string? AuthPassword { get; set; }
    /// <summary>Epoch-ms: start no earlier than this time (download later).</summary>
    public long StartAtMs { get; set; }
    /// <summary>Per-task post-completion action: "" | exit | shutdown | hibernate | sleep | logoff.</summary>
    public string? PostAction { get; set; }
    /// <summary>v1.3.0: cookie header ("k=v; k2=v2") — opt-in, applied to every request of this task.</summary>
    public string? Cookies { get; set; }
    /// <summary>v1.3.0: extra HTTP headers JSON ({"Name":"value"}).</summary>
    public string? ExtraHeaders { get; set; }
    /// <summary>v1.3.0: sequential (in-order) segment allocation — partial media preview.</summary>
    public bool Sequential { get; set; }
    /// <summary>v1.3.0: algo-qualified expected hash ("sha256:hex" | "sha1:hex" | "md5:hex" | "crc32:hex").</summary>
    public string? ExpectedHash { get; set; }
    /// <summary>v1.3.0: Metalink piece hashes JSON (repair-capable integrity).</summary>
    public string? PieceHashes { get; set; }
    /// <summary>v1.3.0: post-download conversion preset ("" | mp3 | remux).</summary>
    public string? ConvertPreset { get; set; }
    /// <summary>v1.4.0: BitTorrent — keep serving (seeding) after completion.</summary>
    public bool SeedAfter { get; set; }
    /// <summary>v1.4.1: BitTorrent multi-file selection CSV ("0,2,5"; null/empty = all).</summary>
    public string? SelectedFiles { get; set; }
    /// <summary>v1.4.1: BitTorrent — super-seeding (BEP-16) for this task.</summary>
    public bool SuperSeed { get; set; }
}

public sealed class BatchAddRequest
{
    public List<AddDownloadRequest> Items { get; set; } = new();
}

/// <summary>Update editable task properties (IDM parity: properties editor).</summary>
public sealed class UpdateTaskRequest
{
    public string TaskKey { get; set; } = string.Empty;
    public string? QueueName { get; set; }
    public int? Priority { get; set; }
    public string? Category { get; set; }
    public string? Referer { get; set; }
    public int? MaxConnections { get; set; }
    public long? SpeedCapBytes { get; set; }
    public long? StartAtMs { get; set; }
    public string? PostAction { get; set; }
    public List<string>? Mirrors { get; set; }
    /// <summary>v1.3.0: replace cookie header (empty string clears).</summary>
    public string? Cookies { get; set; }
    /// <summary>v1.3.0: replace extra headers JSON (empty string clears).</summary>
    public string? ExtraHeaders { get; set; }
    /// <summary>v1.3.0: toggle sequential allocation.</summary>
    public bool? Sequential { get; set; }
    /// <summary>v1.3.0: replace expected hash (empty string clears).</summary>
    public string? ExpectedHash { get; set; }
    /// <summary>v1.3.0: replace conversion preset ("" | mp3 | remux).</summary>
    public string? ConvertPreset { get; set; }
}

/// <summary>FTP directory browser (IDM parity: FTP explorer).</summary>
public sealed class FtpListRequest
{
    public string Url { get; set; } = string.Empty;
    public string? AuthUser { get; set; }
    public string? AuthPassword { get; set; }
}

/// <summary>ZIP preview: local (completed task) or remote via central-directory Range fetch.</summary>
public sealed class ZipListRequest
{
    /// <summary>Task key of a completed local task — preferred.</summary>
    public string? TaskKey { get; set; }
    /// <summary>Remote archive URL (used when task not completed).</summary>
    public string? Url { get; set; }
    public string? Referer { get; set; }
}

public sealed class ZipExtractRequest
{
    public string TaskKey { get; set; } = string.Empty;
    public List<string> Entries { get; set; } = new();
    public string DestinationDir { get; set; } = string.Empty;
}

/// <summary>Site Grabber control (IDM parity).</summary>
public sealed class GrabberStartRequest
{
    public string RootUrl { get; set; } = string.Empty;
    public int MaxDepth { get; set; } = 3;
    public string IncludeFilter { get; set; } = "";
    public string ExcludeFilter { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public bool FollowLinks { get; set; } = true;
    public bool Images { get; set; } = true;
    public bool Css { get; set; } = true;
    public bool Scripts { get; set; }
    public bool Video { get; set; }
    public bool RewriteForOffline { get; set; } = true;
}

/// <summary>Login vault (IDM parity: site logins).</summary>
public sealed class SaveLoginRequest
{
    public string Host { get; set; } = string.Empty;
    public string Realm { get; set; } = "";
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Scheme { get; set; } = "basic";
}

public sealed class DeleteLoginRequest
{
    public long Id { get; set; }
}

/// <summary>Custom categories (IDM parity): name, patterns, save dir.</summary>
public sealed class SaveCategoryRequest
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Comma-separated extensions and :keyword URL filters, e.g. "mp3,flac,:bandcamp".</summary>
    public string Patterns { get; set; } = "";
    public string DestinationDir { get; set; } = "";
    public string Icon { get; set; } = "folder";
}

public sealed class DeleteCategoryRequest
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>v1.8.0 (IDM "Change folder for category on last selected" parity): persist a new default dir for a category.</summary>
public sealed class UpdateCategoryDirRequest
{
    public string Name { get; set; } = string.Empty;
    public string Dir { get; set; } = string.Empty;
}

/// <summary>v1.8.0 (IDM Proxy tab "Get System" parity): result of OS proxy detection.</summary>
public sealed class DetectSystemProxyRequest { }

/// <summary>v1.8.0: load a PAC script and resolve one test URL against it.</summary>
public sealed class TestPacRequest
{
    public string Url { get; set; } = string.Empty;
    public string TestUrl { get; set; } = string.Empty;
}

/// <summary>Import/export the task list (IDM parity).</summary>
public sealed class ImportTasksRequest
{
    /// <summary>JSON as produced by export_tasks.</summary>
    public string Json { get; set; } = string.Empty;
    public bool AutoStart { get; set; }
}

public sealed class CheckUpdateRequest
{
    public string UpdateUrl { get; set; } = "";
}


public sealed class ListTasksRequest
{
    /// <summary>all | active | queued | completed | failed | paused | media</summary>
    public string Filter { get; set; } = "all";
    public string? Search { get; set; }
    public int Limit { get; set; } = 500;
}

public sealed class TaskKeyRequest { public string TaskKey { get; set; } = string.Empty; }

public sealed class RemoveTaskRequest
{
    public string TaskKey { get; set; } = string.Empty;
    public bool DeleteFile { get; set; }
    public bool DeleteAlsoFromHistory { get; set; }
}

public sealed class MoveTaskRequest
{
    public string TaskKey { get; set; } = string.Empty;
    public string? QueueName { get; set; }
    public int? Priority { get; set; }
    public string? Category { get; set; }
}

public sealed class SetDestinationRequest
{
    public string TaskKey { get; set; } = string.Empty;
    public string DestinationDir { get; set; } = string.Empty;
}

public sealed class SetSettingsRequest
{
    public Dictionary<string, string> Values { get; set; } = new();
}

public sealed class PurgeHistoryRequest
{
    public int OlderThanDays { get; set; } = 30;
    public bool OnlyCompleted { get; set; } = true;
}

public sealed class SaveQueueRequest
{
    public string Name { get; set; } = string.Empty;
    public string? DestinationDir { get; set; }
    public int? MaxConcurrent { get; set; }
    public long? SpeedCapBytes { get; set; }
    /// <summary>Per-queue start/stop schedule JSON (QueueScheduleRule[]); null = keep.</summary>
    public string? ScheduleJson { get; set; }
}

public sealed class SaveSyncRuleRequest
{
    public long? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Urls { get; set; } = string.Empty;
    public string DestinationDir { get; set; } = string.Empty;
    public string Category { get; set; } = "other";
    public int IntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; } = true;
}

public sealed class RunSyncRequest
{
    public long Id { get; set; }
    public bool Force { get; set; } = true;
}

public sealed class DeleteSyncRuleRequest
{
    public long Id { get; set; }
}

/// <summary>RSS/Atom feed subscriptions (v1.3.0).</summary>
public sealed class SaveFeedRuleRequest
{
    public long? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    /// <summary>Comma-separated title/URL substrings; empty = accept all.</summary>
    public string Filters { get; set; } = "";
    public string DestinationDir { get; set; } = "";
    public string Category { get; set; } = "other";
    public int IntervalMinutes { get; set; } = 30;
    public bool Enabled { get; set; } = true;
    public bool AutoStart { get; set; } = true;
    public int MaxItems { get; set; }
}

public sealed class RunFeedRequest
{
    public long Id { get; set; }
}

public sealed class DeleteFeedRuleRequest
{
    public long Id { get; set; }
}

/// <summary>Import a Netscape cookies.txt file (v1.3.0); returns host→cookie-header lines.</summary>
public sealed class ImportCookiesRequest
{
    public string Content { get; set; } = string.Empty;
}

/// <summary>Request a post-completion action for one task (v1.3.0: extract / convert).</summary>
public sealed class TaskPostProcessRequest
{
    public string TaskKey { get; set; } = string.Empty;
    /// <summary>extract | mp3 | remux</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>Optional destination for extract.</summary>
    public string? DestinationDir { get; set; }
}

public sealed class ImportIdmRequest
{
    public string Content { get; set; } = string.Empty;
    public string FileName { get; set; } = "";
    public bool AutoStart { get; set; }
}

// ---- v1.5.0 G-track ---------------------------------------------------------

/// <summary>G28: ask the engine whether a yt-dlp binary is available and usable.</summary>
public sealed class YtDlpStatusRequest { }

/// <summary>G28: analyze a page URL into a format list (quality panel).</summary>
public sealed class YtDlpAnalyzeRequest
{
    public string Url { get; set; } = string.Empty;
}

/// <summary>G28: resolve a chosen format and create a normal engine download task.</summary>
public sealed class YtDlpDownloadRequest
{
    public string Url { get; set; } = string.Empty;
    public string FormatId { get; set; } = string.Empty;
    public string? DestinationDir { get; set; }
    public string? FileName { get; set; }
    public string? Referer { get; set; }
    public string? Cookies { get; set; }
    /// <summary>Title from the analyze step (used to name progressive formats).</summary>
    public string? Title { get; set; }
    /// <summary>Extension from the analyze step (used when the URL has none).</summary>
    public string? Ext { get; set; }
    public bool AutoStart { get; set; } = true;
    /// <summary>v1.5.1: run yt-dlp itself (native mode) — required for formats needing merge
    /// or site-specific fragment handling; the task's bytes come from yt-dlp.</summary>
    public bool Native { get; set; }
    /// <summary>v1.5.1: audio format id to merge with a video-only FormatId (native mode).</summary>
    public string? AudioFormatId { get; set; }
    /// <summary>v1.5.1: total bytes of the chosen format(s) when known at analyze time.</summary>
    public long? TotalBytes { get; set; }
}

/// <summary>G17: start (lazily) the preview server and get the URL for one task.</summary>
public sealed class PreviewStartRequest
{
    public string TaskKey { get; set; } = string.Empty;
}

public sealed class PreviewStopRequest { }

/// <summary>G25: save/replace one client-certificate binding.</summary>
public sealed class SaveClientCertRequest
{
    public string HostPattern { get; set; } = string.Empty;
    /// <summary>pfx | pem | store</summary>
    public string Source { get; set; } = "pfx";
    public string CertPath { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public string StoreName { get; set; } = "My";
    public string Thumbprint { get; set; } = "";
    /// <summary>PFX passphrase — stored server-side only, never echoed back.</summary>
    public string Passphrase { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class DeleteClientCertRequest
{
    public long Id { get; set; }
}

/// <summary>G26: import an armored PGP public key into the keyring.</summary>
public sealed class SavePgpKeyRequest
{
    public string Label { get; set; } = string.Empty;
    public string ArmoredKey { get; set; } = string.Empty;
}

public sealed class DeletePgpKeyRequest
{
    public long Id { get; set; }
}

public sealed class ResetServerProfilesRequest { }

public sealed class SaveScheduleRequest
{
    public long? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DaysOfWeek { get; set; } = string.Empty;
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "23:59";
    public bool Enabled { get; set; } = true;
    public long SpeedCapBytes { get; set; }
}

public sealed class EngineStatusResponse
{
    public string EngineVersion { get; set; } = Nova.Domain.VersionInfo.Version;
    public string Channel { get; set; } = Nova.Domain.VersionInfo.Channel;
    public int DbSchemaVersion { get; set; }
    public string MinEngineForExtension { get; set; } = Nova.Domain.VersionInfo.MinEngineForExtension;
    public int ActiveTasks { get; set; }
    public int QueuedTasks { get; set; }
    public long GlobalSpeed { get; set; }
    public long TotalReceivedBytes { get; set; }
    public int UptimeSeconds { get; set; }
    public string DataDir { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; } = Protocol.Version;
    public string Platform { get; set; } = "unknown";
    public QuotaStateDto? Quota { get; set; }
}

/// <summary>Wire shape for byte-quota windows (quota_state + engine_status.quota).</summary>
public sealed class QuotaStateDto
{
    public bool Enabled { get; set; }
    public long CapBytes { get; set; }
    public long WindowMinutes { get; set; }
    public long UsedBytes { get; set; }
    public bool Paused { get; set; }
    public long ResumeInMs { get; set; }
}

public sealed class AddDownloadResponse
{
    public string TaskKey { get; set; } = string.Empty;
    public long TaskId { get; set; }
    public bool Duplicate { get; set; }
    public string? ExistingTaskKey { get; set; }
}

public sealed class ListTasksResponse
{
    public List<object> Tasks { get; set; } = new();
}

public sealed class TaskUpdatedEvent
{
    public string TaskKey { get; set; } = string.Empty;
    public long TaskId { get; set; }
    public string State { get; set; } = string.Empty;
    public string Kind { get; set; } = "file";
    public string FileName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? EffectiveUrl { get; set; }
    public string DestinationDir { get; set; } = string.Empty;
    public string QueueName { get; set; } = "main";
    public string Category { get; set; } = "other";
    public long TotalBytes { get; set; } = -1;
    public long ReceivedBytes { get; set; }
    public int SegmentCount { get; set; }
    public int SegmentsDone { get; set; }
    public int SegmentsTotal { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCategory { get; set; }
    public string? Validation { get; set; }
    public int Priority { get; set; }
    public long CreatedAtMs { get; set; }
    public long? StartedAtMs { get; set; }
    public long? CompletedAtMs { get; set; }
    public int RetryCount { get; set; }
    public string? FinalPath { get; set; }
}

public sealed class TaskProgressEvent
{
    public string TaskKey { get; set; } = string.Empty;
    public long ReceivedBytes { get; set; }
    public long TotalBytes { get; set; } = -1;
    public long Speed { get; set; }
    public long EtaSeconds { get; set; } = -1;
    public int SegmentsDone { get; set; }
    public int SegmentsTotal { get; set; }
    public string State { get; set; } = string.Empty;
}

public sealed class EngineStatusChangedEvent
{
    public int ActiveTasks { get; set; }
    public int QueuedTasks { get; set; }
    public long GlobalSpeed { get; set; }
    public string SchedulerState { get; set; } = "idle";
}
