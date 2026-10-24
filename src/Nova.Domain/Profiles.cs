using System.Text.Json.Serialization;

namespace Nova.Domain;

/// <summary>
/// v1.5.0 (G27): one row of per-server performance history. EWMA speed/latency are
/// recomputed in-memory on every sample and persisted periodically; the row is the
/// restart-stable snapshot the UI reads.
/// </summary>
public sealed class ServerProfile
{
    /// <summary>Normalized host (lowercase, no port).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Exponentially-weighted moving average throughput, bytes/sec (α = 0.25).</summary>
    public long EwmaBps { get; set; }

    /// <summary>EWMA time-to-first-byte in milliseconds (probe + first byte of ranged GETs).</summary>
    public long EwmaTtfbMs { get; set; }

    /// <summary>Successful requests (probe/slice) served by this host.</summary>
    public long OkCount { get; set; }

    /// <summary>Failed requests (transport errors, 4xx/5xx on probe/slice).</summary>
    public long FailCount { get; set; }

    /// <summary>Total bytes actually fetched from this host since tracking began.</summary>
    public long BytesTotal { get; set; }

    /// <summary>Epoch-ms of the last sample recorded (0 = never active this install).</summary>
    public long LastActiveMs { get; set; }

    [JsonIgnore] public long SampleCount => OkCount + FailCount;

    [JsonIgnore] double SuccessRate => SampleCount == 0 ? 1.0 : (double)OkCount / SampleCount;

    /// <summary>Higher is better; blends throughput, reliability and latency. 0 for unknown hosts.</summary>
    [JsonIgnore] public double Score =>
        SampleCount == 0 ? 0
            : Math.Max(0, EwmaBps) * SuccessRate * SuccessRate /
              (1.0 + Math.Max(0, EwmaTtfbMs) / 1000.0);
}

/// <summary>v1.5.0 (G25): TLS client-certificate binding for a host pattern.</summary>
public sealed class ClientCertBinding
{
    public long Id { get; set; }

    /// <summary>Exact host ("example.com") or wildcard ("*.example.com").</summary>
    public string HostPattern { get; set; } = string.Empty;

    /// <summary>pfx | pem | store.</summary>
    public string Source { get; set; } = "pfx";

    /// <summary>Path to .pfx/.p12 (source=pfx) or certificate .pem/.crt (source=pem).</summary>
    public string CertPath { get; set; } = string.Empty;

    /// <summary>Path to the private-key PEM (source=pem only).</summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>Windows/macOS certificate store name (source=store, e.g. "My").</summary>
    public string StoreName { get; set; } = "My";

    /// <summary>SHA-1 thumbprint to find in the store (source=store) or to identify PFX/PEM content.</summary>
    public string Thumbprint { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Passphrase is stored OUT of this object (kept only in the secret vault).</summary>
    [JsonIgnore] public bool NeedsPassphrase => Source == "pfx";
}

/// <summary>v1.5.0 (G26): an imported PGP public key used for metalink/sidecar signature checks.</summary>
public sealed class PgpPublicKey
{
    public long Id { get; set; }

    /// <summary>Human label chosen by the user ("Debian release key").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Uppercase SHA-1 fingerprint (40 hex) of the primary key.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Full ASCII-armored public key block ("-----BEGIN PGP PUBLIC KEY BLOCK-----…").</summary>
    public string ArmoredKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
