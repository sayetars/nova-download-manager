using System.Buffers.Binary;

namespace Nova.Ipc;

/// <summary>
/// Length-prefixed JSON framing shared by every transport.
/// Wire format: [4-byte big-endian payload length][UTF-8 JSON].
/// Max frame 8 MiB — enough for a 10k-task list snapshot, small enough
/// to stop a corrupted stream from exhausting memory.
/// </summary>
public static class Frame
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    public static void Write(Stream stream, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"frame {payload.Length} > {MaxFrameBytes}");
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, payload.Length);
        stream.Write(len);
        stream.Write(payload);
        stream.Flush();
    }

    /// <summary>Returns null on orderly EOF; throws on truncated/corrupt stream.</summary>
    public static byte[]? Read(Stream stream, CancellationToken ct = default)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        if (!ReadExact(stream, lenBuf, ct)) return null;
        var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < 0 || len > MaxFrameBytes)
            throw new IOException($"frame length out of range: {len}");
        var payload = new byte[len];
        if (len > 0 && !ReadExact(stream, payload, ct))
            throw new EndOfStreamException("connection closed mid-frame");
        return payload;
    }

    private static bool ReadExact(Stream s, Span<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            var n = s.Read(buffer[total..]);
            if (n <= 0) return false; // EOF anywhere = failure; caller decides null vs throw
            total += n;
        }
        return true;
    }
}

/// <summary>A duplex byte pipe with clean async disposal, transport agnostic.</summary>
public interface ITransportConnection : IAsyncDisposable
{
    Stream InputStream { get; }
    Stream OutputStream { get; }
    string Describe { get; }
}

/// <summary>Server-side acceptor for a given transport (named pipe / UDS / stdio).</summary>
public interface ITransportServer : IAsyncDisposable
{
    /// <summary>Returns null when the server stops accepting.</summary>
    Task<ITransportConnection?> AcceptAsync(CancellationToken ct);
    string Endpoint { get; }
}
