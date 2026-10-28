using System.Text.Json.Serialization;

namespace Nova.DownloadEngine;

/// <summary>
/// Task state machine:
/// Created -> Analyzing -> Queued -> Downloading -> Pausing -> Paused
///         -> Retrying -> Interrupted -> Verifying -> Completed / Failed / Cancelled
/// </summary>
public enum DownloadState
{
    Created,
    Analyzing,
    Queued,
    Downloading,
    Pausing,
    Paused,
    Retrying,
    Interrupted,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One byte-range of a download. End is inclusive; End &lt; 0 marks an open-ended
/// segment (unknown total size, streamed until EOF).
/// </summary>
public sealed class DownloadSegment
{
    public int Id { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }

    [JsonIgnore] public bool IsOpenEnded => End < 0;
    [JsonIgnore] public bool Completed => !IsOpenEnded && Downloaded >= End - Start + 1;
}

/// <summary>
/// A single download task with all persisted metadata.
/// </summary>
public sealed class DownloadTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public long TotalSize { get; set; } = -1;
    public int SegmentCount { get; set; } = 1;
    public DownloadState State { get; set; } = DownloadState.Created;
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<DownloadSegment> Segments { get; set; } = new();

    [JsonIgnore] public long Received => Segments.Sum(s => s.Downloaded);
    [JsonIgnore] public double SpeedBps { get; set; }
}
