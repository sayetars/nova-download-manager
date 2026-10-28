using Microsoft.Data.Sqlite;
using Nova.Core;
using Nova.Core.Models;

namespace Nova.Storage;

/// <summary>
/// SQLite persistence: schema-migrated, transactional, crash-safe.
/// This database is the single source of truth for task state — the engine
/// rebuilds its in-memory model entirely from here on startup, which is what
/// makes crash recovery and resume-after-reboot possible.
/// </summary>
public sealed class Database : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; }

    public Database(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={path};Cache=Shared;Pooling=true");
        _conn.Open();
        Migrate();
    }

    // ------------------------------------------------------------------
    // Migrations — forward-only, versioned, each in a transaction
    // ------------------------------------------------------------------

    private static readonly (int Version, string[] Sql)[] Migrations =
    [
        (1, new[]
        {
            """
            CREATE TABLE tasks (
                id TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                final_url TEXT,
                referring_url TEXT,
                file_name TEXT NOT NULL,
                save_directory TEXT NOT NULL,
                temp_file_path TEXT,
                protocol INTEGER NOT NULL DEFAULT 0,
                state INTEGER NOT NULL DEFAULT 0,
                failure INTEGER NOT NULL DEFAULT 0,
                error_message TEXT,
                total_bytes INTEGER NOT NULL DEFAULT -1,
                received_bytes INTEGER NOT NULL DEFAULT 0,
                accept_ranges INTEGER NOT NULL DEFAULT 0,
                segments_json TEXT NOT NULL DEFAULT '[]',
                category TEXT NOT NULL DEFAULT 'general',
                queue_name TEXT NOT NULL DEFAULT 'main',
                etag TEXT,
                last_modified TEXT,
                expected_hash TEXT,
                verified_hash TEXT,
                auth_json TEXT,
                proxy_json TEXT,
                headers_json TEXT NOT NULL DEFAULT '{}',
                metadata_json TEXT NOT NULL DEFAULT '{}',
                video_json TEXT,
                selected_video_track TEXT,
                selected_audio_track TEXT,
                sequential_preview INTEGER NOT NULL DEFAULT 0,
                torrent_info_hash TEXT,
                metalink_file TEXT,
                ytdlp_format TEXT,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                started_at INTEGER,
                completed_at INTEGER,
                retry_count INTEGER NOT NULL DEFAULT 0,
                max_retries INTEGER NOT NULL DEFAULT 10,
                scheduled_start INTEGER,
                shutdown_when_complete INTEGER NOT NULL DEFAULT 0,
                priority INTEGER NOT NULL DEFAULT 0,
                depth INTEGER NOT NULL DEFAULT 0,
                site_filter TEXT
            );
            """,
            "CREATE INDEX idx_tasks_state ON tasks(state);",
            "CREATE INDEX idx_tasks_queue ON tasks(queue_name, priority DESC, created_at);",
            "CREATE INDEX idx_tasks_category ON tasks(category);",
            """
            CREATE TABLE credentials (
                id TEXT PRIMARY KEY,
                realm TEXT NOT NULL,
                username TEXT,
                secret BLOB,               -- encrypted at rest (DPAPI on Windows)
                auth_type TEXT NOT NULL DEFAULT 'basic',
                client_cert_path TEXT,
                created_at INTEGER NOT NULL,
                last_used_at INTEGER NOT NULL
            );
            """,
            """
            CREATE TABLE settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE schedules (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                kind TEXT NOT NULL,
                time_expr TEXT NOT NULL,
                days_of_week TEXT,
                speed_limit_bytes INTEGER,
                queue_name TEXT,
                enabled INTEGER NOT NULL DEFAULT 1,
                calendar TEXT NOT NULL DEFAULT 'gregorian',
                last_run_at INTEGER
            );
            """,
            """
            CREATE TABLE video_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                page_url TEXT NOT NULL,
                title TEXT,
                detected_at INTEGER NOT NULL,
                downloaded INTEGER NOT NULL DEFAULT 0
            );
            """,
            "CREATE UNIQUE INDEX idx_video_history ON video_history(page_url);"
        })
    ];

    private void Migrate()
    {
        using var tx = _conn.BeginTransaction();
        try
        {
            _conn.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)", tx);
            var current = 0;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
                current = Convert.ToInt32(cmd.ExecuteScalar());
            }

            foreach (var (version, sql) in Migrations)
            {
                if (version <= current) continue;
                foreach (var stmt in sql) _conn.ExecuteNonQuery(stmt, tx);
                _conn.ExecuteNonQuery($"INSERT INTO schema_version(version) VALUES ({version})", tx);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    public async Task<T> InTxAsync<T>(Func<SqliteCommand, Task<T>> work)
    {
        await _gate.WaitAsync();
        try
        {
            using var tx = (SqliteTransaction?)null;
            using var cmd = _conn.CreateCommand();
            return await work(cmd);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> ExecuteAsync(string sql, params (string, object?)[] args)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, args);
            return await cmd.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task<SqliteDataReader> QueryAsync(string sql, params (string, object?)[] args)
    {
        await _gate.WaitAsync();
        try
        {
            var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, args);
            return await cmd.ExecuteReaderAsync();
        }
        finally { _gate.Release(); }
    }

    private static void Bind(SqliteCommand cmd, params (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _conn.DisposeAsync();
        _gate.Dispose();
    }
}

file static class SqliteExt
{
    public static void ExecuteNonQuery(this SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
