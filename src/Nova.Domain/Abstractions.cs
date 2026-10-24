using System.Text.Json.Serialization;

namespace Nova.Domain;

/// <summary>Clock abstraction so tests can fast-forward time (scheduler, backoff).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    long UnixMs { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long UnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>Result of probing a URL (HEAD / ranged GET) before download starts.</summary>
public sealed class HttpProbeResult
{
    public required string FinalUrl { get; init; }
    public long ContentLength { get; init; } = -1;
    public bool AcceptRanges { get; init; }
    public string? Etag { get; init; }
    public string? LastModified { get; init; }
    public string? ContentType { get; init; }
    public string? SuggestedFileName { get; init; }
    public int StatusCode { get; init; }
    public Dictionary<string, string> ExtraHeaders { get; init; } = new();
    /// <summary>Raw Link headers (Metalink/HTTP RFC 6249 mirrors), v1.3.0.</summary>
    public List<string> LinkHeaders { get; init; } = new();
    /// <summary>Raw Digest header (RFC 3230 instance digests), v1.3.0.</summary>
    public string? DigestHeader { get; init; }

    [JsonIgnore] public bool HasKnownSize => ContentLength >= 0;
}

public interface IDownloadTaskRepository
{
    long Insert(DownloadTask task);
    void Update(DownloadTask task);
    void UpdateProgress(long taskId, DownloadState state, long received, int segmentsDone, int segmentsTotal);
    void UpdateState(long taskId, DownloadState state, string? errorCode, string? errorMessage, ErrorCategory? cat);
    void UpdateValidation(long taskId, ValidationStatus status, string? actualHash);
    DownloadTask? Get(long taskId);
    DownloadTask? GetByKey(string taskKey);
    IReadOnlyList<DownloadTask> GetAll();
    IReadOnlyList<DownloadTask> GetActive();
    IReadOnlyList<DownloadTask> GetInterrupted();
    void Delete(long taskId);
    void PurgeHistory(DateTimeOffset olderThan, bool onlyCompleted);
    string? GetActualHash(long taskId);
}

public interface ISegmentRepository
{
    void ReplaceSegments(long taskId, IReadOnlyList<DownloadSegment> segments);
    IReadOnlyList<DownloadSegment> GetForTask(long taskId);
    void UpdateProgress(long segmentId, long received);
    void DeleteForTask(long taskId);
}

public interface IQueueRepository
{
    IReadOnlyList<DownloadQueueInfo> GetAll();
    DownloadQueueInfo GetOrCreate(string name);
    void Save(DownloadQueueInfo queue);
    void Delete(string name);
}

public interface ISettingsRepository
{
    string? Get(string key);
    void Set(string key, string value);
    IReadOnlyDictionary<string, string> GetAll();
}

public interface ISyncRuleRepository
{
    IReadOnlyList<SyncRule> GetAll();
    SyncRule? Get(long id);
    long Insert(SyncRule rule);
    void Update(SyncRule rule);
    void Delete(long id);
    void SetLastRun(long id, long atMs);
    /// <summary>Stored validators (ETag / Last-Modified) for one rule's URLs.</summary>
    IReadOnlyDictionary<string, (string? ETag, string? LastModified)> GetValidators(long ruleId);
    void SaveValidator(long ruleId, string url, string? etag, string? lastModified);
    void ForgetRule(long ruleId);
}

public interface IFeedRuleRepository
{
    IReadOnlyList<FeedRule> GetAll();
    FeedRule? Get(long id);
    long Insert(FeedRule rule);
    void Update(FeedRule rule);
    void Delete(long id);
    void SetLastCheck(long id, long atMs);
    /// <summary>Item ids already seen for a feed (dedupe across checks).</summary>
    IReadOnlySet<string> GetSeen(long feedId);
    void MarkSeen(long feedId, string itemId, long atMs);
}
