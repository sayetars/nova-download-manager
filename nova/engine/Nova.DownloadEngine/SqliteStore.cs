using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Nova.DownloadEngine;

/// <summary>
/// SQLite persistence — the single source of truth for task state.
/// Supports crash recovery: segments' downloaded counters are checkpointed
/// every ~2 seconds during transfers and reloaded at startup.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    readonly SqliteConnection _conn;
    readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteStore(string path)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS tasks (
                id            TEXT PRIMARY KEY,
                url           TEXT NOT NULL,
                file_path     TEXT NOT NULL,
                etag          TEXT,
                last_modified TEXT,
                total_size    INTEGER NOT NULL,
                segment_count INTEGER NOT NULL,
                state         TEXT NOT NULL,
                error         TEXT,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS segments (
                task_id    TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                id         INTEGER NOT NULL,
                start      INTEGER NOT NULL,
                end        INTEGER NOT NULL,
                downloaded INTEGER NOT NULL,
                PRIMARY KEY (task_id, id)
            );
            """);
    }

    void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Save(DownloadTask t)
    {
        _gate.Wait();
        try
        {
            using var tx = _conn.BeginTransaction();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO tasks (id,url,file_path,etag,last_modified,total_size,segment_count,state,error,created_at,updated_at)
                    VALUES ($id,$url,$fp,$etag,$lm,$size,$segs,$state,$err,$ca,$ua)
                    ON CONFLICT(id) DO UPDATE SET
                        url=$url, file_path=$fp, etag=$etag, last_modified=$lm, total_size=$size,
                        segment_count=$segs, state=$state, error=$err, updated_at=$ua
                    """;
                var p = cmd.Parameters;
                p.AddWithValue("$id", t.Id);
                p.AddWithValue("$url", t.Url);
                p.AddWithValue("$fp", t.FilePath);
                p.AddWithValue("$etag", (object?)t.ETag ?? DBNull.Value);
                p.AddWithValue("$lm", (object?)t.LastModified ?? DBNull.Value);
                p.AddWithValue("$size", t.TotalSize);
                p.AddWithValue("$segs", t.SegmentCount);
                p.AddWithValue("$state", t.State.ToString());
                p.AddWithValue("$err", (object?)t.Error ?? DBNull.Value);
                p.AddWithValue("$ca", t.CreatedAt.ToString("o"));
                p.AddWithValue("$ua", t.UpdatedAt.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM segments WHERE task_id = $id";
                cmd.Parameters.AddWithValue("$id", t.Id);
                cmd.ExecuteNonQuery();
            }
            foreach (var s in t.Segments)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO segments (task_id,id,start,end,downloaded) VALUES ($t,$i,$s,$e,$d)";
                cmd.Parameters.AddWithValue("$t", t.Id);
                cmd.Parameters.AddWithValue("$i", s.Id);
                cmd.Parameters.AddWithValue("$s", s.Start);
                cmd.Parameters.AddWithValue("$e", s.End);
                cmd.Parameters.AddWithValue("$d", s.Downloaded);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally { _gate.Release(); }
    }

    public List<DownloadTask> LoadAll()
    {
        _gate.Wait();
        try
        {
            var tasks = new List<DownloadTask>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,url,file_path,etag,last_modified,total_size,segment_count,state,error,created_at,updated_at FROM tasks ORDER BY created_at";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    tasks.Add(new DownloadTask
                    {
                        Id = r.GetString(0),
                        Url = r.GetString(1),
                        FilePath = r.GetString(2),
                        ETag = r.IsDBNull(3) ? null : r.GetString(3),
                        LastModified = r.IsDBNull(4) ? null : r.GetString(4),
                        TotalSize = r.GetInt64(5),
                        SegmentCount = r.GetInt32(6),
                        State = Enum.Parse<DownloadState>(r.GetString(7)),
                        Error = r.IsDBNull(8) ? null : r.GetString(8),
                        CreatedAt = DateTime.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        UpdatedAt = DateTime.Parse(r.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    });
                }
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT task_id,id,start,end,downloaded FROM segments ORDER BY task_id,id";
                using var r = cmd.ExecuteReader();
                var map = tasks.ToDictionary(t => t.Id);
                while (r.Read())
                {
                    var tid = r.GetString(0);
                    if (map.TryGetValue(tid, out var t))
                        t.Segments.Add(new DownloadSegment
                        {
                            Id = r.GetInt32(1),
                            Start = r.GetInt64(2),
                            End = r.GetInt64(3),
                            Downloaded = r.GetInt64(4),
                        });
                }
            }
            return tasks;
        }
        finally { _gate.Release(); }
    }

    public void Delete(string id)
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tasks WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _conn.Dispose();
    }
}
