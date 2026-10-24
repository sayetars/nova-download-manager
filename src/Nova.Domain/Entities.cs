using System.Text.Json.Serialization;

namespace Nova.Domain;

/// <summary>
/// A single download job. Immutable identity; mutable progress fields guarded by the engine.
/// This entity is the persistence AND wire representation (engine → Flutter, engine → extensions).
/// </summary>
public sealed class DownloadTask
{
    public long Id { get; set; }

    /// <summary>Stable client-generated GUID; used by IPC callers instead of DB id.</summary>
    public string TaskKey { get; set; } = Guid.NewGuid().ToString("N");

    public DownloadKind Kind { get; set; } = DownloadKind.File;

    public string Url { get; set; } = string.Empty;

    /// <summary>Effective URL after redirects (captured at analyze time).</summary>
    public string? EffectiveUrl { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string DestinationDir { get; set; } = string.Empty;

    public DownloadState State { get; set; } = DownloadState.Created;

    public TaskPriority Priority { get; set; } = TaskPriority.Normal;

    public string QueueName { get; set; } = "main";

    public string Category { get; set; } = "other";

    public long TotalBytes { get; set; } = -1;

    public long ReceivedBytes { get; set; }

    /// <summary>ETag (W/ prefix stripped) captured at analyze; guards resume validity.</summary>
    public string? Etag { get; set; }

    /// <summary>Last-Modified captured at analyze; secondary resume validator.</summary>
    public string? LastModified { get; set; }

    /// <summary>SHA-256 expected by the user (optional).</summary>
    public string? ExpectedSha256 { get; set; }

    public ValidationStatus Validation { get; set; } = ValidationStatus.NotChecked;

    public int SegmentCount { get; set; } = 1;

    public int MaxConnections { get; set; } = 4;

    /// <summary>Per-task speed cap in bytes/sec (0 = uncapped).</summary>
    public long SpeedCapBytes { get; set; }

    public int RetryCount { get; set; }

    public int MaxRetries { get; set; } = 5;

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public ErrorCategory? ErrorCategory { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>For HLS/DASH: how many segments are done (progress within the task).</summary>
    public int SegmentsDone { get; set; }

    /// <summary>For HLS/DASH: total segments (known after manifest parse).</summary>
    public int SegmentsTotal { get; set; }

    /// <summary>Optional MIME type from probe (e.g. application/vnd.apple.mpegurl).</summary>
    public string? MimeType { get; set; }

    /// <summary>Referer / origin page for media tasks (privacy: kept only for retries, never logged).</summary>
    public string? Referer { get; set; }

    /// <summary>Selection payload for media (e.g. chosen variant URL / representation id).</summary>
    public string? MediaSelection { get; set; }

    /// <summary>True when the user removed the task but chose to keep the file.</summary>
    public bool FileKept { get; set; }

    public bool IsSpeedCapped { get; set; }

    /// <summary>Fallback URLs tried in order when the primary fails permanently (IDM mirrors).</summary>
    public string MirrorUrls { get; set; } = string.Empty;   // newline-separated

    /// <summary>Epoch-ms before which the scheduler must not start this task (0 = now).</summary>
    public long StartAtMs { get; set; }

    /// <summary>Per-task post-completion action (empty | exit | shutdown | hibernate | sleep | logoff).</summary>
    public string PostAction { get; set; } = string.Empty;

    /// <summary>Cookie header for this task ("k=v; k2=v2") — applied to every HTTP request of the task.
    /// Opt-in: never auto-captured from browsers without the user's explicit consent.</summary>
    public string Cookies { get; set; } = string.Empty;

    /// <summary>Extra HTTP headers, JSON object {"Name": "value"} — applied after defaults.</summary>
    public string ExtraHeaders { get; set; } = string.Empty;

    /// <summary>Sequential (in-order) segment allocation — enables partial media preview while downloading.</summary>
    public bool Sequential { get; set; }

    /// <summary>Algo-qualified whole-file hash ("sha256:…", "sha1:…", "md5:…", "crc32:…").
    /// When set, takes precedence over ExpectedSha256 (kept for backward compatibility).</summary>
    public string? ExpectedHash { get; set; }

    /// <summary>Metalink piece hashes JSON: {"algo":"sha1","size":262144,"hashes":["hex",…]} — enables piece-level repair.</summary>
    public string? PieceHashes { get; set; }

    /// <summary>Post-download conversion preset ("" | mp3 | remux) — requires ffmpeg on PATH.</summary>
    public string ConvertPreset { get; set; } = string.Empty;

    /// <summary>BitTorrent info-hash (hex, 40 chars) once the torrent metadata is known (v1.4.0).</summary>
    public string BtInfoHash { get; set; } = string.Empty;

    /// <summary>BitTorrent: keep serving (seeding) after completion (v1.4.0).</summary>
    public bool SeedAfter { get; set; }

    [JsonIgnore] public bool HasSpeedCap => SpeedCapBytes > 0;

    [JsonIgnore] public IReadOnlyList<string> Mirrors =>
        MirrorUrls.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [JsonIgnore] public bool IsRunning =>
        State is DownloadState.Analyzing or DownloadState.Downloading
            or DownloadState.Pausing or DownloadState.Retrying or DownloadState.Verifying;

    [JsonIgnore] public bool IsTerminal =>
        State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled;

    /// <summary>Effective completed-file path. During download the data lives at <c>.novadl</c> temp path.</summary>
    [JsonIgnore] public string FinalPath =>
        Path.GetFullPath(Path.Combine(string.IsNullOrEmpty(DestinationDir) ? "." : DestinationDir, FileName));
}

/// <summary>One byte-range slice of a multipart task. Persisted so resume is crash-safe.</summary>
public sealed class DownloadSegment
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public int Index { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public long Received { get; set; }

    [JsonIgnore] public long Length => End - Start + 1;
    [JsonIgnore] public bool IsComplete => Received >= Length;
}

public sealed class DownloadQueueInfo
{
    public string Name { get; set; } = "main";
    public QueueKind Kind { get; set; } = QueueKind.Main;
    public int MaxConcurrent { get; set; }
    public long SpeedCapBytes { get; set; }
    public string DestinationDir { get; set; } = string.Empty;
    /// <summary>Per-queue schedule JSON (QueueScheduleRule[]); empty = always allowed.</summary>
    public string ScheduleJson { get; set; } = string.Empty;
    public int ActiveCount { get; set; }
    public int WaitingCount { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>Timer window when the scheduler is allowed to run (e.g. nights, weekends).</summary>
public sealed class SchedulerRule
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>days of week 0=Sunday..6=Saturday; empty = every day.</summary>
    public string DaysOfWeek { get; set; } = string.Empty;
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "23:59";
    public bool Enabled { get; set; } = true;
    /// <summary>Apply speed cap while active.</summary>
    public long SpeedCapBytes { get; set; }
}

/// <summary>Per-queue start/stop schedule entry (IDM parity: queue scheduling).</summary>
public sealed class QueueScheduleRule
{
    /// <summary>"run" = queue may only run inside the window; "pause" = queue must not run inside it.</summary>
    public string Action { get; set; } = "pause";
    public string DaysOfWeek { get; set; } = string.Empty;
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "23:59";
    public bool Enabled { get; set; } = true;
}

/// <summary>Periodic synchronization rule (IDM parity: sync queues) — re-downloads changed URLs.</summary>
public sealed class SyncRule
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>One URL per line.</summary>
    public string Urls { get; set; } = string.Empty;
    public string DestinationDir { get; set; } = string.Empty;
    public string Category { get; set; } = "other";
    /// <summary>Minimum minutes between automatic runs.</summary>
    public int IntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    /// <summary>Epoch-ms of the last completed run (0 = never).</summary>
    public long LastRunMs { get; set; }
}

/// <summary>Result of one synchronization run.</summary>
public sealed record SyncRunResult(long RuleId, int Checked, int Added, int Unchanged, int Failed);

/// <summary>RSS/Atom feed subscription — new enclosures/links matching the filters become downloads.</summary>
public sealed class FeedRule
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    /// <summary>Comma-separated case-insensitive title/URL substrings; empty = accept all items.</summary>
    public string Filters { get; set; } = string.Empty;
    public string DestinationDir { get; set; } = string.Empty;
    public string Category { get; set; } = "other";
    public int IntervalMinutes { get; set; } = 30;
    public bool Enabled { get; set; } = true;
    /// <summary>Epoch-ms of the last completed check (0 = never).</summary>
    public long LastCheckMs { get; set; }
    /// <summary>Auto-start matched downloads (vs. created in Created state for review).</summary>
    public bool AutoStart { get; set; } = true;
    /// <summary>Max items added per check (0 = unlimited).</summary>
    public int MaxItems { get; set; }
}

/// <summary>One new download discovered in a feed (result of a feed check).</summary>
public sealed record FeedItem(string ItemId, string Title, string Url, long PublishedMs);

/// <summary>Result of one feed check run.</summary>
public sealed record FeedRunResult(long RuleId, int Checked, int Added, int Seen, int Failed);
