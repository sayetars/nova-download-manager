using Microsoft.Data.Sqlite;
using Nova.Domain;

namespace Nova.Persistence;

/// <summary>
/// Owns the SQLite connection and schema. WAL journal for crash safety,
/// foreign keys on, versioned migrations in a transaction.
/// </summary>
public sealed class NovaDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _txLock = new();
    public string DbPath { get; }

    public NovaDatabase(string dbPath)
    {
        DbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared;Pooling=false");
        _conn.Open();
        using var pragmas = _conn.CreateCommand();
        pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragmas.ExecuteNonQuery();
        Migrate();
    }

    public SqliteConnection Connection => _conn;

    public int SchemaVersion { get; private set; }

    private void Migrate()
    {
        lock (_txLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL); " +
                              "INSERT INTO schema_info(version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT version FROM schema_info LIMIT 1";
            SchemaVersion = Convert.ToInt32(cmd.ExecuteScalar()!);
        }
        if (SchemaVersion < 2) { RunMigration(2, Sql.Migration2); SchemaVersion = 2; }
        if (SchemaVersion < 3) { RunMigration(3, Sql.Migration3); SchemaVersion = 3; }
        if (SchemaVersion < 4) { RunMigration(4, Sql.Migration4); SchemaVersion = 4; }
    }

    private void RunMigration(int version, string sql)
    {
        lock (_txLock)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                cmd.CommandText = "UPDATE schema_info SET version = " + version;
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>Runs a unit of work inside a transaction; rollback on any exception.</summary>
    public T InTransaction<T>(Func<SqliteConnection, T> work)
    {
        lock (_txLock)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                var result = work(_conn);
                tx.Commit();
                return result;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _conn.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }
}

internal static class Sql
{
    public const string Migration2 = """
        CREATE TABLE IF NOT EXISTS download_task (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            task_key TEXT NOT NULL UNIQUE,
            kind INTEGER NOT NULL DEFAULT 0,
            url TEXT NOT NULL,
            effective_url TEXT,
            file_name TEXT NOT NULL,
            destination_dir TEXT NOT NULL,
            state INTEGER NOT NULL DEFAULT 0,
            priority INTEGER NOT NULL DEFAULT 0,
            queue_name TEXT NOT NULL DEFAULT 'main',
            category TEXT NOT NULL DEFAULT 'other',
            total_bytes INTEGER NOT NULL DEFAULT -1,
            received_bytes INTEGER NOT NULL DEFAULT 0,
            etag TEXT,
            last_modified TEXT,
            expected_sha256 TEXT,
            validation INTEGER NOT NULL DEFAULT 0,
            actual_sha256 TEXT,
            segment_count INTEGER NOT NULL DEFAULT 1,
            max_connections INTEGER NOT NULL DEFAULT 4,
            retry_count INTEGER NOT NULL DEFAULT 0,
            max_retries INTEGER NOT NULL DEFAULT 5,
            error_code TEXT,
            error_message TEXT,
            error_category INTEGER,
            created_at TEXT NOT NULL,
            started_at TEXT,
            completed_at TEXT,
            segments_done INTEGER NOT NULL DEFAULT 0,
            segments_total INTEGER NOT NULL DEFAULT 0,
            mime_type TEXT,
            referer TEXT,
            media_selection TEXT,
            file_kept INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_task_state ON download_task(state);
        CREATE INDEX IF NOT EXISTS ix_task_queue ON download_task(queue_name, state);
        CREATE INDEX IF NOT EXISTS ix_task_url ON download_task(url);
        CREATE TABLE IF NOT EXISTS download_segment (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            task_id INTEGER NOT NULL REFERENCES download_task(id) ON DELETE CASCADE,
            seg_index INTEGER NOT NULL,
            start_offset INTEGER NOT NULL,
            end_offset INTEGER NOT NULL,
            received INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_seg_task ON download_segment(task_id);
        CREATE TABLE IF NOT EXISTS download_queue (
            name TEXT PRIMARY KEY,
            kind INTEGER NOT NULL DEFAULT 0,
            max_concurrent INTEGER NOT NULL DEFAULT 0,
            speed_cap_bytes INTEGER NOT NULL DEFAULT 0,
            destination_dir TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS scheduler_rule (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            days_of_week TEXT NOT NULL DEFAULT '',
            start_time TEXT NOT NULL DEFAULT '00:00',
            end_time TEXT NOT NULL DEFAULT '23:59',
            enabled INTEGER NOT NULL DEFAULT 1,
            speed_cap_bytes INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS app_setting (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        INSERT OR IGNORE INTO download_queue(name, kind, max_concurrent, speed_cap_bytes, destination_dir)
            VALUES ('main', 0, 0, 0, '');
        """;

    public const string Migration3 = """
        CREATE TABLE IF NOT EXISTS download_event (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            task_key TEXT NOT NULL,
            event_type TEXT NOT NULL,
            detail TEXT,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_event_task ON download_event(task_key, created_at);
        """;

    public const string Migration4 = """
        ALTER TABLE download_task ADD COLUMN speed_cap_bytes INTEGER NOT NULL DEFAULT 0;
        """;
}
