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
}

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
    public string EngineVersion { get; set; } = "1.0.0";
    public int ActiveTasks { get; set; }
    public int QueuedTasks { get; set; }
    public long GlobalSpeed { get; set; }
    public long TotalReceivedBytes { get; set; }
    public int UptimeSeconds { get; set; }
    public string DataDir { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; } = Protocol.Version;
    public string Platform { get; set; } = "unknown";
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
