using System.Security.Cryptography;

namespace Nova.Media;

/// <summary>
/// HLS AES-128 decryption (RFC 8216 §5.2): AES-128-CBC with PKCS7 padding.
/// IV = the tag's IV attribute when present, otherwise the media segment's
/// sequence number encoded as a big-endian 128-bit unsigned integer.
/// Only METHOD=AES-128 / KEYFORMAT=identity content is handled here — anything
/// else (SAMPLE-AES, Widevine/FairPlay/PlayReady containers) is DRM and is refused upstream.
/// </summary>
public static class HlsCrypt
{
    /// <summary>Maximum ciphertext held in memory for a single segment (TS segments are typically 1–10 MiB).</summary>
    public const int MaxInMemorySegmentBytes = 512 * 1024 * 1024;

    /// <summary>Parse an EXT-X-KEY IV attribute: "0x" + 32 hex chars → 16 bytes. Null/invalid → null (sequence fallback).</summary>
    public static byte[]? ParseIv(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        if (h.Length != 32) return null;
        var iv = new byte[16];
        for (var i = 0; i < 16; i++)
            if (!byte.TryParse(h.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out iv[i]))
                return null;
        return iv;
    }

    /// <summary>RFC 8216 IV fallback: the segment's sequence number as a 16-byte big-endian integer.</summary>
    public static byte[] IvFromSequence(long sequence)
    {
        var iv = new byte[16];
        for (var i = 15; i >= 8; i--)
        {
            iv[i] = (byte)(sequence & 0xFF);
            sequence >>= 8;
        }
        return iv;
    }

    /// <summary>
    /// Decrypt one AES-128-CBC segment and strip PKCS7 padding.
    /// Lenient on invalid padding (some packagers emit none/crypto padding); the bytes are
    /// returned unchanged in that case — matches ffmpeg's tolerance.
    /// </summary>
    public static byte[] DecryptSegment(ReadOnlySpan<byte> ciphertext, byte[] key, byte[] iv)
    {
        if (key.Length != 16) throw new CryptographicException("HLS AES-128 key must be 16 bytes.");
        if (iv.Length != 16) throw new CryptographicException("HLS AES-128 IV must be 16 bytes.");
        if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0)
            throw new CryptographicException($"Ciphertext length ({ciphertext.Length}) is not a multiple of the 16-byte AES block.");

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None; // strip manually for tolerance
        aes.Key = key;
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        var plain = dec.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);

        var pad = plain[^1];
        if (pad is >= 1 and <= 16 && pad <= plain.Length)
        {
            var ok = true;
            for (var i = plain.Length - pad; i < plain.Length; i++)
                if (plain[i] != pad) { ok = false; break; }
            if (ok) return plain[..^pad];
        }
        return plain; // non-conforming padding — keep payload bytes
    }

    /// <summary>Encrypt helper used by tests to build AES-128 fixtures (PKCS7).</summary>
    public static byte[] EncryptSegmentForTest(ReadOnlySpan<byte> plaintext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
    }
}
