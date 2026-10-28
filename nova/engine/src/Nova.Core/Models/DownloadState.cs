namespace Nova.Core.Models;

/// <summary>
/// Full download lifecycle state machine.
/// Legal transitions are declared once here; every other module must consult
/// <see cref="CanTransition"/> before mutating state.
/// </summary>
public enum DownloadState
{
    /// <summary>Task created, not yet analyzed.</summary>
    Created,
    /// <summary>Probing URL: HEAD/Range probe, filename detection, stream parsing.</summary>
    Analyzing,
    /// <summary>Analyzed and waiting for a free slot in its queue.</summary>
    Queued,
    /// <summary>Actively transferring bytes.</summary>
    Downloading,
    /// <summary>User requested pause; finishing in-flight segments.</summary>
    Pausing,
    /// <summary>Paused by user; .novapart kept on disk.</summary>
    Paused,
    /// <summary>Error occurred; automatic retry with backoff scheduled.</summary>
    Retrying,
    /// <summary>Retries exhausted / fatal error.</summary>
    Interrupted,
    /// <summary>All bytes on disk; hash or size verification in progress.</summary>
    Verifying,
    /// <summary>Finished and verified.</summary>
    Completed,
    /// <summary>User cancelled; partial file policy applied.</summary>
    Cancelled,
    /// <summary>Unrecoverable failure.</summary>
    Failed
}

public static class DownloadStateMachine
{
    private static readonly Dictionary<DownloadState, DownloadState[]> Transitions = new()
    {
        [DownloadState.Created]      = [DownloadState.Analyzing, DownloadState.Cancelled],
        [DownloadState.Analyzing]    = [DownloadState.Queued, DownloadState.Downloading, DownloadState.Paused,
                                         DownloadState.Interrupted, DownloadState.Failed, DownloadState.Cancelled, DownloadState.Completed],
        [DownloadState.Queued]       = [DownloadState.Downloading, DownloadState.Paused, DownloadState.Cancelled],
        [DownloadState.Downloading]  = [DownloadState.Pausing, DownloadState.Paused, DownloadState.Retrying,
                                         DownloadState.Interrupted, DownloadState.Verifying, DownloadState.Completed,
                                         DownloadState.Failed, DownloadState.Queued],
        [DownloadState.Pausing]      = [DownloadState.Paused, DownloadState.Completed, DownloadState.Failed],
        [DownloadState.Paused]       = [DownloadState.Queued, DownloadState.Downloading, DownloadState.Cancelled],
        [DownloadState.Retrying]     = [DownloadState.Downloading, DownloadState.Interrupted, DownloadState.Cancelled],
        [DownloadState.Interrupted]  = [DownloadState.Queued, DownloadState.Retrying, DownloadState.Cancelled],
        [DownloadState.Verifying]    = [DownloadState.Completed, DownloadState.Failed],
        [DownloadState.Completed]    = [DownloadState.Downloading], // re-download / refresh
        [DownloadState.Cancelled]    = [DownloadState.Queued],      // revive
        [DownloadState.Failed]       = [DownloadState.Queued, DownloadState.Retrying, DownloadState.Cancelled]
    };

    public static bool CanTransition(DownloadState from, DownloadState to)
        => Transitions.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static bool IsTerminal(DownloadState s)
        => s is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled;
}

/// <summary>
/// Reason a task failed — used both for retry policy decisions and for the UI
/// error strings (localized client-side).
/// </summary>
public enum FailureReason
{
    None = 0,
    NetworkUnavailable,
    DnsFailure,
    ConnectionRefused,
    TlsError,
    Forbidden = 403,
    NotFound = 404,
    ServerError = 500,
    RangeNotSupported,
    FileTooLarge,
    DiskFull,
    AccessDenied,
    ServerChangedFile,   // ETag/Last-Modified mismatch on resume attempt
    ChecksumMismatch,
    InvalidUrl,
    Timeout,
    StreamExpired,       // HLS/DASH playlist link expired
    DecryptionFailed,    // DRM detected or key fetch failure
    ProtocolNotSupported,
    Unknown
}
