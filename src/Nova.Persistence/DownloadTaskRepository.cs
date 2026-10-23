using System.Globalization;
using Microsoft.Data.Sqlite;
using Nova.Domain;

namespace Nova.Persistence;

public sealed class DownloadTaskRepository : IDownloadTaskRepository
{
    private readonly NovaDatabase _db;
    public DownloadTaskRepository(NovaDatabase db) => _db = db;

    public long Insert(DownloadTask t)
    {
        return _db.InTransaction(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO download_task
                (task_key, kind, url, effective_url, file_name, destination_dir, state, priority,
                 queue_name, category, total_bytes, received_bytes, etag, last_modified,
                 expected_sha256, validation, segment_count, max_connections, speed_cap_bytes,
                 retry_count, max_retries,
                 created_at, started_at, completed_at, segments_done, segments_total, mime_type,
                 referer, media_selection, file_kept, mirror_urls, start_at_ms, post_action)
                VALUES
                (@key, @kind, @url, @eff, @name, @dir, @state, @prio, @queue, @cat, @total, @recv,
                 @etag, @lm, @sha, @valid, @segc, @maxc, @cap,
                 @retry, @maxr, @created, @started,
                 @completed, @segd, @segt, @mime, @ref, @sel, @kept, @mirrors, @startat, @postact);
                SELECT last_insert_rowid();
                """;
            BindTask(cmd, t);
            return Convert.ToInt64(cmd.ExecuteScalar()!);
        });
    }

    public void Update(DownloadTask t)
    {
        _db.InTransaction<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE download_task SET
                    kind=@kind, url=@url, effective_url=@eff, file_name=@name, destination_dir=@dir,
                    state=@state, priority=@prio, queue_name=@queue, category=@cat,
                    total_bytes=@total, received_bytes=@recv, etag=@etag, last_modified=@lm,
                    expected_sha256=@sha, validation=@valid, segment_count=@segc,
                    max_connections=@maxc, speed_cap_bytes=@cap,
                    retry_count=@retry, max_retries=@maxr,
                    error_code=@ecode, error_message=@emsg, error_category=@ecat,
                    started_at=@started, completed_at=@completed,
                    segments_done=@segd, segments_total=@segt, mime_type=@mime,
                    referer=@ref, media_selection=@sel, file_kept=@kept,
                    mirror_urls=@mirrors, start_at_ms=@startat, post_action=@postact
                WHERE id=@id;
                """;
            BindTask(cmd, t);
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    public void UpdateProgress(long taskId, DownloadState state, long received, int segmentsDone, int segmentsTotal)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE download_task SET state=@s, received_bytes=@r, segments_done=@d, segments_total=@t WHERE id=@id";
            cmd.Parameters.AddValue("@s", (int)state);
            cmd.Parameters.AddValue("@r", received);
            cmd.Parameters.AddValue("@d", segmentsDone);
            cmd.Parameters.AddValue("@t", segmentsTotal);
            cmd.Parameters.AddValue("@id", taskId);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateState(long taskId, DownloadState state, string? errorCode, string? errorMessage, ErrorCategory? cat)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE download_task SET state=@s, error_code=@c, error_message=@m, error_category=@cat, " +
                              "completed_at=CASE WHEN @s IN (9,10,11) THEN COALESCE(completed_at, @now) ELSE completed_at END WHERE id=@id";
            cmd.Parameters.AddValue("@s", (int)state);
            cmd.Parameters.AddValue("@c", (object?)errorCode ?? DBNull.Value);
            cmd.Parameters.AddValue("@m", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddValue("@cat", (object?)(cat.HasValue ? (int)cat.Value : null) ?? DBNull.Value);
            cmd.Parameters.AddValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddValue("@id", taskId);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateValidation(long taskId, ValidationStatus status, string? actualHash)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE download_task SET validation=@v, actual_sha256=@h WHERE id=@id";
            cmd.Parameters.AddValue("@v", (int)status);
            cmd.Parameters.AddValue("@h", (object?)actualHash ?? DBNull.Value);
            cmd.Parameters.AddValue("@id", taskId);
            cmd.ExecuteNonQuery();
        }
    }

    public DownloadTask? Get(long taskId) => QueryOne("SELECT * FROM download_task WHERE id=@id", p => p.Parameters.AddValue("@id", taskId));
    public DownloadTask? GetByKey(string taskKey) => QueryOne("SELECT * FROM download_task WHERE task_key=@k", p => p.Parameters.AddValue("@k", taskKey));

    public IReadOnlyList<DownloadTask> GetAll() => QueryList("SELECT * FROM download_task ORDER BY id DESC");
    public IReadOnlyList<DownloadTask> GetActive() => QueryList(
        "SELECT * FROM download_task WHERE state IN (1,3,4,6,8) ORDER BY id");
    public IReadOnlyList<DownloadTask> GetInterrupted() => QueryList(
        "SELECT * FROM download_task WHERE state IN (3,4,6,7) ORDER BY priority DESC, id");

    public void Delete(long taskId)
    {
        _db.InTransaction<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM download_segment WHERE task_id=@id; DELETE FROM download_task WHERE id=@id;";
            cmd.Parameters.AddValue("@id", taskId);
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    public void PurgeHistory(DateTimeOffset olderThan, bool onlyCompleted)
    {
        _db.InTransaction<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = onlyCompleted
                ? "DELETE FROM download_task WHERE state IN (9,11) AND created_at < @c"
                : "DELETE FROM download_task WHERE created_at < @c";
            cmd.Parameters.AddValue("@c", olderThan.ToString("O"));
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    public string? GetActualHash(long taskId)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT actual_sha256 FROM download_task WHERE id=@id";
            cmd.Parameters.AddValue("@id", taskId);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void LogEvent(string taskKey, string type, string? detail)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO download_event(task_key, event_type, detail, created_at) VALUES(@k,@t,@d,@c)";
            cmd.Parameters.AddValue("@k", taskKey);
            cmd.Parameters.AddValue("@t", type);
            cmd.Parameters.AddValue("@d", (object?)detail ?? DBNull.Value);
            cmd.Parameters.AddValue("@c", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    // ---- mapping -----------------------------------------------------------

    private static void BindTask(SqliteCommand cmd, DownloadTask t)
    {
        cmd.Parameters.AddValue("@id", t.Id);
        cmd.Parameters.AddValue("@key", t.TaskKey);
        cmd.Parameters.AddValue("@kind", (int)t.Kind);
        cmd.Parameters.AddValue("@url", t.Url);
        cmd.Parameters.AddValue("@eff", (object?)t.EffectiveUrl ?? DBNull.Value);
        cmd.Parameters.AddValue("@name", t.FileName);
        cmd.Parameters.AddValue("@dir", t.DestinationDir);
        cmd.Parameters.AddValue("@state", (int)t.State);
        cmd.Parameters.AddValue("@prio", (int)t.Priority);
        cmd.Parameters.AddValue("@queue", t.QueueName);
        cmd.Parameters.AddValue("@cat", t.Category);
        cmd.Parameters.AddValue("@total", t.TotalBytes);
        cmd.Parameters.AddValue("@recv", t.ReceivedBytes);
        cmd.Parameters.AddValue("@etag", (object?)t.Etag ?? DBNull.Value);
        cmd.Parameters.AddValue("@lm", (object?)t.LastModified ?? DBNull.Value);
        cmd.Parameters.AddValue("@sha", (object?)t.ExpectedSha256 ?? DBNull.Value);
        cmd.Parameters.AddValue("@valid", (int)t.Validation);
        cmd.Parameters.AddValue("@segc", t.SegmentCount);
        cmd.Parameters.AddValue("@maxc", t.MaxConnections);
        cmd.Parameters.AddValue("@cap", t.SpeedCapBytes);
        cmd.Parameters.AddValue("@retry", t.RetryCount);
        cmd.Parameters.AddValue("@maxr", t.MaxRetries);
        cmd.Parameters.AddValue("@ecode", (object?)t.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddValue("@emsg", (object?)t.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddValue("@ecat", (object?)(t.ErrorCategory.HasValue ? (int)t.ErrorCategory.Value : null) ?? DBNull.Value);
        cmd.Parameters.AddValue("@created", t.CreatedAt.ToString("O"));
        cmd.Parameters.AddValue("@started", (object?)(t.StartedAt?.ToString("O")) ?? DBNull.Value);
        cmd.Parameters.AddValue("@completed", (object?)(t.CompletedAt?.ToString("O")) ?? DBNull.Value);
        cmd.Parameters.AddValue("@segd", t.SegmentsDone);
        cmd.Parameters.AddValue("@segt", t.SegmentsTotal);
        cmd.Parameters.AddValue("@mime", (object?)t.MimeType ?? DBNull.Value);
        cmd.Parameters.AddValue("@ref", (object?)t.Referer ?? DBNull.Value);
        cmd.Parameters.AddValue("@sel", (object?)t.MediaSelection ?? DBNull.Value);
        cmd.Parameters.AddValue("@kept", t.FileKept ? 1 : 0);
        cmd.Parameters.AddValue("@mirrors", t.MirrorUrls);
        cmd.Parameters.AddValue("@startat", t.StartAtMs);
        cmd.Parameters.AddValue("@postact", t.PostAction);
    }

    private static DownloadTask? MapOne(SqliteDataReader r) => r.Read() ? Map(r) : null;

    private static DownloadTask Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        TaskKey = r.GetString(r.GetOrdinal("task_key")),
        Kind = (DownloadKind)r.GetInt32(r.GetOrdinal("kind")),
        Url = r.GetString(r.GetOrdinal("url")),
        EffectiveUrl = r.IsDBNull(r.GetOrdinal("effective_url")) ? null : r.GetString(r.GetOrdinal("effective_url")),
        FileName = r.GetString(r.GetOrdinal("file_name")),
        DestinationDir = r.GetString(r.GetOrdinal("destination_dir")),
        State = (DownloadState)r.GetInt32(r.GetOrdinal("state")),
        Priority = (TaskPriority)r.GetInt32(r.GetOrdinal("priority")),
        QueueName = r.GetString(r.GetOrdinal("queue_name")),
        Category = r.GetString(r.GetOrdinal("category")),
        TotalBytes = r.GetInt64(r.GetOrdinal("total_bytes")),
        ReceivedBytes = r.GetInt64(r.GetOrdinal("received_bytes")),
        Etag = r.IsDBNull(r.GetOrdinal("etag")) ? null : r.GetString(r.GetOrdinal("etag")),
        LastModified = r.IsDBNull(r.GetOrdinal("last_modified")) ? null : r.GetString(r.GetOrdinal("last_modified")),
        ExpectedSha256 = r.IsDBNull(r.GetOrdinal("expected_sha256")) ? null : r.GetString(r.GetOrdinal("expected_sha256")),
        Validation = (ValidationStatus)r.GetInt32(r.GetOrdinal("validation")),
        SegmentCount = r.GetInt32(r.GetOrdinal("segment_count")),
        MaxConnections = r.GetInt32(r.GetOrdinal("max_connections")),
        SpeedCapBytes = r.GetInt64(r.GetOrdinal("speed_cap_bytes")),
        RetryCount = r.GetInt32(r.GetOrdinal("retry_count")),
        MaxRetries = r.GetInt32(r.GetOrdinal("max_retries")),
        ErrorCode = r.IsDBNull(r.GetOrdinal("error_code")) ? null : r.GetString(r.GetOrdinal("error_code")),
        ErrorMessage = r.IsDBNull(r.GetOrdinal("error_message")) ? null : r.GetString(r.GetOrdinal("error_message")),
        ErrorCategory = r.IsDBNull(r.GetOrdinal("error_category")) ? null : (ErrorCategory)r.GetInt32(r.GetOrdinal("error_category")),
        CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), CultureInfo.InvariantCulture),
        StartedAt = r.IsDBNull(r.GetOrdinal("started_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("started_at")), CultureInfo.InvariantCulture),
        CompletedAt = r.IsDBNull(r.GetOrdinal("completed_at")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("completed_at")), CultureInfo.InvariantCulture),
        SegmentsDone = r.GetInt32(r.GetOrdinal("segments_done")),
        SegmentsTotal = r.GetInt32(r.GetOrdinal("segments_total")),
        MimeType = r.IsDBNull(r.GetOrdinal("mime_type")) ? null : r.GetString(r.GetOrdinal("mime_type")),
        Referer = r.IsDBNull(r.GetOrdinal("referer")) ? null : r.GetString(r.GetOrdinal("referer")),
        MediaSelection = r.IsDBNull(r.GetOrdinal("media_selection")) ? null : r.GetString(r.GetOrdinal("media_selection")),
        FileKept = r.GetInt32(r.GetOrdinal("file_kept")) != 0,
        MirrorUrls = r.GetString(r.GetOrdinal("mirror_urls")),
        StartAtMs = r.GetInt64(r.GetOrdinal("start_at_ms")),
        PostAction = r.GetString(r.GetOrdinal("post_action")),
    };

    private DownloadTask? QueryOne(string sql, Action<SqliteCommand> bind)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = sql;
            bind(cmd);
            using var r = cmd.ExecuteReader();
            return MapOne(r);
        }
    }

    private IReadOnlyList<DownloadTask> QueryList(string sql)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var list = new List<DownloadTask>();
            while (r.Read()) list.Add(Map(r));
            return list;
        }
    }
}
