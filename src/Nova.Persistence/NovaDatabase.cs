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
        if (SchemaVersion < 5) { RunMigration(5, Sql.Migration5); SchemaVersion = 5; }
        if (SchemaVersion < 6) { RunMigration(6, Sql.Migration6); SchemaVersion = 6; }
        if (SchemaVersion < 7) { RunMigration(7, Sql.Migration7); SchemaVersion = 7; }
        if (SchemaVersion < 8) { RunMigration(8, Sql.Migration8); SchemaVersion = 8; }
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

    // ---- byte-quota checkpoints (QuotaTracker persistence) ---------------------

    /// <summary>Checkpoint rows inside the window, oldest first.</summary>
    public IReadOnlyList<(long AtMs, long Bytes)> QuotaSamplesSince(long cutoffMs)
    {
        lock (_txLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT at_ms, bytes FROM byte_quota_log WHERE at_ms >= @c ORDER BY at_ms";
            cmd.Parameters.AddWithValue("@c", cutoffMs);
            using var r = cmd.ExecuteReader();
            var list = new List<(long, long)>();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1)));
            return list;
        }
    }

    public void QuotaAppend(long atMs, long bytes)
    {
        lock (_txLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO byte_quota_log(at_ms, bytes) VALUES(@a, @b)";
            cmd.Parameters.AddWithValue("@a", atMs);
            cmd.Parameters.AddWithValue("@b", bytes);
            cmd.ExecuteNonQuery();
        }
    }

    public void QuotaPruneBefore(long cutoffMs)
    {
        lock (_txLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM byte_quota_log WHERE at_ms < @c";
            cmd.Parameters.AddWithValue("@c", cutoffMs);
            cmd.ExecuteNonQuery();
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

    public const string Migration5 = """
        ALTER TABLE download_task ADD COLUMN mirror_urls TEXT NOT NULL DEFAULT '';
        ALTER TABLE download_task ADD COLUMN start_at_ms INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_task ADD COLUMN post_action TEXT NOT NULL DEFAULT '';
        CREATE TABLE IF NOT EXISTS login_vault (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            host TEXT NOT NULL,
            realm TEXT NOT NULL DEFAULT '',
            user_name TEXT NOT NULL,
            secret TEXT NOT NULL,
            scheme TEXT NOT NULL DEFAULT 'basic',
            created_at TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_vault_host ON login_vault(host, realm, user_name);
        CREATE TABLE IF NOT EXISTS grabber_project (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            root_url TEXT NOT NULL,
            max_depth INTEGER NOT NULL DEFAULT 3,
            include_filter TEXT NOT NULL DEFAULT '',
            exclude_filter TEXT NOT NULL DEFAULT '',
            options TEXT NOT NULL DEFAULT '{}',
            save_dir TEXT NOT NULL DEFAULT '',
            last_run_at TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_grabber_root ON grabber_project(root_url);
        CREATE TABLE IF NOT EXISTS category_def (
            name TEXT PRIMARY KEY,
            patterns TEXT NOT NULL DEFAULT '',
            destination_dir TEXT NOT NULL DEFAULT '',
            icon TEXT NOT NULL DEFAULT 'folder'
        );
        CREATE TABLE IF NOT EXISTS byte_quota_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            at_ms INTEGER NOT NULL,
            bytes INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_quota_time ON byte_quota_log(at_ms);
        """;

    public const string Migration6 = """
        ALTER TABLE download_queue ADD COLUMN schedule_json TEXT NOT NULL DEFAULT '';
        CREATE TABLE IF NOT EXISTS sync_rule (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            urls TEXT NOT NULL DEFAULT '',
            destination_dir TEXT NOT NULL DEFAULT '',
            category TEXT NOT NULL DEFAULT 'other',
            interval_minutes INTEGER NOT NULL DEFAULT 60,
            enabled INTEGER NOT NULL DEFAULT 1,
            last_run_ms INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS sync_state (
            rule_id INTEGER NOT NULL REFERENCES sync_rule(id) ON DELETE CASCADE,
            url TEXT NOT NULL,
            etag TEXT,
            last_modified TEXT,
            PRIMARY KEY (rule_id, url)
        );
        """;

    public const string Migration8 = """
        ALTER TABLE download_task ADD COLUMN bt_info_hash TEXT NOT NULL DEFAULT '';
        ALTER TABLE download_task ADD COLUMN seed_after INTEGER NOT NULL DEFAULT 0;
        """;

    public const string Migration7 = """
        ALTER TABLE download_task ADD COLUMN cookies TEXT NOT NULL DEFAULT '';
        ALTER TABLE download_task ADD COLUMN extra_headers TEXT NOT NULL DEFAULT '';
        ALTER TABLE download_task ADD COLUMN sequential INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_task ADD COLUMN expected_hash TEXT NOT NULL DEFAULT '';
        ALTER TABLE download_task ADD COLUMN piece_hashes TEXT NOT NULL DEFAULT '';
        ALTER TABLE download_task ADD COLUMN convert_preset TEXT NOT NULL DEFAULT '';
        CREATE TABLE IF NOT EXISTS feed_rule (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            url TEXT NOT NULL,
            filters TEXT NOT NULL DEFAULT '',
            destination_dir TEXT NOT NULL DEFAULT '',
            category TEXT NOT NULL DEFAULT 'other',
            interval_minutes INTEGER NOT NULL DEFAULT 30,
            enabled INTEGER NOT NULL DEFAULT 1,
            last_check_ms INTEGER NOT NULL DEFAULT 0,
            auto_start INTEGER NOT NULL DEFAULT 1,
            max_items INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS feed_seen (
            feed_id INTEGER NOT NULL REFERENCES feed_rule(id) ON DELETE CASCADE,
            item_id TEXT NOT NULL,
            seen_ms INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (feed_id, item_id)
        );
        """;
}
