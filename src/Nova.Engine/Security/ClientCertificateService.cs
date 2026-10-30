using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nova.Domain;
using Nova.Persistence;

namespace Nova.Engine.Security;

/// <summary>
/// v1.5.0 (G25): TLS client-certificate support. Bindings are matched per target host
/// inside the shared engine HttpClient via SslOptions.LocalCertificateSelectionCallback —
/// no handler rebuild needed when the user edits bindings (callback reads current set).
/// Sources: PFX/P12 file, PEM cert+key pair, or a user certificate store (by thumbprint).
/// </summary>
public sealed class ClientCertificateService
{
    private readonly ClientCertRepository _repo;
    private readonly object _reloadLock = new();
    private IReadOnlyList<(ClientCertBinding Binding, X509Certificate2 Cert)> _cache =
        Array.Empty<(ClientCertBinding, X509Certificate2)>();
    private bool _cacheLoaded;
    private long _loadedAtMs;
    private const long CacheTtlMs = 10_000;

    public ClientCertificateService(ClientCertRepository repo) => _repo = repo;

    /// <summary>Wire into any SocketsHttpHandler the engine builds.</summary>
    public void Configure(SocketsHttpHandler handler)
    {
        handler.SslOptions.LocalCertificateSelectionCallback = Select;
    }

    /// <summary>TLS callback: picks the certificate bound to the target host (if any).</summary>
    private X509Certificate? Select(object sender, string targetHost,
        X509CertificateCollection localCertificates, X509Certificate? remoteCertificate, string[] acceptableIssuers)
    {
        try
        {
            var cert = CertificateFor(targetHost);
            if (cert is null)
                EngineLog.Info($"client-cert: no binding for '{targetHost}' — continuing without one");
            return cert;
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"client-cert selection failed for '{targetHost}': {ex.Message}");
            return null;
        }
    }

    /// <summary>The loaded certificate matching the host, or null when none applies.</summary>
    public X509Certificate2? CertificateFor(string host)
    {
        foreach (var (binding, cert) in Current())
        {
            if (!binding.Enabled) continue;
            if (ClientCertRepository.HostMatches(host, binding.HostPattern))
                return cert;
        }
        return null;
    }

    /// <summary>Loads/refreshes bindings (10s TTL). Broken entries are logged + skipped, never fatal.</summary>
    private IReadOnlyList<(ClientCertBinding, X509Certificate2)> Current()
    {
        var now = Environment.TickCount64;
        lock (_reloadLock)
        {
            if (_cacheLoaded && now - _loadedAtMs < CacheTtlMs)
                return _cache;
        }
        var loaded = new List<(ClientCertBinding, X509Certificate2)>();
        foreach (var b in _repo.List())
        {
            if (!b.Enabled) continue;
            try
            {
                var cert = Load(b, _repo.PassphraseFor(b.HostPattern));
                if (cert is not null) loaded.Add((b, cert));
            }
            catch (Exception ex)
            {
                EngineLog.Warn($"client-cert '{b.HostPattern}' failed to load: {ex.Message}");
            }
        }
        lock (_reloadLock)
        {
            _cache = loaded;
            _cacheLoaded = true;
            _loadedAtMs = now;
        }
        return loaded;
    }

    /// <summary>Loads one binding into an X509Certificate2 (with private key where applicable).</summary>
    private static X509Certificate2? Load(ClientCertBinding b, string passphrase)
    {
        return b.Source.ToLowerInvariant() switch
        {
            "pfx" when File.Exists(b.CertPath) =>
                new X509Certificate2(b.CertPath, passphrase),
            "pem" when File.Exists(b.CertPath) =>
                WithWindowsUsableKey(X509Certificate2.CreateFromPemFile(b.CertPath,
                    string.IsNullOrEmpty(b.KeyPath) ? null : b.KeyPath)),
            "store" => FromStore(b),
            _ => null,
        };
    }

    /// <summary>T-026 (Windows): CreateFromPemFile yields an ephemeral private key,
    /// which SChannel refuses during TLS client authentication (the handshake dies
    /// with "unexpected EOF"). Round-tripping through PFX gives the key a handle
    /// SChannel can use. No-op on POSIX.</summary>
    private static X509Certificate2 WithWindowsUsableKey(X509Certificate2 cert)
    {
        if (!OperatingSystem.IsWindows() || !cert.HasPrivateKey) return cert;
        try
        {
            var pfx = cert.Export(X509ContentType.Pfx);
            var usable = new X509Certificate2(pfx);
            cert.Dispose();
            return usable;
        }
        catch
        {
            return cert; // best effort — hand back the original
        }
    }

    private static X509Certificate2? FromStore(ClientCertBinding b)
    {
        using var store = new X509Store(string.IsNullOrWhiteSpace(b.StoreName) ? StoreName.My.ToString() : b.StoreName,
            StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var thumb = (b.Thumbprint ?? "").Replace(":", "").Trim().ToUpperInvariant();
        if (thumb.Length == 0) return null;
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false);
        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>Validates a candidate binding before saving (returns a UI-safe error, or null on success).</summary>
    public string? Validate(ClientCertBinding b, string passphrase)
    {
        try
        {
            var cert = Load(b, passphrase);
            if (cert is null)
                return b.Source switch
                {
                    "pfx" => $"The PFX file was not found: {b.CertPath}",
                    "pem" => $"The PEM certificate was not found: {b.CertPath}",
                    "store" => $"No certificate with thumbprint {b.Thumbprint} in the '{b.StoreName}' store.",
                    _ => "Unknown certificate source.",
                };
            if (!cert.HasPrivateKey && b.Source is "pfx" or "pem")
                return "The certificate does not contain a private key — it cannot be used for TLS client auth.";
            return null;
        }
        catch (CryptographicException)
        {
            return "The certificate could not be loaded — for PFX check the passphrase.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>SHA-256 fingerprint of the certificate actually used, for UI confirmation.</summary>
    public static string? Sha256Thumbprint(X509Certificate2? cert)
    {
        if (cert is null) return null;
        return Convert.ToHexString(SHA256.HashData(cert.GetCertHash())).ToLowerInvariant();
    }
}
