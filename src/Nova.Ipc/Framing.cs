using System.Buffers.Binary;
using System.Text;

namespace Nova.Ipc;

/// <summary>
/// Frame codec: 4-byte little-endian payload length, then that many UTF-8 JSON bytes.
/// Frame boundaries are the message boundaries — no newline splitting, no partial reads leaked.
/// </summary>
public static class Framing
{
    public const int PrefixBytes = 4;

    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[PrefixBytes];
        if (!await ReadExactAsync(stream, prefix, ct)) return null;

        var len = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (len is < 0 or > Protocol.MaxFrameBytes)
            throw new IpcFramingException($"Frame length {len} out of bounds (0..{Protocol.MaxFrameBytes})");

        if (len == 0) return Array.Empty<byte>();
        var payload = new byte[len];
        if (!await ReadExactAsync(stream, payload, ct)) return null;
        return payload;
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > Protocol.MaxFrameBytes)
            throw new IpcFramingException($"Payload too large: {payload.Length}");
        var prefix = new byte[PrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var off = 0;
        while (off < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(off), ct);
            if (n <= 0) return false; // peer closed
            off += n;
        }
        return true;
    }

    public static byte[] Encode(Envelope env) => Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(env, Protocol.Json));

    public static Envelope? Decode(byte[]? frame)
    {
        if (frame is null || frame.Length == 0) return null;
        var env = System.Text.Json.JsonSerializer.Deserialize<Envelope>(frame, Protocol.Json);
        return env is { V: <= Protocol.Version } and { V: > 0 } ? env : null;
    }
}

public sealed class IpcFramingException : Exception
{
    public IpcFramingException(string message) : base(message) { }
}
