namespace Nova.Domain;

/// <summary>Stable lifecycle states of a download task. UI must render these — never guess them.</summary>
public enum DownloadState
{
    Created = 0,
    Analyzing = 1,
    Queued = 2,
    Downloading = 3,
    Pausing = 4,
    Paused = 5,
    Retrying = 6,
    Interrupted = 7,
    Verifying = 8,
    Completed = 9,
    Failed = 10,
    Cancelled = 11,
}

/// <summary>Which strategy the engine uses for a task.</summary>
public enum DownloadKind
{
    /// <summary>Plain HTTP(S) file (single or multi-part).</summary>
    File = 0,
    /// <summary>HLS playlist; engine downloads segments and assembles container.</summary>
    Hls = 1,
    /// <summary>DASH manifest; engine downloads a representation's segments.</summary>
    Dash = 2,
}

public enum TaskPriority
{
    Lowest = -2,
    Low = -1,
    Normal = 0,
    High = 1,
    Highest = 2,
}

public enum QueueKind
{
    Main = 0,
    Secondary = 1,
    Scheduler = 2,
}

public enum RetryClass
{
    None = 0,
    /// <summary>Retry safe: network timeouts, 408/429/5xx, connection resets.</summary>
    Transient = 1,
    /// <summary>Retry will not help until user changes something (401/403 auth, 404 gone).</summary>
    Permanent = 2,
}

public enum ValidationStatus
{
    NotChecked = 0,
    Pending = 1,
    Passed = 2,
    Failed = 3,
    SkippedNoExpected = 4,
}

/// <summary>Structured error category — mapped 1:1 to UI error chips; never expose raw exceptions to users.</summary>
public enum ErrorCategory
{
    Unknown = 0,
    Network = 1,
    Dns = 2,
    Tls = 3,
    Http = 4,
    Auth = 5,
    NotFound = 6,
    Forbidden = 7,
    RateLimited = 8,
    Server = 9,
    DiskFull = 10,
    FileIo = 11,
    AccessDenied = 12,
    ValidatorChanged = 13,
    HashMismatch = 14,
    UrlExpired = 15,
    Database = 16,
    Protocol = 17,
    Media = 18,
    Cancelled = 19,
    InsufficientInfo = 20,
}
