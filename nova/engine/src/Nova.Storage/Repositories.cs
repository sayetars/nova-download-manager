using Microsoft.Data.Sqlite;
using Nova.Core;
using Nova.Core.Models;
using Nova.Core.Protocol;
using System.Text.Json;

namespace Nova.Storage;

/// <summary>
/// Task persistence: maps DownloadTask <-> tasks table. On engine start,
/// every non-terminal task found here is revived (Paused/Queued) — this is
/// the crash-recovery contract.
/// </summary>
public sealed class TaskRepository(Database db)
{
    public void Insert(DownloadTask t)
    {
        const string sql = """
            INSERT INTO tasks (
                id, url, final_url, referring_url, file_name, save_directory, temp_file_path,
                protocol, state, failure, error_message, total_bytes, received_bytes, accept_ranges,
                segments_json, category, queue_name, etag, last_modified, expected_hash, verified_hash,
                auth_json, proxy_json, headers_json, metadata_json, video_json, selected_video_track,
                selected_audio_track, sequential_preview, torrent_info_hash, metalink_file, ytdlp_format,
                created_at, updated_at, started_at, completed_at, retry_count, max_retries,
                scheduled_start, shutdown_when_complete, priority, depth, site_filter
            ) VALUES (
                @id, @url, @final, @ref, @name, @dir, @tmp,
                @proto, @state, @fail, @err, @total, @recv, @ranges,
                @segs, @cat, @queue, @etag, @lm, @ehash, @vhash,
                @auth, @proxy, @hdrs, @meta, @video, @vid, @aud,
                @seq, @tih, @ml, @ytf,
                @created, @updated, @started, @completed, @retry, @maxretry,
                @sched, @shutdown, @prio, @depth, @filter
            );
            """;
        db.ExecuteAsync(sql, Args(t)).GetAwaiter().GetResult();
    }

    public void Update(DownloadTask t)
    {
        t.UpdatedAt = DateTime.UtcNow;
        const string sql = """
            UPDATE tasks SET
                final_url=@final, file_name=@name, save_directory=@dir, temp_file_path=@tmp,
                state=@state, failure=@fail, error_message=@err,
                total_bytes=@total, received_bytes=@recv, accept_ranges=@ranges,
                segments_json=@segs, category=@cat, queue_name=@queue,
                etag=@etag, last_modified=@lm, verified_hash=@vhash,
                headers_json=@hdrs, metadata_json=@meta,
                selected_video_track=@vid, selected_audio_track=@aud,
                retry_count=@retry, updated_at=@updated, started_at=@started,
                completed_at=@completed, priority=@prio
            WHERE id=@id;
            """;
        db.ExecuteAsync(sql, Args(t)).GetAwaiter().GetResult();
    }

    public void UpdateState(DownloadTask t)
    {
        db.ExecuteAsync(
            "UPDATE tasks SET state=@state, failure=@fail, error_message=@err, received_bytes=@recv, " +
            "updated_at=@u WHERE id=@id",
            ("@state", (int)t.State), ("@fail", (int)t.Failure), ("@err", t.ErrorMessage),
            ("@recv", t.ReceivedBytes), ("@u", t.UpdatedAt.Ticks), ("@id", t.Id))
            .GetAwaiter().GetResult();
    }

    public void Delete(string id, out bool existed)
    {
        // caller should also delete .novapart if requested
        existed = db.ExecuteAsync("DELETE FROM tasks WHERE id=@id", ("@id", id)).GetAwaiter().GetResult() > 0;
    }

    public List<DownloadTask> LoadAll()
    {
        const string sql = "SELECT * FROM tasks ORDER BY created_at ASC";
        using var reader = db.QueryAsync(sql).GetAwaiter().GetResult();
        var list = new List<DownloadTask>();
        while (reader.Read()) list.Add(Hydrate(reader));
        return list;
    }

    public List<DownloadTask> LoadActive()
    {
        using var reader = db.QueryAsync(
            "SELECT * FROM tasks WHERE state IN (1,2,3,4,6,8) ORDER BY queue_name, priority DESC, created_at")
            .GetAwaiter().GetResult();
        var list = new List<DownloadTask>();
        while (reader.Read()) list.Add(Hydrate(reader));
        return list;
    }

    // ------------------------------------------------------------------

    private static (string, object?)[] Args(DownloadTask t) => new[]
    {
        ("@id", t.Id), ("@url", t.Url), ("@final", (object?)t.FinalUrl), ("@ref", (object?)t.ReferringUrl),
        ("@name", t.FileName), ("@dir", t.SaveDirectory), ("@tmp", (object?)t.TempFilePath),
        ("@proto", (int)t.Protocol), ("@state", (int)t.State), ("@fail", (int)t.Failure),
        ("@err", (object?)t.ErrorMessage), ("@total", t.TotalBytes), ("@recv", t.ReceivedBytes),
        ("@ranges", t.AcceptRanges ? 1 : 0),
        ("@segs", Json.Serialize(t.Segments)),
        ("@cat", t.Category), ("@queue", t.QueueName),
        ("@etag", (object?)t.ETag), ("@lm", (object?)t.LastModified),
        ("@ehash", (object?)t.ExpectedHash), ("@vhash", (object?)t.VerifiedHash),
        ("@auth", (object?)(t.Username == null && t.Secret == null ? null
            : Json.Serialize(new { t.Username, t.Secret, t.AuthType }))),
        ("@proxy", (object?)(t.Proxy == null ? null : Json.Serialize(t.Proxy))),
        ("@hdrs", Json.Serialize(t.Headers)),
        ("@meta", Json.Serialize(t.Metadata)),
        ("@video", (object?)(t.Video == null ? null : Json.Serialize(t.Video))),
        ("@vid", (object?)t.SelectedVideoTrackId), ("@aud", (object?)t.SelectedAudioTrackId),
        ("@seq", t.IsSequentialPreview ? 1 : 0),
        ("@tih", (object?)t.TorrentInfoHash), ("@ml", (object?)t.MetalinkFile), ("@ytf", (object?)t.YtDlpFormat),
        ("@created", t.CreatedAt.Ticks), ("@updated", t.UpdatedAt.Ticks),
        ("@started", (object?)t.StartedAt?.Ticks), ("@completed", (object?)t.CompletedAt?.Ticks),
        ("@retry", t.RetryCount), ("@maxretry", t.MaxRetries),
        ("@sched", (object?)t.ScheduledStart?.Ticks), ("@shutdown", t.ShutdownWhenComplete ? 1 : 0),
        ("@prio", t.Priority), ("@depth", t.Depth), ("@filter", (object?)t.SiteGrabberFilter)
    };

    private static DownloadTask Hydrate(SqliteDataReader r)
    {
        var auth = ParseJson(r, "auth_json") as JsonElement?;
        var t = new DownloadTask
        {
            Id = r.GetString(r.GetOrdinal("id")),
            Url = r.GetString(r.GetOrdinal("url")),
            FinalUrl = GetStringOrNull(r, "final_url"),
            ReferringUrl = GetStringOrNull(r, "referring_url"),
            FileName = r.GetString(r.GetOrdinal("file_name")),
            SaveDirectory = r.GetString(r.GetOrdinal("save_directory")),
            TempFilePath = GetStringOrNull(r, "temp_file_path"),
            Protocol = (TransferProtocol)r.GetInt32(r.GetOrdinal("protocol")),
            State = (DownloadState)r.GetInt32(r.GetOrdinal("state")),
            Failure = (FailureReason)r.GetInt32(r.GetOrdinal("failure")),
            ErrorMessage = GetStringOrNull(r, "error_message"),
            TotalBytes = r.GetInt64(r.GetOrdinal("total_bytes")),
            ReceivedBytes = r.GetInt64(r.GetOrdinal("received_bytes")),
            AcceptRanges = r.GetInt32(r.GetOrdinal("accept_ranges")) == 1,
            Segments = Json.Deserialize<List<DownloadSegment>>(r.GetString(r.GetOrdinal("segments_json"))) ?? new(),
            Category = r.GetString(r.GetOrdinal("category")),
            QueueName = r.GetString(r.GetOrdinal("queue_name")),
            ETag = GetStringOrNull(r, "etag"),
            LastModified = GetStringOrNull(r, "last_modified"),
            ExpectedHash = GetStringOrNull(r, "expected_hash"),
            VerifiedHash = GetStringOrNull(r, "verified_hash"),
            Headers = Json.Deserialize<Dictionary<string, string>>(r.GetString(r.GetOrdinal("headers_json"))) ?? new(),
            Metadata = Json.Deserialize<Dictionary<string, string>>(r.GetString(r.GetOrdinal("metadata_json"))) ?? new(),
            SelectedVideoTrackId = GetStringOrNull(r, "selected_video_track"),
            SelectedAudioTrackId = GetStringOrNull(r, "selected_audio_track"),
            IsSequentialPreview = r.GetInt32(r.GetOrdinal("sequential_preview")) == 1,
            TorrentInfoHash = GetStringOrNull(r, "torrent_info_hash"),
            MetalinkFile = GetStringOrNull(r, "metalink_file"),
            YtDlpFormat = GetStringOrNull(r, "ytdlp_format"),
            CreatedAt = new DateTime(r.GetInt64(r.GetOrdinal("created_at")), DateTimeKind.Utc),
            UpdatedAt = new DateTime(r.GetInt64(r.GetOrdinal("updated_at")), DateTimeKind.Utc),
            RetryCount = r.GetInt32(r.GetOrdinal("retry_count")),
            MaxRetries = r.GetInt32(r.GetOrdinal("max_retries")),
            ShutdownWhenComplete = r.GetInt32(r.GetOrdinal("shutdown_when_complete")) == 1,
            Priority = r.GetInt32(r.GetOrdinal("priority")),
            Depth = r.GetInt32(r.GetOrdinal("depth")),
            SiteGrabberFilter = GetStringOrNull(r, "site_filter")
        };

        var startedIdx = r.GetOrdinal("started_at");
        if (!r.IsDBNull(startedIdx)) t.StartedAt = new DateTime(r.GetInt64(startedIdx), DateTimeKind.Utc);
        var completedIdx = r.GetOrdinal("completed_at");
        if (!r.IsDBNull(completedIdx)) t.CompletedAt = new DateTime(r.GetInt64(completedIdx), DateTimeKind.Utc);
        var schedIdx = r.GetOrdinal("scheduled_start");
        if (!r.IsDBNull(schedIdx)) t.ScheduledStart = new DateTime(r.GetInt64(schedIdx), DateTimeKind.Utc);

        var proxyJson = GetStringOrNull(r, "proxy_json");
        if (proxyJson != null) t.Proxy = Json.Deserialize<ProxyConfig>(proxyJson);
        var videoJson = GetStringOrNull(r, "video_json");
        if (videoJson != null) t.Video = Json.Deserialize<VideoCandidate>(videoJson);

        if (auth is { } a)
        {
            t.Username = a.TryGetProperty("username", out var u) && u.ValueKind != JsonValueKind.Null ? u.GetString() : null;
            t.AuthType = a.TryGetProperty("authType", out var at) ? at.GetString() ?? "none" : "none";
        }
        return t;
    }

    private static object? ParseJson(SqliteDataReader r, string col)
    {
        var idx = r.GetOrdinal(col);
        return r.IsDBNull(idx) ? null : (object?)Json.Deserialize<JsonElement>(r.GetString(idx));
    }

    private static string? GetStringOrNull(SqliteDataReader r, string col)
    {
        var idx = r.GetOrdinal(col);
        return r.IsDBNull(idx) ? null : r.GetString(idx);
    }
}

/// <summary>Settings persisted as key/value rows; strongly typed via SettingsDocument.</summary>
public sealed class SettingsRepository(Database db)
{
    public SettingsDocument Load()
    {
        using var reader = db.QueryAsync("SELECT key, value FROM settings").GetAwaiter().GetResult();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) map[reader.GetString(0)] = reader.GetString(1);
        var doc = new SettingsDocument();
        foreach (var prop in typeof(SettingsDocument).GetProperties())
        {
            if (map.TryGetValue(prop.Name, out var raw))
            {
                try
                {
                    var typed = prop.PropertyType == typeof(string) ? raw
                        : prop.PropertyType == typeof(bool) ? bool.Parse(raw)
                        : prop.PropertyType == typeof(long) ? long.Parse(raw)
                        : prop.PropertyType == typeof(int) ? int.Parse(raw)
                        : Convert.ChangeType(raw, prop.PropertyType);
                    prop.SetValue(doc, typed);
                }
                catch { /* keep default on malformed row */ }
            }
        }
        return doc;
    }

    public void Save(SettingsDocument doc)
    {
        foreach (var prop in typeof(SettingsDocument).GetProperties())
        {
            var raw = prop.GetValue(doc)?.ToString() ?? "";
            db.ExecuteAsync(
                "INSERT INTO settings(key, value) VALUES (@k, @v) " +
                "ON CONFLICT(key) DO UPDATE SET value=@v",
                ("@k", prop.Name), ("@v", raw)).GetAwaiter().GetResult();
        }
    }
}
