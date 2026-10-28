using System.Security.Cryptography;
using Nova.Core.Models;

namespace Nova.Engine;

/// <summary>
/// Hash verification: MD5 / SHA-1 / SHA-256 / SHA-512 / CRC32, formats
/// "algo:hex". Used post-download and by Metalink PGP workflows.
/// </summary>
public static class ChecksumVerifier
{
    public static async Task<string?> ComputeAsync(string path, string algo, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        HashAlgorithm hash = algo.ToLowerInvariant() switch
        {
            "md5" => MD5.Create(),
            "sha1" or "sha-1" => SHA1.Create(),
            "sha256" or "sha-256" => SHA256.Create(),
            "sha512" or "sha-512" => SHA512.Create(),
            _ => throw new NotSupportedException($"hash algo {algo} not supported")
        };
        using (hash)
        {
            var digest = await hash.ComputeHashAsync(fs, ct);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
    }

    /// <summary>Streaming CRC32 for legacy checksum formats.</summary>
    public static async Task<uint> Crc32Async(string path, CancellationToken ct = default)
    {
        var table = BuildCrcTable();
        uint crc = 0xFFFFFFFF;
        var buf = new byte[1 << 20];
        await using var fs = File.OpenRead(path);
        int n;
        while ((n = await fs.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            for (var i = 0; i < n; i++)
                crc = (crc >> 8) ^ table[(crc ^ buf[i]) & 0xFF];
        }
        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static async Task<bool> VerifyAsync(DownloadTask task, string filePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(task.ExpectedHash)) return true;
        var parts = task.ExpectedHash.Split(':', 2);
        if (parts.Length != 2) return true;
        var algo = parts[0].Trim().ToLowerInvariant();
        var expected = parts[1].Trim().ToLowerInvariant();

        string actual;
        if (algo == "crc32")
        {
            var v = await Crc32Async(filePath, ct);
            actual = v.ToString("x8");
        }
        else
        {
            var digest = algo switch
            {
                "md5" => MD5.HashData(await File.ReadAllBytesAsync(filePath, ct)),
                "sha-1" or "sha1" => SHA1.HashData(await File.ReadAllBytesAsync(filePath, ct)),
                "sha-256" or "sha256" => SHA256.HashData(await File.ReadAllBytesAsync(filePath, ct)),
                "sha-512" or "sha512" => SHA512.HashData(await File.ReadAllBytesAsync(filePath, ct)),
                _ => throw new NotSupportedException(algo)
            };
            actual = Convert.ToHexString(digest).ToLowerInvariant();
        }
        task.VerifiedHash = $"{algo}:{actual}";
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}

file static class HexExt
{
    public static string ToHexStringLower(this byte[] b)
        => Convert.ToHexString(b).ToLowerInvariant();
}
