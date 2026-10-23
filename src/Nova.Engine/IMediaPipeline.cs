using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// Contract implemented by Nova.Media: full run of an HLS/DASH task body.
/// The ENGINE keeps single-source-of-truth: pipeline writes the assembled container to
/// the task's ".novadl" temp path, reports bytes/segments, and calls FinalizeTempFile when done.
/// </summary>
public interface IMediaPipeline
{
    /// <summary>Runs to completion or throws MediaProcessException / NovaHttpException / OperationCanceledException.</summary>
    Task RunMediaTaskAsync(DownloadTaskContext ctx, CancellationToken ct);
}

/// <summary>Everything the media pipeline needs from the engine, without circular references.</summary>
public sealed class DownloadTaskContext
{
    public required Nova.Domain.DownloadTask Task { get; init; }
    public required HttpClient Http { get; init; }
    public required Func<RateLimiter?> GlobalLimiter { get; init; }
    public required RateLimiter? TaskLimiter { get; init; }
    /// <summary>Report received bytes (atomic accumulate).</summary>
    public required Action<long> OnBytes { get; init; }
    /// <summary>Report segment completion progress (done, total).</summary>
    public required Action<int, int> OnSegments { get; init; }
    /// <summary>The temp path the pipeline must write its container to.</summary>
    public required string TempOutputPath { get; init; }
    /// <summary>Move temp → final unique path; returns final path.</summary>
    public required Func<string> Finalize { get; init; }
}

/// <summary>Structured media failure (manifest unparseable, DRM detected, expired URL…).</summary>
public sealed class MediaProcessException : Exception
{
    public string Code { get; }
    public ErrorCategory Category { get; }
    public bool Retryable { get; }

    public MediaProcessException(string code, ErrorCategory category, string message, bool retryable = false)
        : base(message)
    {
        Code = code;
        Category = category;
        Retryable = retryable;
    }
}
