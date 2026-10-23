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
            cmd.CommandText = "SELECT name, kind, max_concurrent, speed_cap_bytes, destination_dir FROM download_queue ORDER BY name";
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
                INSERT INTO download_queue(name, kind, max_concurrent, speed_cap_bytes, destination_dir)
                VALUES(@n,@k,@m,@s,@d)
                ON CONFLICT(name) DO UPDATE SET
                    kind=excluded.kind, max_concurrent=excluded.max_concurrent,
                    speed_cap_bytes=excluded.speed_cap_bytes, destination_dir=excluded.destination_dir
                """;
            cmd.Parameters.AddValue("@n", q.Name);
            cmd.Parameters.AddValue("@k", (int)q.Kind);
            cmd.Parameters.AddValue("@m", q.MaxConcurrent);
            cmd.Parameters.AddValue("@s", q.SpeedCapBytes);
            cmd.Parameters.AddValue("@d", q.DestinationDir ?? string.Empty);
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
