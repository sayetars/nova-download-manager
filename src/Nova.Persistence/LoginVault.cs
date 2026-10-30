using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace Nova.Persistence;

public sealed record VaultEntry(long Id, string Host, string Realm, string UserName, string Password, string Scheme, DateTimeOffset CreatedAt);

/// <summary>
/// Site logins vault (IDM parity). Passwords are encrypted at rest with
/// AES-256-GCM; the key is a random 32-byte file (0600) inside the engine data
/// directory — an attacker needs BOTH the DB and the key file. On Windows the
/// data dir is under the user profile (ACL-protected); on Linux it is 0700.
/// </summary>
public sealed class LoginVault
{
    private readonly NovaDatabase _db;
    private readonly byte[] _key;

    public LoginVault(NovaDatabase db, string dataDir)
    {
        _db = db;
        Directory.CreateDirectory(dataDir);
        var keyPath = Path.Combine(dataDir, "vault.key");
        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
            if (_key.Length != 32) _key = DeriveFallback(keyPath);
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, _key);
            TrySetOwnerOnly(keyPath);
        }
    }

    private static void TrySetOwnerOnly(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* Windows: profile ACLs apply */ }
    }

    private static byte[] DeriveFallback(string keyPath) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(keyPath + "|nova-vault"));

    private string Seal(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        using var aes = new AesGcm(_key, 16);
        var ct = new byte[Encoding.UTF8.GetByteCount(plain)];
        var tag = new byte[16];
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plain), ct, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(ct).ToArray());
    }

    private string Unseal(string sealed64)
    {
        var raw = Convert.FromBase64String(sealed64);
        var nonce = raw[..12];
        var tag = raw[12..28];
        var ct = raw[28..];
        using var aes = new AesGcm(_key, 16);
        var plaintext = new byte[ct.Length];
        aes.Decrypt(nonce, ct, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    public void Save(string host, string realm, string userName, string password, string scheme = "basic")
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(userName)) return;
        _db.InTransaction<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO login_vault(host, realm, user_name, secret, scheme, created_at)
                VALUES(@h,@r,@u,@s,@sc,@t)
                ON CONFLICT(host, realm, user_name) DO UPDATE SET
                    secret=@s, scheme=@sc, created_at=@t
                """;
            cmd.Parameters.AddValue("@h", host.ToLowerInvariant());
            cmd.Parameters.AddValue("@r", realm ?? "");
            cmd.Parameters.AddValue("@u", userName);
            cmd.Parameters.AddValue("@s", Seal(password));
            cmd.Parameters.AddValue("@sc", scheme);
            cmd.Parameters.AddValue("@t", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    public void Delete(long id)
    {
        _db.InTransaction<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM login_vault WHERE id=@id";
            cmd.Parameters.AddValue("@id", id);
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    /// <summary>
    /// Best entry for a URL or host (exact host match wins, then suffix match).
    /// v1.8.0 (IDM "Site Logins" parity — different logins for different directories
    /// of the same site): an entry stored as "host/path" matches by URL prefix —
    /// the LONGEST matching prefix wins over plain-host entries.
    /// </summary>
    public VaultEntry? Find(string urlOrHost)
    {
        string host;
        var isUrl = Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri);
        host = isUrl ? uri.Host : urlOrHost;
        var all = List();

        // prefix entries ("example.com/private/") — longest URL-prefix match wins
        VaultEntry? bestPrefix = null;
        var bestLen = -1;
        if (isUrl)
        {
            var noQuery = uri.GetLeftPart(UriPartial.Path);
            foreach (var v in all)
            {
                if (!v.Host.Contains('/')) continue;
                foreach (var scheme in new[] { "http://", "https://" })
                {
                    var prefix = scheme + v.Host.TrimEnd('/');
                    if (noQuery.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && (noQuery.Length == prefix.Length || noQuery[prefix.Length] is '/' or '?')
                        && v.Host.Length > bestLen)
                    {
                        bestPrefix = v;
                        bestLen = v.Host.Length;
                    }
                }
            }
        }

        return bestPrefix
            ?? all.FirstOrDefault(v => v.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(v => host.EndsWith("." + v.Host, StringComparison.OrdinalIgnoreCase));
    }

    public List<VaultEntry> List()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, host, realm, user_name, secret, scheme, created_at FROM login_vault ORDER BY host, user_name";
            using var r = cmd.ExecuteReader();
            var list = new List<VaultEntry>();
            while (r.Read())
            {
                string pass;
                try { pass = Unseal(r.GetString(4)); }
                catch (CryptographicException) { pass = ""; }  // key rotated → entry unreadable
                list.Add(new VaultEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), pass, r.GetString(5), DateTimeOffset.Parse(r.GetString(6))));
            }
            return list;
        }
    }

    /// <summary>Never expose secrets through IPC — list metadata only.</summary>
    public List<object> ListSafe() =>
        List().Select(v => (object)new { id = v.Id, host = v.Host, realm = v.Realm, user = v.UserName, scheme = v.Scheme, created_at = v.CreatedAt }).ToList();
}
