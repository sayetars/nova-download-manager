using Microsoft.Data.Sqlite;
using Nova.Domain;

namespace Nova.Persistence;

/// <summary>
/// v1.6.0: SQLite-backed DHT peer-store persistence (schema v11). Replace-all semantics:
/// each save rewrites the bounded row set inside one transaction — the store is small
/// (≤ 64 peers per info-hash) so a full swap is cheaper and simpler than diffing.
/// Expired rows are filtered on load; hard row cap guards the file size.
/// </summary>
public sealed class DhtPeerStoreDb
{
    public const int MaxRows = 4000;

    private readonly SqliteConnection _conn;
    private readonly object _txLock;

    public DhtPeerStoreDb(NovaDatabase db)
    {
        _conn = db.Connection;
        _txLock = new();
    }

    /// <summary>Loads all unexpired rows.</summary>
    public IReadOnlyList<PeerStoreEntry> LoadAll()
    {
        lock (_txLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT infohash, compact, expires_at FROM dht_peer_store";
            using var r = cmd.ExecuteReader();
            var now = DateTime.UtcNow;
            var result = new List<PeerStoreEntry>();
            while (r.Read())
            {
                var exp = ParseUtc(r.GetString(2));
                if (exp < now) continue;
                var compact = (byte[])r[1];
                if (compact.Length != 6) continue;
                result.Add(new PeerStoreEntry(r.GetString(0), compact, exp));
            }
            return result;
        }
    }

    /// <summary>Replaces the stored set with <paramref name="entries"/> atomically.</summary>
    public void SaveAll(IReadOnlyList<PeerStoreEntry> entries)
    {
        var now = DateTime.UtcNow;
        var rows = entries
            .Where(e => e.ExpiresAt >= now && e.Compact is { Length: 6 })
            .Take(MaxRows)
            .ToList();
        lock (_txLock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM dht_peer_store";
                del.ExecuteNonQuery();
            }
            using var ins = _conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText =
                "INSERT OR IGNORE INTO dht_peer_store (infohash, compact, expires_at) VALUES ($h, $c, $e)";
            var ph = ins.Parameters.AddValue("$h", null!);
            var pc = ins.Parameters.AddValue("$c", null!);
            var pe = ins.Parameters.AddValue("$e", null!);
            foreach (var e in rows)
            {
                ph.Value = e.InfoHashHex;
                pc.Value = e.Compact;
                pe.Value = FormatUtc(e.ExpiresAt);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static string FormatUtc(DateTime t) =>
        t.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime() : DateTime.MinValue;
}
