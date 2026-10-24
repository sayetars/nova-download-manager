using System.Globalization;
using Microsoft.Data.Sqlite;
using Nova.Domain;

namespace Nova.Persistence;

public sealed class SegmentRepository : ISegmentRepository
{
    private readonly NovaDatabase _db;
    public SegmentRepository(NovaDatabase db) => _db = db;

    public void ReplaceSegments(long taskId, IReadOnlyList<DownloadSegment> segments)
    {
        _db.InTransaction<object?>(conn =>
        {
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM download_segment WHERE task_id=@t";
                del.Parameters.AddValue("@t", taskId);
                del.ExecuteNonQuery();
            }
            foreach (var s in segments)
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO download_segment(task_id, seg_index, start_offset, end_offset, received) " +
                                  "VALUES(@t,@i,@s,@e,@r); SELECT last_insert_rowid();";
                ins.Parameters.AddValue("@t", taskId);
                ins.Parameters.AddValue("@i", s.Index);
                ins.Parameters.AddValue("@s", s.Start);
                ins.Parameters.AddValue("@e", s.End);
                ins.Parameters.AddValue("@r", s.Received);
                s.Id = Convert.ToInt64(ins.ExecuteScalar()!);
            }
            return null;
        });
    }

    public IReadOnlyList<DownloadSegment> GetForTask(long taskId)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, task_id, seg_index, start_offset, end_offset, received FROM download_segment WHERE task_id=@t ORDER BY seg_index";
            cmd.Parameters.AddValue("@t", taskId);
            using var r = cmd.ExecuteReader();
            var list = new List<DownloadSegment>();
            while (r.Read())
            {
                list.Add(new DownloadSegment
                {
                    Id = r.GetInt64(0),
                    TaskId = r.GetInt64(1),
                    Index = r.GetInt32(2),
                    Start = r.GetInt64(3),
                    End = r.GetInt64(4),
                    Received = r.GetInt64(5),
                });
            }
            return list;
        }
    }

    public void UpdateProgress(long segmentId, long received)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE download_segment SET received=@r WHERE id=@id";
            cmd.Parameters.AddValue("@r", received);
            cmd.Parameters.AddValue("@id", segmentId);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteForTask(long taskId)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM download_segment WHERE task_id=@t";
            cmd.Parameters.AddValue("@t", taskId);
            cmd.ExecuteNonQuery();
        }
    }
}

public sealed class QueueRepository : IQueueRepository
{
    private readonly NovaDatabase _db;
    public QueueRepository(NovaDatabase db) => _db = db;

    public IReadOnlyList<DownloadQueueInfo> GetAll()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name, kind, max_concurrent, speed_cap_bytes, destination_dir, schedule_json FROM download_queue ORDER BY name";
            using var r = cmd.ExecuteReader();
            var list = new List<DownloadQueueInfo>();
            while (r.Read())
                list.Add(new DownloadQueueInfo
                {
                    Name = r.GetString(0),
                    Kind = (QueueKind)r.GetInt32(1),
                    MaxConcurrent = r.GetInt32(2),
                    SpeedCapBytes = r.GetInt64(3),
                    DestinationDir = r.GetString(4),
                    ScheduleJson = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                });
            return list;
        }
    }

    public DownloadQueueInfo GetOrCreate(string name)
    {
        var existing = GetAll().FirstOrDefault(q => q.Name == name);
        if (existing is not null) return existing;
        var q = new DownloadQueueInfo { Name = name };
        Save(q);
        return q;
    }

    public void Save(DownloadQueueInfo q)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO download_queue(name, kind, max_concurrent, speed_cap_bytes, destination_dir, schedule_json)
                VALUES(@n,@k,@m,@s,@d,@j)
                ON CONFLICT(name) DO UPDATE SET
                    kind=excluded.kind, max_concurrent=excluded.max_concurrent,
                    speed_cap_bytes=excluded.speed_cap_bytes, destination_dir=excluded.destination_dir,
                    schedule_json=excluded.schedule_json
                """;
            cmd.Parameters.AddValue("@n", q.Name);
            cmd.Parameters.AddValue("@k", (int)q.Kind);
            cmd.Parameters.AddValue("@m", q.MaxConcurrent);
            cmd.Parameters.AddValue("@s", q.SpeedCapBytes);
            cmd.Parameters.AddValue("@d", q.DestinationDir ?? string.Empty);
            cmd.Parameters.AddValue("@j", q.ScheduleJson ?? string.Empty);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(string name)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM download_queue WHERE name=@n AND name <> 'main'";
            cmd.Parameters.AddValue("@n", name);
            cmd.ExecuteNonQuery();
        }
    }
}

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly NovaDatabase _db;
    public SettingsRepository(NovaDatabase db) => _db = db;

    public string? Get(string key)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM app_setting WHERE key=@k";
            cmd.Parameters.AddValue("@k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void Set(string key, string value)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO app_setting(key, value) VALUES(@k,@v) " +
                              "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddValue("@k", key);
            cmd.Parameters.AddValue("@v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyDictionary<string, string> GetAll()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM app_setting";
            using var r = cmd.ExecuteReader();
            var dict = new Dictionary<string, string>();
            while (r.Read()) dict[r.GetString(0)] = r.GetString(1);
            return dict;
        }
    }
}

/// <summary>Sync-rule persistence (IDM parity: periodic synchronization queues).</summary>
public sealed class SyncRuleRepository : ISyncRuleRepository
{
    private readonly NovaDatabase _db;
    public SyncRuleRepository(NovaDatabase db) => _db = db;

    public IReadOnlyList<SyncRule> GetAll()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, urls, destination_dir, category, interval_minutes, enabled, last_run_ms FROM sync_rule ORDER BY id";
            using var r = cmd.ExecuteReader();
            var list = new List<SyncRule>();
            while (r.Read())
                list.Add(new SyncRule
                {
                    Id = r.GetInt64(0),
                    Name = r.GetString(1),
                    Urls = r.GetString(2),
                    DestinationDir = r.GetString(3),
                    Category = r.GetString(4),
                    IntervalMinutes = r.GetInt32(5),
                    Enabled = r.GetInt32(6) != 0,
                    LastRunMs = r.GetInt64(7),
                });
            return list;
        }
    }

    public SyncRule? Get(long id)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, urls, destination_dir, category, interval_minutes, enabled, last_run_ms FROM sync_rule WHERE id=@i";
            cmd.Parameters.AddValue("@i", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new SyncRule
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                Urls = r.GetString(2),
                DestinationDir = r.GetString(3),
                Category = r.GetString(4),
                IntervalMinutes = r.GetInt32(5),
                Enabled = r.GetInt32(6) != 0,
                LastRunMs = r.GetInt64(7),
            };
        }
    }

    public long Insert(SyncRule rule)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO sync_rule(name, urls, destination_dir, category, interval_minutes, enabled, last_run_ms)
                VALUES(@n,@u,@d,@c,@i,@e,@l)
                """;
            Bind(cmd, rule);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt64(cmd.ExecuteScalar()!);
        }
    }

    public void Update(SyncRule rule)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE sync_rule SET name=@n, urls=@u, destination_dir=@d, category=@c,
                    interval_minutes=@i, enabled=@e, last_run_ms=@l WHERE id=@i2
                """;
            Bind(cmd, rule);
            cmd.Parameters.AddValue("@i2", rule.Id);
            cmd.ExecuteNonQuery();
        }
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand cmd, SyncRule rule)
    {
        cmd.Parameters.AddValue("@n", rule.Name ?? string.Empty);
        cmd.Parameters.AddValue("@u", rule.Urls ?? string.Empty);
        cmd.Parameters.AddValue("@d", rule.DestinationDir ?? string.Empty);
        cmd.Parameters.AddValue("@c", rule.Category ?? "other");
        cmd.Parameters.AddValue("@i", rule.IntervalMinutes);
        cmd.Parameters.AddValue("@e", rule.Enabled ? 1 : 0);
        cmd.Parameters.AddValue("@l", rule.LastRunMs);
    }

    public void Delete(long id)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM sync_rule WHERE id=@i";
            cmd.Parameters.AddValue("@i", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetLastRun(long id, long atMs)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE sync_rule SET last_run_ms=@m WHERE id=@i";
            cmd.Parameters.AddValue("@m", atMs);
            cmd.Parameters.AddValue("@i", id);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyDictionary<string, (string? ETag, string? LastModified)> GetValidators(long ruleId)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT url, etag, last_modified FROM sync_state WHERE rule_id=@r";
            cmd.Parameters.AddValue("@r", ruleId);
            using var r = cmd.ExecuteReader();
            var dict = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
                dict[r.GetString(0)] = (r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            return dict;
        }
    }

    public void SaveValidator(long ruleId, string url, string? etag, string? lastModified)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO sync_state(rule_id, url, etag, last_modified) VALUES(@r,@u,@e,@m)
                ON CONFLICT(rule_id, url) DO UPDATE SET etag=excluded.etag, last_modified=excluded.last_modified
                """;
            cmd.Parameters.AddValue("@r", ruleId);
            cmd.Parameters.AddValue("@u", url);
            cmd.Parameters.AddValue("@e", (object?)etag ?? DBNull.Value);
            cmd.Parameters.AddValue("@m", (object?)lastModified ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void ForgetRule(long ruleId)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM sync_state WHERE rule_id=@r";
            cmd.Parameters.AddValue("@r", ruleId);
            cmd.ExecuteNonQuery();
        }
    }
}

/// <summary>Feed-subscription persistence (v1.3.0: RSS/Atom auto-download).</summary>
public sealed class FeedRuleRepository : IFeedRuleRepository
{
    private readonly NovaDatabase _db;
    public FeedRuleRepository(NovaDatabase db) => _db = db;

    private static FeedRule Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Url = r.GetString(2),
        Filters = r.GetString(3),
        DestinationDir = r.GetString(4),
        Category = r.GetString(5),
        IntervalMinutes = r.GetInt32(6),
        Enabled = r.GetInt32(7) != 0,
        LastCheckMs = r.GetInt64(8),
        AutoStart = r.GetInt32(9) != 0,
        MaxItems = r.GetInt32(10),
    };

    private const string Columns = "id, name, url, filters, destination_dir, category, interval_minutes, enabled, last_check_ms, auto_start, max_items";

    public IReadOnlyList<FeedRule> GetAll()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {Columns} FROM feed_rule ORDER BY id";
            using var r = cmd.ExecuteReader();
            var list = new List<FeedRule>();
            while (r.Read()) list.Add(Map(r));
            return list;
        }
    }

    public FeedRule? Get(long id)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {Columns} FROM feed_rule WHERE id=@i";
            cmd.Parameters.AddValue("@i", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand cmd, FeedRule rule)
    {
        cmd.Parameters.AddValue("@n", rule.Name ?? string.Empty);
        cmd.Parameters.AddValue("@u", rule.Url ?? string.Empty);
        cmd.Parameters.AddValue("@f", rule.Filters ?? string.Empty);
        cmd.Parameters.AddValue("@d", rule.DestinationDir ?? string.Empty);
        cmd.Parameters.AddValue("@c", rule.Category ?? "other");
        cmd.Parameters.AddValue("@i", Math.Clamp(rule.IntervalMinutes, 1, 10_080));
        cmd.Parameters.AddValue("@e", rule.Enabled ? 1 : 0);
        cmd.Parameters.AddValue("@l", rule.LastCheckMs);
        cmd.Parameters.AddValue("@a", rule.AutoStart ? 1 : 0);
        cmd.Parameters.AddValue("@m", Math.Max(0, rule.MaxItems));
    }

    public long Insert(FeedRule rule)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO feed_rule(name, url, filters, destination_dir, category, interval_minutes, enabled, last_check_ms, auto_start, max_items)
                VALUES(@n,@u,@f,@d,@c,@i,@e,@l,@a,@m)
                """;
            Bind(cmd, rule);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt64(cmd.ExecuteScalar()!);
        }
    }

    public void Update(FeedRule rule)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE feed_rule SET name=@n, url=@u, filters=@f, destination_dir=@d, category=@c,
                    interval_minutes=@i, enabled=@e, last_check_ms=@l, auto_start=@a, max_items=@m
                WHERE id=@id
                """;
            Bind(cmd, rule);
            cmd.Parameters.AddValue("@id", rule.Id);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(long id)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM feed_seen WHERE feed_id=@i; DELETE FROM feed_rule WHERE id=@i;";
            cmd.Parameters.AddValue("@i", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetLastCheck(long id, long atMs)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE feed_rule SET last_check_ms=@l WHERE id=@i";
            cmd.Parameters.AddValue("@l", atMs);
            cmd.Parameters.AddValue("@i", id);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlySet<string> GetSeen(long feedId)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT item_id FROM feed_seen WHERE feed_id=@i";
            cmd.Parameters.AddValue("@i", feedId);
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>();
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    public void MarkSeen(long feedId, string itemId, long atMs)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO feed_seen(feed_id, item_id, seen_ms) VALUES(@i,@t,@m)";
            cmd.Parameters.AddValue("@i", feedId);
            cmd.Parameters.AddValue("@t", itemId);
            cmd.Parameters.AddValue("@m", atMs);
            cmd.ExecuteNonQuery();
        }
    }
}
