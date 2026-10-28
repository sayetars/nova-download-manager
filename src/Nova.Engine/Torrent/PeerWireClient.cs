using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nova.Engine.Torrent;

/// <summary>
/// Peer wire protocol client (BEP-3): handshake + length-prefixed message loop, with the
/// extension handshake (BEP-10) and ut_metadata (BEP-9) for magnet metadata download.
/// One instance per TCP peer; owned by TorrentSession.
/// </summary>
public sealed class PeerWireClient : IAsyncDisposable
{
    public const int BlockSize = 16384;               // 16 KiB standard block
    public const int MaxPendingBlocks = 12;           // request pipeline depth per peer

    /// <summary>Reserved bits we advertise — v1.6.0: BEP-10 extension protocol
    /// (0x10 in byte 5) + BEP-5 DHT (0x01 in the last byte). We only claim bits we
    /// actually implement (no FAST/uTP claims) — honest advertisement.</summary>
    public static ReadOnlySpan<byte> OurReserved => new byte[8] { 0, 0, 0, 0, 0, 0x10, 0, 0x01 };

    // ------------------------------ v1.6.0: peer reserved-flag parsing (telemetry)
    /// <summary>Peer advertised BEP-5 DHT (0x01 in reserved byte 7).</summary>
    public static bool ParseSupportsDht(ReadOnlySpan<byte> reserved) => (reserved[7] & 0x01) != 0;
    /// <summary>Peer advertised BEP-10 extension protocol (0x10 in reserved byte 5).</summary>
    public static bool ParseSupportsExtensionProtocol(ReadOnlySpan<byte> reserved) => (reserved[5] & 0x10) != 0;
    /// <summary>Peer advertised BEP-6 FAST extensions (0x04 in reserved byte 7) — we parse but do not send.</summary>
    public static bool ParseSupportsFast(ReadOnlySpan<byte> reserved) => (reserved[7] & 0x04) != 0;

    public const byte MsgChoke = 0, MsgUnchoke = 1, MsgInterested = 2, MsgNotInterested = 3,
        MsgHave = 4, MsgBitfield = 5, MsgRequest = 6, MsgPiece = 7, MsgCancel = 8,
        MsgExtended = 20;

    private readonly TcpClient _tcp;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public byte[] InfoHash { get; }
    public byte[] PeerId { get; }
    public string Address { get; }
    /// <summary>Metadata size advertised via BEP-10 (-1 = we have no metadata).
    /// Seeds/sessions with a .torrent MUST set it so magnet-only peers can fetch it.</summary>
    public int MetadataSize { get; set; } = -1;
    public bool Connected { get; private set; }
    public bool AmChoked { get; private set; } = true;
    public bool PeerChoked { get; private set; } = true;
    public bool AmInterested { get; private set; }
    public bool PeerInterested { get; private set; }
    public bool[]? PeerBitfield { get; private set; }
    /// <summary>Extension ids the peer announced via BEP-10 handshake (name → its id).</summary>
    public IReadOnlyDictionary<string, int> PeerExtensions { get; private set; } =
        new Dictionary<string, int>();
    /// <summary>Our ut_metadata extension id (0 = peer does not support it).</summary>
    public int UtMetadataId { get; private set; }
    /// <summary>Our ut_pex extension id (0 = peer does not support PEX) — v1.4.1 BEP-11.</summary>
    public int UtPexId { get; private set; }
    public int PeerMetadataSize { get; private set; } = -1;
    /// <summary>Peer ids from the BEP-10 handshake for logging/telemetry (may be empty).</summary>
    public string? PeerClientName { get; private set; }
    /// <summary>v1.6.0: flags parsed from the peer's handshake reserved bytes.</summary>
    public bool PeerSupportsDht { get; private set; }
    /// <summary>v1.6.0: peer supports the BEP-10 extension protocol.</summary>
    public bool PeerSupportsExtensionProtocol { get; private set; }
    /// <summary>v1.6.0: peer advertises BEP-6 FAST extensions (parsed only).</summary>
    public bool PeerSupportsFast { get; private set; }
    /// <summary>Statistics.</summary>
    public long BytesDown { get; private set; }
    public long BytesUp { get; private set; }
    /// <summary>Peer's advertised request pipeline depth (BEP-10 reqq, clamped) — v1.4.1.</summary>
    public int PeerRequestQueueDepth { get; private set; } = MaxPendingBlocks;

    public PeerWireClient(TcpClient tcp, byte[] infoHash, byte[] ourPeerId)
    {
        _tcp = tcp;
        InfoHash = infoHash;
        PeerId = ourPeerId;
        Address = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public static PeerWireClient CreateConnected(TorrentPeer peer, byte[] infoHash, byte[] ourPeerId)
    {
        var tcp = new TcpClient();
        tcp.Connect(peer.Address, peer.Port);
        tcp.NoDelay = true;
        tcp.ReceiveTimeout = 90_000;
        tcp.SendTimeout = 30_000;
        return new PeerWireClient(tcp, infoHash, ourPeerId);
    }

    // ------------------------------------------------------------------ handshake
    /// <summary>v1.6.0: parse the peer's reserved bytes into telemetry flags.</summary>
    private void ParsePeerFlags(ReadOnlySpan<byte> reserved)
    {
        PeerSupportsDht = ParseSupportsDht(reserved);
        PeerSupportsExtensionProtocol = ParseSupportsExtensionProtocol(reserved);
        PeerSupportsFast = ParseSupportsFast(reserved);
    }
    /// <summary>Performs the two-way handshake. Returns false when info-hash mismatch
    /// (peer has a different torrent) — caller should drop the connection.</summary>
    public async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        _stream = _tcp.GetStream();

        var hello = new byte[68];
        hello[0] = 19;
        Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(hello, 1);
        OurReserved.CopyTo(hello.AsSpan(20));
        InfoHash.CopyTo(hello, 28);
        PeerId.CopyTo(hello, 48);
        await _stream.WriteAsync(hello, ct);

        // read 68-byte reply
        var reply = new byte[68];
        var got = 0;
        while (got < 68)
        {
            var n = await _stream.ReadAsync(reply.AsMemory(got), ct);
            if (n <= 0) return false;
            got += n;
        }
        if (reply[0] != 19 || Encoding.ASCII.GetString(reply, 1, 19) != "BitTorrent protocol")
            return false;
        var theirHash = new byte[20];
        Array.Copy(reply, 28, theirHash, 0, 20);
        if (!theirHash.AsSpan().SequenceEqual(InfoHash)) return false;

        ParsePeerFlags(reply.AsSpan(20, 8));
        Connected = true;
        // send BEP-10 extension handshake immediately (supports ut_metadata)
        var extHandshake = BuildExtensionHandshake();
        await SendMessageAsync(MsgExtended, 0, extHandshake, ct);
        return true;
    }

    private byte[] BuildExtensionHandshake()
    {
        // fixed extension ids (both directions agree on OUR ids for messages we receive)
        var m = BencValue.OfDict()
            .Set("ut_metadata", BencValue.Of(1))
            .Set("ut_pex", BencValue.Of(2));
        var dict = BencValue.OfDict()
            .Set("m", m)
            .Set("p", BencValue.Of((_tcp.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0))
            .Set("v", BencValue.Of(Nova.Domain.VersionInfo.PeerWireClient))
            .Set("reqq", BencValue.Of(PeerWireClient.MaxPendingBlocks));
        if (MetadataSize >= 0)
            dict.Set("metadata_size", BencValue.Of(MetadataSize));
        return dict.Encode();
    }

    /// <summary>Serves inbound peers on a listening socket: reads THEIR handshake first,
    /// then replies (this is how our seeder accepts connections).</summary>
    public static async Task<PeerWireClient> AcceptAsync(TcpClient tcp, byte[] infoHash,
        byte[] ourPeerId, int metadataSize, CancellationToken ct)
    {
        var client = new PeerWireClient(tcp, infoHash, ourPeerId)
        {
            MetadataSize = metadataSize,
        };
        var stream = tcp.GetStream();

        var hello = new byte[68];
        var got = 0;
        while (got < 68)
        {
            var n = await stream.ReadAsync(hello.AsMemory(got), ct);
            if (n <= 0) throw new IOException("peer closed before handshake");
            got += n;
        }
        if (hello[0] != 19 || Encoding.ASCII.GetString(hello, 1, 19) != "BitTorrent protocol")
            throw new IOException("not a BitTorrent handshake");
        var theirHash = new byte[20];
        Array.Copy(hello, 28, theirHash, 0, 20);
        if (!theirHash.AsSpan().SequenceEqual(infoHash))
            throw new IOException("info hash mismatch");
        client.ParsePeerFlags(hello.AsSpan(20, 8));

        var reply = new byte[68];
        reply[0] = 19;
        Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(reply, 1);
        OurReserved.CopyTo(reply.AsSpan(20));
        infoHash.CopyTo(reply, 28);
        ourPeerId.CopyTo(reply, 48);
        await stream.WriteAsync(reply, ct);

        client._stream = stream;
        client.Connected = true;
        await client.SendMessageAsync(MsgExtended, 0, client.BuildExtensionHandshake(), ct);
        return client;
    }

    // ------------------------------------------------------------------ messages
    /// <summary>Extension messages may be sent externally (magnet fetch, PEX broadcast,
    /// tests exercising the wire).</summary>
    public Task SendMessageInternalAsync(byte id, int extendedId, ReadOnlyMemory<byte> payload,
        CancellationToken ct) => SendMessageAsync(id, extendedId, payload, ct);

    private async Task SendMessageAsync(byte id, int extraPrefix, ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("not connected");
        // regular message: [len][id][payload]  |  extension message: [len][id][extId][payload]
        var len = 1 + (extraPrefix >= 0 ? 1 : 0) + payload.Length;
        var buffer = new byte[4 + len];
        BinaryPrimitives.WriteInt32BigEndian(buffer, len);
        var off = 4;
        buffer[off++] = id;
        if (extraPrefix >= 0) buffer[off++] = (byte)extraPrefix;
        payload.Span.CopyTo(buffer.AsSpan(off));
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(buffer, ct);
            BytesUp += buffer.Length;
        }
        finally { _writeLock.Release(); }
    }

    public async Task SendKeepaliveAsync(CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("not connected");
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(new byte[4], ct);
        }
        finally { _writeLock.Release(); }
    }

    public Task SendInterestedAsync(CancellationToken ct) { AmInterested = true; return SendMessageAsync(MsgInterested, -1, default, ct); }
    public Task SendNotInterestedAsync(CancellationToken ct) { AmInterested = false; return SendMessageAsync(MsgNotInterested, -1, default, ct); }
    public Task SendHaveAsync(int piece, CancellationToken ct) =>
        SendMessageAsync(MsgHave, -1, Int32BE(piece), ct);
    public Task SendChokeAsync(CancellationToken ct) { PeerChoked = true; return SendMessageAsync(MsgChoke, -1, default, ct); }
    public Task SendUnchokeAsync(CancellationToken ct) { PeerChoked = false; return SendMessageAsync(MsgUnchoke, -1, default, ct); }

    public Task SendBitfieldAsync(bool[] bits, CancellationToken ct)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return SendMessageAsync(MsgBitfield, -1, bytes, ct);
    }

    public Task SendRequestAsync(int index, int begin, int length, CancellationToken ct)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload, index);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), begin);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), length);
        return SendMessageAsync(MsgRequest, -1, payload, ct);
    }

    public Task SendCancelAsync(int index, int begin, int length, CancellationToken ct)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload, index);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), begin);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), length);
        return SendMessageAsync(MsgCancel, -1, payload, ct);
    }

    public Task SendPieceAsync(int index, int begin, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, index);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), begin);
        // combine to avoid two writes (callers hold the data already)
        var buffer = new byte[8 + data.Length];
        header.CopyTo(buffer, 0);
        data.Span.CopyTo(buffer.AsSpan(8));
        return SendMessageAsync(MsgPiece, -1, buffer, ct);
    }

    // ------------------------------------------------------------------ inbound
    /// <summary>A decoded inbound message from a peer.</summary>
    public readonly record struct PeerMessage(byte Id, int ExtendedId, byte[] Payload)
    {
        public int PieceIndex => BinaryPrimitives.ReadInt32BigEndian(Payload);
        public int BlockBegin => Payload.Length >= 8 ? BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4)) : 0;
        /// <summary>For REQUEST messages: the block length field (payload[8..12]).</summary>
        public int RequestLength => Payload.Length >= 12 ? BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(8)) : 0;
        public ReadOnlySpan<byte> Block => Payload.AsSpan(8);
        public int HavePiece => BinaryPrimitives.ReadInt32BigEndian(Payload);
    }

    public async Task<PeerMessage?> ReadMessageAsync(CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("not connected");
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(lenBuf, ct)) return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (length == 0) return new PeerMessage(255, 0, Array.Empty<byte>()); // keepalive
        if (length is < 0 or > 4 * 1024 * 1024)
            throw new IOException($"peer message length {length} out of bounds");

        var body = new byte[length];
        if (!await ReadExactAsync(body, ct)) return null;

        var id = body[0];
        var extendedId = 0;
        var payloadStart = 1;
        if (id == MsgExtended)
        {
            extendedId = body[1];
            payloadStart = 2;
        }
        var payload = body[payloadStart..];
        ApplyState(id, extendedId, payload);
        return new PeerMessage(id, extendedId, payload);
    }

    private void ApplyState(byte id, int extendedId, byte[] payload)
    {
        switch (id)
        {
            case MsgChoke: AmChoked = true; break;
            case MsgUnchoke: AmChoked = false; break;
            case MsgInterested: PeerInterested = true; break;
            case MsgNotInterested: PeerInterested = false; break;
            case MsgHave:
                if (PeerBitfield is not null && payload.Length >= 4)
                {
                    var idx = BinaryPrimitives.ReadInt32BigEndian(payload);
                    if (idx >= 0 && idx < PeerBitfield.Length) PeerBitfield[idx] = true;
                }
                break;
            case MsgBitfield:
                {
                    var count = PeerBitfield?.Length ?? 0;
                    if (count > 0)
                    {
                        var bits = new bool[count];
                        for (var i = 0; i < count && (i / 8) < payload.Length; i++)
                            bits[i] = (payload[i / 8] & (0x80 >> (i % 8))) != 0;
                        PeerBitfield = bits;
                    }
                    break;
                }
            case MsgPiece:
                break; // wire bytes already counted in ReadExactAsync
            case MsgExtended:
                if (payload.Length > 0 && extendedId == 0)
                {
                    // extension handshake — parse "m" and metadata_size
                    try
                    {
                        var dict = BencValue.Decode(payload);
                        if (dict.Get("m") is { } m)
                        {
                            var exts = new Dictionary<string, int>();
                            foreach (var kv in m.AsDictionary)
                            {
                                var name = Encoding.UTF8.GetString(kv.Key);
                                exts[name] = (int)kv.Value.AsInteger;
                            }
                            PeerExtensions = exts;
                            if (exts.TryGetValue("ut_metadata", out var utm)) UtMetadataId = utm;
                            if (exts.TryGetValue("ut_pex", out var utp)) UtPexId = utp;
                        }
                        if (dict.Get("metadata_size") is { } ms)
                            PeerMetadataSize = (int)ms.AsInteger;
                        if (dict.Get("v")?.AsUtf8 is { Length: > 0 } v)
                            PeerClientName = v;
                        if (dict.Get("reqq")?.AsInteger is { } rq && rq > 0)
                            PeerRequestQueueDepth = (int)Math.Min(rq, 64);
                    }
                    catch (InvalidDataException) { /* peer sent junk — ignore */ }
                }
                break;
        }
    }

    /// <summary>Caller sets expected piece count before handshake reply processing.</summary>
    public void InitBitfield(int pieceCount) => PeerBitfield = new bool[pieceCount];

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var n = await _stream!.ReadAsync(buffer.AsMemory(got), ct);
            if (n <= 0) return false;
            got += n;
        }
        BytesDown += buffer.Length;
        return true;
    }

    private static byte[] Int32BE(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        return b;
    }

    public async ValueTask DisposeAsync()
    {
        try { _tcp.Close(); } catch { /* already closed */ }
        _writeLock.Dispose();
        await Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------- BEP-9
/// <summary>ut_metadata helpers (BEP-9): request/data/reject pieces of the info dict.</summary>
public static class UtMetadata
{
    public const int MetadataPiece = 16384;

    public static byte[] BuildRequest(int piece) =>
        BencValue.OfDict().Set("msg_type", BencValue.Of(0)).Set("piece", BencValue.Of(piece)).Encode();

    public static byte[] BuildData(int piece, byte[] infoDict, int totalSize)
    {
        var offset = piece * MetadataPiece;
        var len = Math.Min(MetadataPiece, totalSize - offset);
        if (len <= 0) throw new ArgumentOutOfRangeException(nameof(piece));
        var header = BencValue.OfDict()
            .Set("msg_type", BencValue.Of(1))
            .Set("piece", BencValue.Of(piece))
            .Set("total_size", BencValue.Of(totalSize))
            .Encode();
        var msg = new byte[header.Length + len];
        header.CopyTo(msg, 0);
        Array.Copy(infoDict, offset, msg, header.Length, len);
        return msg;
    }

    /// <summary>Parses a ut_metadata data message payload → (infoDictSlice) or null when reject.</summary>
    public static byte[]? ParseData(byte[] payload, int piece)
    {
        // the message is [bencoded header dict][raw metadata bytes] — split grammar-aware
        var prefixLen = BencValue.MeasureFirstValue(payload);
        if (prefixLen <= 0 || prefixLen >= payload.Length) return null;
        var dict = BencValue.Decode(payload.AsSpan(0, prefixLen).ToArray());
        var msgType = dict.Get("msg_type")?.AsInteger ?? -1;
        if (msgType == 2) return null; // reject
        if (msgType != 1) return null;
        var p = dict.Get("piece")?.AsInteger ?? -1;
        if (p != piece) return null;
        return payload[prefixLen..];
    }
}
