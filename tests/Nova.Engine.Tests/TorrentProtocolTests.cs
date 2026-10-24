using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Nova.Engine.Torrent;
using Xunit;
using static Nova.Engine.Torrent.BencValue;

namespace Nova.Engine.Tests;

[Xunit.Collection("TorrentSerial")]
/// <summary>
/// v1.4.0 BitTorrent core — protocol layer: bencode codec, metainfo, magnet,
/// trackers (HTTP BEP-3 + UDP BEP-15), DHT KRPC, piece picker, extension framing.
/// Network loops use loopback only.
/// </summary>
public class TorrentProtocolTests
{
    // ------------------------------------------------------------------ bencode
    [Fact]
    public void Bencode_Integer_Round_Trip()
    {
        foreach (var v in new long[] { 0, 1, -1, 42, int.MaxValue, long.MaxValue, long.MinValue + 1 })
        {
            var enc = Of(v).Encode();
            Assert.Equal(Decode(enc).AsInteger, v);
        }
    }

    [Fact]
    public void Bencode_Known_Vectors()
    {
        // canonical examples from BEP-3
        Assert.Equal("i42e", System.Text.Encoding.ASCII.GetString(Of(42).Encode()));
        Assert.Equal(42, Decode("i42e"u8).AsInteger);
        Assert.Equal("spam", Decode("4:spam"u8).AsUtf8);
        var list = Decode("l4:spam4:eggsi-3ee"u8);
        Assert.Equal(3, list.AsList.Count);
        Assert.Equal("spam", list.AsList[0].AsUtf8);
        Assert.Equal("eggs", list.AsList[1].AsUtf8);
        Assert.Equal(-3, list.AsList[2].AsInteger);
        var dict = Decode("d3:bar4:spam3:fooi42ee"u8);
        Assert.Equal("spam", dict.Get("bar")!.AsUtf8);
        Assert.Equal(42, dict.Get("foo")!.AsInteger);
    }

    [Fact]
    public void Bencode_Binary_Strings_Survive_Round_Trip()
    {
        var blob = RandomNumberGenerator.GetBytes(64);   // arbitrary binary — must NOT be UTF-8
        var dict = OfDict().Set(blob, Of(blob));
        var decoded = Decode(dict.Encode());
        var value = decoded.Get(blob);
        Assert.NotNull(value);
        Assert.Equal(blob, value!.AsString);
    }

    [Fact]
    public void Bencode_Rejects_Trailing_Garbage()
    {
        Assert.Throws<InvalidDataException>(() => Decode("i42eXX"u8));
        Assert.Throws<InvalidDataException>(() => Decode("4:spa"u8));
        Assert.Throws<InvalidDataException>(() => Decode("i4x2e"u8));
    }

    [Fact]
    public void Bencode_Dictionary_Keys_Are_Sorted_On_Encode()
    {
        var dict = OfDict().Set("zz", Of(1)).Set("aa", Of(2));
        var text = System.Text.Encoding.ASCII.GetString(dict.Encode());
        Assert.StartsWith("d2:aa", text);
        Assert.True(text.IndexOf("2:aa", StringComparison.Ordinal) < text.IndexOf("2:zz", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ metainfo
    private static (string Path, byte[] Payload, TorrentMeta Meta) MakeTorrent(
        long size = 700_000, long pieceLength = 65536, string[]? trackers = null)
    {
        var payload = RandomNumberGenerator.GetBytes((int)size);
        var path = Path.Combine(Path.GetTempPath(), "nova-bt-test-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, payload);
        trackers ??= new[] { "http://127.0.0.1:1/announce" };
        var raw = TorrentCreator.CreateSingleFile(path, trackers, pieceLength: pieceLength);
        return (path, payload, TorrentMeta.Parse(raw));
    }

    [Fact]
    public void Torrent_Meta_Parses_And_Hashes_Match_Manual_Sha1()
    {
        var (path, payload, meta) = MakeTorrent(700_000, 65536);
        try
        {
            Assert.Equal(payload.LongLength, meta.TotalLength);
            Assert.Equal(700_000 / 65536 + 1, meta.PieceCount);
            Assert.Equal(65536, meta.PieceLength);
            Assert.Equal(Path.GetFileName(path), meta.Name);
            Assert.Single(meta.Trackers);
            Assert.True(meta.IsSingleFile);

            // first piece hash computed manually must equal metadata piece hash
            var expected = SHA1.HashData(payload.AsSpan(0, 65536));
            Assert.Equal(expected, meta.PieceHashes[0]);
            // last piece is the remainder
            var lastLen = (int)(meta.TotalLength - (meta.PieceCount - 1) * 65536);
            var last = SHA1.HashData(payload.AsSpan((int)(meta.TotalLength - lastLen), lastLen));
            Assert.Equal(last, meta.PieceHashes[^1]);

            // info hash is a stable 20-byte digest
            Assert.Equal(20, meta.InfoHash.Length);
            Assert.Equal(meta.InfoHash, SHA1.HashData(meta.InfoDictBytes));
            // piece sizes: last one is the remainder
            Assert.Equal(lastLen, meta.PieceSize(meta.PieceCount - 1));
            Assert.Equal(65536, meta.PieceSize(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Torrent_Meta_Round_Trip_Preserves_InfoHash()
    {
        var (path, _, meta) = MakeTorrent();
        try
        {
            var reparsed = TorrentMeta.Parse(meta.RawBytes);
            Assert.Equal(meta.InfoHashHex, reparsed.InfoHashHex);
            Assert.Equal(meta.TotalLength, reparsed.TotalLength);
            Assert.Equal(meta.PieceCount, reparsed.PieceCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Torrent_Multi_File_Meta_Parses_Paths_And_Sanitizes_Traversal()
    {
        var root = OfDict()
            .Set("files", Of(new List<BencValue>
            {
                OfDict().Set("length", Of(10)).Set("path", Of(new List<BencValue> { Of(".."), Of("a"), Of("x.bin") })),
                OfDict().Set("length", Of(20)).Set("path", Of(new List<BencValue> { Of("b"), Of("y.bin") })),
            }))
            .Set("name", Of("pack"))
            .Set("piece length", Of(16384))
            .Set("pieces", Of(new byte[20 * 2]));
        var raw = OfDict().Set("info", root).Set("announce", Of("http://t/announce")).SortKeys().Encode();
        var meta = TorrentMeta.Parse(raw);

        Assert.False(meta.IsSingleFile);
        Assert.Equal(30, meta.TotalLength);
        Assert.Equal(2, meta.Files.Length);
        // ".." is stripped, not interpreted
        Assert.Equal(Path.Join("a", "x.bin"), meta.Files[0].SubPath);
        Assert.Equal(0, meta.Files[0].Offset);
        Assert.Equal(10, meta.Files[1].Offset);
    }

    [Fact]
    public void Torrent_Announce_List_Collapses_Trackers()
    {
        var (path, _, meta0) = MakeTorrent(trackers: new[] { "http://a/ann", "http://b/ann", "http://a/ann" });
        File.Delete(path);
        Assert.Equal(2, meta0.Trackers.Length);
    }

    // ------------------------------------------------------------------ magnet
    [Fact]
    public void Magnet_Build_And_Parse_Round_Trip()
    {
        var (path, _, meta) = MakeTorrent(trackers: new[] { "http://a/ann", "udp://b:6969/announce" });
        try
        {
            var magnet = TorrentMeta.BuildMagnet(meta.InfoHash, "My File.bin", meta.Trackers);
            var parsed = TorrentMeta.ParseMagnet(magnet);

            Assert.Equal(meta.InfoHashHex, parsed.InfoHashHex);
            Assert.Equal("My File.bin", parsed.Name);
            Assert.Equal(2, parsed.Trackers.Count);
            Assert.Contains("udp://b:6969/announce", parsed.Trackers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Magnet_Parses_Base32_Hashes()
    {
        var hash = RandomNumberGenerator.GetBytes(20);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        // base32 of 20 bytes = 32 chars
        var b32 = Base32Encode(hash);
        var magnet = $"magnet:?xt=urn:btih:{b32}&dn=x";
        var parsed = TorrentMeta.ParseMagnet(magnet);
        Assert.Equal(hex, parsed.InfoHashHex);
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var sb = new System.Text.StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    [Fact]
    public void Magnet_Rejects_Missing_Or_Bad_Hash()
    {
        Assert.Throws<InvalidDataException>(() => TorrentMeta.ParseMagnet("magnet:?dn=only-name"));
        Assert.Throws<InvalidDataException>(() => TorrentMeta.ParseMagnet("magnet:?xt=urn:btih:zz"));
    }

    // ------------------------------------------------------------------ HTTP tracker
    [Fact]
    public async Task HttpTracker_Announce_Parses_Compact_Peers()
    {
        using var tracker = new LoopbackHttpTracker();
        var url = await tracker.StartAsync();
        tracker.KnownPeers.Add(new IPEndPoint(IPAddress.Loopback, 55555));

        var infoHash = RandomNumberGenerator.GetBytes(20);
        var peerId = System.Text.Encoding.UTF8.GetBytes("-NO1400-testtest");
        var result = await HttpTracker.AnnounceAsync(url, infoHash, peerId, 12345,
            0, 0, 1000, TrackerEvent.Started, CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Single(result.Peers);
        Assert.Equal(IPAddress.Loopback, result.Peers[0].Address);
        Assert.Equal(55555, result.Peers[0].Port);
        Assert.Equal(1800, result.IntervalSeconds);
        // the tracker saw properly percent-encoded info_hash + peer_id + left
        var seen = tracker.LastQuery;
        Assert.NotNull(seen);
        Assert.StartsWith("?info_hash=", seen);
        Assert.Contains("&left=1000", seen);
        Assert.Contains("&event=started", seen);
        Assert.Contains("&compact=1", seen);
    }

    [Fact]
    public async Task HttpTracker_Failure_Reason_Is_Reported()
    {
        using var tracker = new LoopbackHttpTracker(failure: "torrent not registered");
        var url = await tracker.StartAsync();
        var result = await HttpTracker.AnnounceAsync(url, RandomNumberGenerator.GetBytes(20),
            new byte[20], 12345, 0, 0, 0, TrackerEvent.Started, CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Equal("torrent not registered", result.FailureReason);
    }

    /// <summary>Minimal BEP-3 HTTP tracker for loopback tests.</summary>
    private sealed class LoopbackHttpTracker : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task? _failMode;
        public List<IPEndPoint> KnownPeers { get; } = new();
        public string? LastQuery { get; private set; }
        private readonly string _failure;

        public LoopbackHttpTracker(string? failure = null)
        {
            _failure = failure ?? "";
            var port = 20000 + Random.Shared.Next(20000);
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public async Task<string> StartAsync()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var ctx = await _listener.GetContextAsync();
                        LastQuery = ctx.Request.Url?.Query;
                        byte[] body;
                        if (_failure.Length > 0)
                        {
                            body = System.Text.Encoding.UTF8.GetBytes(
                                $"d14:failure reason{_failure.Length}:{_failure}e");
                        }
                        else
                        {
                            var peers = new List<byte>();
                            foreach (var p in KnownPeers)
                            {
                                peers.AddRange(p.Address.GetAddressBytes());
                                peers.Add((byte)(p.Port >> 8));
                                peers.Add((byte)(p.Port & 0xFF));
                            }
                            body = System.Text.Encoding.UTF8.GetBytes(
                                $"d8:completei3e10:incompletei1e8:intervali1800e5:peers{peers.Count}:")
                                .Concat(peers).Concat(new byte[] { (byte)'e' }).ToArray();
                        }
                        ctx.Response.ContentType = "text/plain";
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                    catch (Exception) { /* stopping */ }
                }
            });
            // give the listener a moment to bind
            await Task.Delay(100);
            return _listener.Prefixes.First().TrimEnd('/') + "/announce";
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* already stopped */ }
        }
    }

    // ------------------------------------------------------------------ UDP tracker (BEP-15)
    [Fact]
    public async Task UdpTracker_Connect_And_Announce_Parses_Peers()
    {
        using var server = new LoopbackUdpTracker();
        var endpoint = server.Start();
        server.PeersToReturn.Add(new IPEndPoint(IPAddress.Loopback, 42424));

        using var client = new UdpTrackerClient();
        var infoHash = RandomNumberGenerator.GetBytes(20);
        var peerId = System.Text.Encoding.UTF8.GetBytes("-NO1400-udp-test");
        var result = await client.AnnounceAsync(endpoint, infoHash, peerId, 12345,
            10, 20, 990, TrackerEvent.Started, CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Single(result.Peers);
        Assert.Equal(42424, result.Peers[0].Port);
        Assert.Equal(120, result.IntervalSeconds);
        Assert.Equal(7, result.Complete);
        Assert.Equal(2, result.Incomplete);
        // event started maps to 2 in BEP-15
        Assert.Equal(2, server.LastAnnounceEvent);
    }

    /// <summary>Minimal BEP-15 UDP tracker for loopback tests.</summary>
    private sealed class LoopbackUdpTracker : IDisposable
    {
        private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
        public List<IPEndPoint> PeersToReturn { get; } = new();
        public int LastAnnounceEvent { get; private set; } = -1;
        private CancellationTokenSource? _cts;

        public IPEndPoint Start()
        {
            _udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var ep = (IPEndPoint)_udp.Client.LocalEndPoint!;
            _cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    UdpReceiveResult r;
                    try { r = await _udp.ReceiveAsync(); }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception) { continue; }

                    var action = BinaryPrimitives.ReadInt32BigEndian(r.Buffer.AsSpan(8));
                    if (action == 0 && r.Buffer.Length >= 16)
                    {
                        // connect reply: [action=0][txn][connection_id]
                        var reply = new byte[16];
                        BinaryPrimitives.WriteInt32BigEndian(reply, 0);
                        Array.Copy(r.Buffer, 12, reply, 4, 4);                     // txn echo
                        BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(8), 0x1122334455667788L);
                        await _udp.SendAsync(reply, r.RemoteEndPoint);
                    }
                    else if (action == 1 && r.Buffer.Length >= 98)
                    {
                        LastAnnounceEvent = BinaryPrimitives.ReadInt32BigEndian(r.Buffer.AsSpan(80));
                        var peers = new List<byte>();
                        foreach (var p in PeersToReturn)
                        {
                            peers.AddRange(p.Address.GetAddressBytes());
                            peers.Add((byte)(p.Port >> 8));
                            peers.Add((byte)(p.Port & 0xFF));
                        }
                        var reply = new byte[20 + peers.Count];
                        BinaryPrimitives.WriteInt32BigEndian(reply, 1);        // announce
                        Array.Copy(r.Buffer, 12, reply, 4, 4);                    // txn
                        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(8), 120);  // interval
                        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(12), 2);  // leechers
                        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(16), 7);  // seeders
                        peers.CopyTo(reply, 20);
                        await _udp.SendAsync(reply, r.RemoteEndPoint);
                    }
                }
            });
            return ep;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _udp.Dispose();
        }
    }

    // ------------------------------------------------------------------ piece picker
    [Fact]
    public void PiecePicker_Rarest_First_Prefers_Rarest_Pieces()
    {
        var picker = new PiecePicker(4, 16384, 65536);
        var rich = new[] { true, true, true, true };     // has all
        var poor = new[] { true, true, false, false };    // has only 0,1

        picker.AddAvailability(rich);
        picker.AddAvailability(poor);
        picker.AddAvailability(rich);
        // availability: piece0=3, piece1=3, piece2=1, piece3=1 → pick from {2,3}

        var picks = new HashSet<int>();
        for (var i = 0; i < 8; i++)
        {
            var blk = picker.PickBlock(rich);
            Assert.NotNull(blk);
            picks.Add(blk!.Value.Piece);
            picker.ReleaseBlock(blk.Value.Piece, blk.Value.Begin);
        }
        Assert.Subset(new HashSet<int> { 2, 3 }, picks);
    }

    [Fact]
    public void PiecePicker_Sequential_Picks_In_Order()
    {
        var picker = new PiecePicker(4, 16384, 65536) { Sequential = true };
        var peer = new[] { true, true, true, true };
        var order = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            for (var b = 0; b < 4; b++) // 16384 blocks per piece
            {
                var blk = picker.PickBlock(peer);
                Assert.NotNull(blk);
                if (order.Count == 0 || order[^1] != blk!.Value.Piece) order.Add(blk.Value.Piece);
                picker.ReleaseBlock(blk.Value.Piece, blk.Value.Begin);
            }
            picker.MarkHave(order[^1]);
        }
        Assert.Equal(new[] { 0, 1, 2, 3 }, order);
    }

    [Fact]
    public void PiecePicker_Does_Not_Return_Same_Block_Twice_In_Flight()
    {
        var picker = new PiecePicker(1, 16384, 16384);
        var peer = new[] { true };
        var first = picker.PickBlock(peer);
        var second = picker.PickBlock(peer);
        Assert.NotNull(first);
        Assert.Null(second);                        // only block is in flight
        picker.ReleaseBlock(first!.Value.Piece, first.Value.Begin);
        Assert.NotNull(picker.PickBlock(peer));     // released → pickable again
    }

    [Fact]
    public void PiecePicker_Restore_And_Skip_Have()
    {
        var picker = new PiecePicker(3, 10, 25);
        picker.RestoreFrom(new[] { true, false, true });
        Assert.Equal(2, picker.HaveCount);
        Assert.False(picker.IsComplete);
        var pick = picker.PickBlock(new[] { true, true, true });
        Assert.Equal(1, pick!.Value.Piece);         // only piece 1 remains
        picker.MarkHave(1);
        Assert.True(picker.IsComplete);
    }

    // ------------------------------------------------------------------ DHT KRPC
    [Fact]
    public async Task Dht_Two_Nodes_Exchange_Ping()
    {
        using var a = new DhtClient();
        using var b = new DhtClient();
        a.StartListening();
        b.StartListening();
        var bEp = new IPEndPoint(IPAddress.Loopback, b.LocalPort);

        await a.PingAsync(bEp, CancellationToken.None);

        Assert.True(a.KnownNodes >= 1);             // b inserted into a's table
    }

    [Fact]
    public async Task Dht_GetPeers_Returns_Token_And_Node_Ids()
    {
        using var a = new DhtClient();
        using var b = new DhtClient();
        a.StartListening();
        b.StartListening();
        var bEp = new IPEndPoint(IPAddress.Loopback, b.LocalPort);
        await a.PingAsync(bEp, CancellationToken.None);

        var infoHash = RandomNumberGenerator.GetBytes(20);
        var peers = await a.GetPeersAsync(infoHash, 8, CancellationToken.None);
        // b holds no peers for this hash → empty list, but the lookup must complete
        // without error and a must have learned b's node id
        Assert.True(a.KnownNodes >= 1);
        Assert.Empty(peers);
    }

    [Fact]
    public void Dht_Distance_Rank_Prefers_Longer_Prefix()
    {
        var a = new byte[20];
        var b = new byte[20];
        b[19] = 0x01;                                 // differs in last bit
        var c = new byte[20];
        c[0] = 0xFF;                                  // differs in first bits
        var rankNear = DhtClient.DistanceRank(a, b);
        var rankFar = DhtClient.DistanceRank(a, c);
        Assert.True(rankNear > rankFar);
    }

    // ------------------------------------------------------------------ wire framing
    [Fact]
    public async Task PeerWire_Handshake_And_Extension_Exchange()
    {
        var infoHash = RandomNumberGenerator.GetBytes(20);
        var serverPeerId = System.Text.Encoding.UTF8.GetBytes("-SV1400-serverpeer1");
        var clientPeerId = System.Text.Encoding.UTF8.GetBytes("-CL1400-clientpeer1");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            var wire = await PeerWireClient.AcceptAsync(tcp, infoHash, serverPeerId, -1,
                CancellationToken.None);
            wire.InitBitfield(4);
            // send bitfield + unchoke
            await wire.SendBitfieldAsync(new[] { true, true, true, true }, CancellationToken.None);
            await wire.SendUnchokeAsync(CancellationToken.None);
            // serve requests until the connection closes
            while (true)
            {
                var msg = await wire.ReadMessageAsync(CancellationToken.None);
                if (msg is null) break;
                if (msg.Value.Id == PeerWireClient.MsgRequest)
                {
                    await wire.SendPieceAsync(0, 0, new byte[16384], CancellationToken.None);
                }
            }
            await wire.DisposeAsync();
        });

        var ep = (IPEndPoint)listener.LocalEndpoint;
        await using var client = PeerWireClient.CreateConnected(new TorrentPeer(ep.Address, ep.Port),
            infoHash, clientPeerId);
        client.InitBitfield(4);
        Assert.True(await client.HandshakeAsync(CancellationToken.None));

        // first inbound messages: our own ext handshake echo (20) then bitfield (5)
        PeerWireClient.PeerMessage? bf = null;
        for (var i = 0; i < 4 && bf is null; i++)
        {
            var m = await client.ReadMessageAsync(CancellationToken.None);
            if (m is { } mm && mm.Id == PeerWireClient.MsgBitfield) bf = mm;
        }
        Assert.NotNull(bf);
        Assert.Equal(4, client.PeerBitfield!.Length);
        Assert.True(client.PeerBitfield[0]);
        // extension handshake arrived before bitfield? consume until unchoke
        var sawUnchoke = false;
        for (var i = 0; i < 4 && !sawUnchoke; i++)
        {
            var m = await client.ReadMessageAsync(CancellationToken.None);
            if (m is { } mm && mm.Id == PeerWireClient.MsgUnchoke) sawUnchoke = true;
        }
        Assert.True(sawUnchoke);

        // BEP-10 extension ids from the server's handshake
        Assert.True(client.PeerExtensions.ContainsKey("ut_metadata"));

        // request a block
        await client.SendRequestAsync(0, 0, 16384, CancellationToken.None);
        PeerWireClient.PeerMessage? piece = null;
        for (var i = 0; i < 4 && piece is null; i++)
        {
            var m = await client.ReadMessageAsync(CancellationToken.None);
            if (m is { } mm && mm.Id == PeerWireClient.MsgPiece) piece = mm;
        }
        Assert.NotNull(piece);
        Assert.Equal(0, piece!.Value.PieceIndex);
        Assert.Equal(0, piece.Value.BlockBegin);
        Assert.Equal(16384, piece.Value.Payload.Length - 8);

        await client.DisposeAsync();       // server loop ends when the socket closes
        await accepted;
        listener.Stop();
    }

    [Fact]
    public void UtMetadata_Request_Builds_And_Data_Parses()
    {
        var infoDict = RandomNumberGenerator.GetBytes(4096);
        var data = UtMetadata.BuildData(0, infoDict, 4096);
        var parsed = UtMetadata.ParseData(data, 0);
        Assert.NotNull(parsed);
        Assert.Equal(infoDict, parsed);

        // second piece of a larger dict
        var big = RandomNumberGenerator.GetBytes(UtMetadata.MetadataPiece + 1024);
        var piece1 = UtMetadata.BuildData(1, big, big.Length);
        var parsed1 = UtMetadata.ParseData(piece1, 1);
        Assert.NotNull(parsed1);
        Assert.Equal(big.AsSpan(UtMetadata.MetadataPiece, 1024).ToArray(), parsed1);
    }
}
