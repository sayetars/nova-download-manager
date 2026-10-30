using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Nova.Engine;

/// <summary>
/// Algo-qualified integrity verification (v1.3.0): whole-file hashes ("sha256:hex",
/// "sha1:hex", "md5:hex", "crc32:hex") + Metalink piece hashes with repair.
/// CRC32 is implemented inline (no external dependency) for SFV sidecars.
/// </summary>
public static class Hashing
{
    public static readonly string[] SupportedAlgos = { "sha256", "sha1", "md5", "crc32" };

    public static bool IsSupported(string? algo) =>
        algo is not null && SupportedAlgos.Contains(algo.ToLowerInvariant());

    /// <summary>Parses "algo:hex" (or bare hex → sha256 for back-compat). Null when malformed.</summary>
    public static (string Algo, string Hex)? ParseQualified(string? qualified)
    {
        if (string.IsNullOrWhiteSpace(qualified)) return null;
        var v = qualified.Trim();
        var idx = v.IndexOf(':');
        if (idx > 0)
        {
            var algo = v[..idx].Trim().ToLowerInvariant();
            var hex = v[(idx + 1)..].Trim();
            if (IsSupported(algo) && hex.Length > 0) return (algo, hex);
            return null;
        }
        // bare hex: infer from length
        return v.Length switch
        {
            64 => ("sha256", v),
            40 => ("sha1", v),
            32 => ("md5", v),
            8 => ("crc32", v),
            _ => null,
        };
    }

    public static async Task<string> ComputeAsync(string filePath, string algo, CancellationToken ct)
    {
        // FileShare.ReadWrite (T-026): verification runs while the DownloadDataFile
        // writer still holds the temp open. On Windows a plain FileShare.Read open
        // conflicts with the existing Write handle ("being used by another process")
        // and sends every hash-verified download into the retry loop. The write side
        // is already quiescent at verify time — all segments are done.
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeAsync(fs, algo, ct);
    }

    public static async Task<string> ComputeAsync(Stream stream, string algo, CancellationToken ct)
    {
        switch (algo.ToLowerInvariant())
        {
            case "sha256":
                using (var sha = SHA256.Create())
                    return Convert.ToHexString(await sha.ComputeHashAsync(stream, ct)).ToLowerInvariant();
            case "sha1":
                using (var sha = SHA1.Create())
                    return Convert.ToHexString(await sha.ComputeHashAsync(stream, ct)).ToLowerInvariant();
            case "md5":
                using (var md5 = MD5.Create())
                    return Convert.ToHexString(await md5.ComputeHashAsync(stream, ct)).ToLowerInvariant();
            case "crc32":
                var crc = new Crc32();
                var buf = new byte[1024 * 64];
                int n;
                while ((n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                    crc.Update(buf.AsSpan(0, n));
                return crc.Hex;
            default:
                throw new NotSupportedException($"hash algo '{algo}'");
        }
    }

    /// <summary>Verifies a whole file against a qualified requirement. Returns actual hex.</summary>
    public static async Task<(bool Ok, string Actual)> VerifyFileAsync(string path, string algo, string expectedHex, CancellationToken ct)
    {
        var actual = await ComputeAsync(path, algo, ct);
        return (string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase), actual);
    }
}

/// <summary>Standard CRC-32 (IEEE 802.3, reflected, poly 0xEDB88320) — SFV compatible.</summary>
public sealed class Crc32
{
    private static readonly uint[] Table = BuildTable();
    private uint _crc = 0xFFFFFFFFu;

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            _crc = Table[(_crc ^ b) & 0xFF] ^ (_crc >> 8);
    }

    public uint Value => _crc ^ 0xFFFFFFFFu;
    public string Hex => Value.ToString("x8");
}

/// <summary>Metalink piece-hash descriptor (persisted as JSON in download_task.piece_hashes).</summary>
public sealed class PieceHashInfo
{
    [JsonPropertyName("algo")] public string Algo { get; set; } = "sha1";
    [JsonPropertyName("size")] public long PieceSize { get; set; }
    [JsonPropertyName("hashes")] public List<string> Hashes { get; set; } = new();

    public static PieceHashInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var info = System.Text.Json.JsonSerializer.Deserialize<PieceHashInfo>(json);
            return info is { PieceSize: > 0, Hashes.Count: > 0 } && Hashing.IsSupported(info.Algo) ? info : null;
        }
        catch { return null; }
    }

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);
}
