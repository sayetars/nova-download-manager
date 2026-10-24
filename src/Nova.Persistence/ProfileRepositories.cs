using System.Globalization;
using Microsoft.Data.Sqlite;
using Nova.Domain;

namespace Nova.Persistence;

/// <summary>v1.5.0 (G27): persistence for per-server performance profiles.</summary>
public sealed class ServerProfileRepository
{
    private readonly NovaDatabase _db;
    public ServerProfileRepository(NovaDatabase db) => _db = db;

    public IReadOnlyList<ServerProfile> GetAll()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT host, ewma_bps, ewma_ttfb_ms, ok_count, fail_count, bytes_total, last_active_ms " +
                              "FROM server_profile ORDER BY last_active_ms DESC";
            using var r = cmd.ExecuteReader();
            var list = new List<ServerProfile>();
            while (r.Read())
                list.Add(new ServerProfile
                {
                    Host = r.GetString(0),
                    EwmaBps = r.GetInt64(1),
                    EwmaTtfbMs = r.GetInt64(2),
                    OkCount = r.GetInt64(3),
                    FailCount = r.GetInt64(4),
                    BytesTotal = r.GetInt64(5),
                    LastActiveMs = r.GetInt64(6),
                });
            return list;
        }
    }

    public void Upsert(ServerProfile p)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO server_profile(host, ewma_bps, ewma_ttfb_ms, ok_count, fail_count, bytes_total, last_active_ms)
                VALUES(@h,@b,@t,@ok,@f,@bytes,@last)
                ON CONFLICT(host) DO UPDATE SET
                    ewma_bps=@b, ewma_ttfb_ms=@t, ok_count=@ok, fail_count=@f, bytes_total=@bytes, last_active_ms=@last
                """;
            cmd.Parameters.AddValue("@h", p.Host);
            cmd.Parameters.AddValue("@b", p.EwmaBps);
            cmd.Parameters.AddValue("@t", p.EwmaTtfbMs);
            cmd.Parameters.AddValue("@ok", p.OkCount);
            cmd.Parameters.AddValue("@f", p.FailCount);
            cmd.Parameters.AddValue("@bytes", p.BytesTotal);
            cmd.Parameters.AddValue("@last", p.LastActiveMs);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteAll()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM server_profile";
            cmd.ExecuteNonQuery();
        }
    }
}

/// <summary>
/// v1.5.0 (G25): TLS client-certificate bindings. The PFX passphrase lives in the same
/// table (DB file is user-profile-private, 0600-equivalent on POSIX); it is never logged
/// and never returned over IPC — responses strip the column.
/// </summary>
public sealed class ClientCertRepository
{
    private readonly NovaDatabase _db;
    public ClientCertRepository(NovaDatabase db) => _db = db;

    public IReadOnlyList<ClientCertBinding> List()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, host_pattern, source, cert_path, key_path, store_name, thumbprint, enabled, created_at " +
                              "FROM client_cert ORDER BY host_pattern";
            using var r = cmd.ExecuteReader();
            var list = new List<ClientCertBinding>();
            while (r.Read())
                list.Add(new ClientCertBinding
                {
                    Id = r.GetInt64(0),
                    HostPattern = r.GetString(1),
                    Source = r.GetString(2),
                    CertPath = r.GetString(3),
                    KeyPath = r.GetString(4),
                    StoreName = r.GetString(5),
                    Thumbprint = r.GetString(6),
                    Enabled = r.GetInt64(7) != 0,
                    CreatedAt = DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture),
                });
            return list;
        }
    }

    /// <summary>Full row including the secret passphrase (engine-side only).</summary>
    public (ClientCertBinding Binding, string Passphrase)? Find(string host)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            // longest/most-specific match wins: exact > wildcard; order by length of pattern
            cmd.CommandText = "SELECT id, host_pattern, source, cert_path, key_path, store_name, thumbprint, enabled, created_at, passphrase " +
                              "FROM client_cert WHERE enabled=1 ORDER BY LENGTH(host_pattern) DESC";
            cmd.Parameters.Clear();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var pattern = r.GetString(1);
                if (!HostMatches(host, pattern)) continue;
                return (new ClientCertBinding
                {
                    Id = r.GetInt64(0),
                    HostPattern = pattern,
                    Source = r.GetString(2),
                    CertPath = r.GetString(3),
                    KeyPath = r.GetString(4),
                    StoreName = r.GetString(5),
                    Thumbprint = r.GetString(6),
                    Enabled = r.GetInt64(7) != 0,
                    CreatedAt = DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture),
                }, r.IsDBNull(9) ? "" : r.GetString(9));
            }
            return null;
        }
    }

    /// <summary>Passphrase stored for an exact host pattern (engine-side only, never over IPC).</summary>
    public string PassphraseFor(string hostPattern)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT passphrase FROM client_cert WHERE host_pattern=@p";
            cmd.Parameters.AddValue("@p", hostPattern);
            var v = cmd.ExecuteScalar();
            return v is string s ? s : "";
        }
    }

    /// <summary>Exact host ("a.com") or left-anchored wildcard ("*.a.com").</summary>
    public static bool HostMatches(string host, string pattern)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pattern)) return false;
        if (pattern.StartsWith("*."))
        {
            var suffix = pattern[1..]; // ".a.com"
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                   host.Length > suffix.Length; // at least one label before the dot
        }
        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    public long Save(ClientCertBinding b, string passphrase)
    {
        return _db.InTransaction(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO client_cert(host_pattern, source, cert_path, key_path, store_name, thumbprint, passphrase, enabled, created_at)
                VALUES(@h,@s,@c,@k,@st,@th,@p,@e,@ca)
                ON CONFLICT(host_pattern) DO UPDATE SET
                    source=@s, cert_path=@c, key_path=@k, store_name=@st, thumbprint=@th,
                    passphrase=@p, enabled=@e
                """;
            cmd.Parameters.AddValue("@h", b.HostPattern.Trim().ToLowerInvariant());
            cmd.Parameters.AddValue("@s", string.IsNullOrWhiteSpace(b.Source) ? "pfx" : b.Source);
            cmd.Parameters.AddValue("@c", b.CertPath ?? "");
            cmd.Parameters.AddValue("@k", b.KeyPath ?? "");
            cmd.Parameters.AddValue("@st", string.IsNullOrWhiteSpace(b.StoreName) ? "My" : b.StoreName);
            cmd.Parameters.AddValue("@th", (b.Thumbprint ?? "").Replace(":", "").Trim().ToUpperInvariant());
            cmd.Parameters.AddValue("@p", passphrase ?? "");
            cmd.Parameters.AddValue("@e", b.Enabled ? 1 : 0);
            cmd.Parameters.AddValue("@ca", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id FROM client_cert WHERE host_pattern=@h";
            return Convert.ToInt64(cmd.ExecuteScalar()!);
        });
    }

    public bool Delete(long id)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM client_cert WHERE id=@id";
            cmd.Parameters.AddValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }
}

/// <summary>v1.5.0 (G26): PGP public-key keyring used for metalink/sidecar verification.</summary>
public sealed class PgpKeyringRepository
{
    private readonly NovaDatabase _db;
    public PgpKeyringRepository(NovaDatabase db) => _db = db;

    public IReadOnlyList<PgpPublicKey> List()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, label, fingerprint, armored_key, created_at FROM pgp_keyring ORDER BY id";
            using var r = cmd.ExecuteReader();
            var list = new List<PgpPublicKey>();
            while (r.Read())
                list.Add(new PgpPublicKey
                {
                    Id = r.GetInt64(0),
                    Label = r.GetString(1),
                    Fingerprint = r.GetString(2),
                    ArmoredKey = r.GetString(3),
                    CreatedAt = DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture),
                });
            return list;
        }
    }

    public long Save(PgpPublicKey key)
    {
        return _db.InTransaction(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pgp_keyring(label, fingerprint, armored_key, created_at)
                VALUES(@l,@f,@k,@ca)
                ON CONFLICT(fingerprint) DO UPDATE SET label=@l, armored_key=@k
                """;
            cmd.Parameters.AddValue("@l", key.Label);
            cmd.Parameters.AddValue("@f", key.Fingerprint);
            cmd.Parameters.AddValue("@k", key.ArmoredKey);
            cmd.Parameters.AddValue("@ca", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id FROM pgp_keyring WHERE fingerprint=@f";
            return Convert.ToInt64(cmd.ExecuteScalar()!);
        });
    }

    public bool Delete(long id)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM pgp_keyring WHERE id=@id";
            cmd.Parameters.AddValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }
}
