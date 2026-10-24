using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Nova.Engine.Torrent;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>Serializes all torrent suites (they bind many ephemeral ports).</summary>
[Xunit.CollectionDefinition("TorrentSerial141")]
public sealed class TorrentSerial141Collection { }

[Xunit.Collection("TorrentSerial141")]
/// <summary>
/// v1.4.1 BitTorrent additions: PEX (BEP-11) wire codec + handshake advertisement,
/// DHT peer-store serving (BEP-5 announce_peer/get_peers values), UDP tracker scrape
/// (BEP-15 action=2), per-file selection (piece mask + partial E2E) and super-seeding
/// (BEP-16) E2E. Everything loopback-only.
/// </summary>
public class TorrentV141Tests : IAsyncLifetime
{
    private string _dir = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nova-bt-141-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() =>
        await Task.Run(() => { try { Directory.Delete(_dir, true); } catch { /* temp */ } });

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start(1);
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // ================================================================= PEX codec
    [Fact]
    public void Pex_Encode_Decode_Roundtrip_Preserves_Peers_And_Flags()
    {
        var added = new List<PexMessage.PexPeer>
        {
            new(new TorrentPeer(IPAddress.Parse("10.1.2.3"), 51413), 0x01),
            new(new TorrentPeer(IPAddress.Parse("192.168.0.240"), 6881), 0x00),
            new(new TorrentPeer(IPAddress.Parse("127.0.0.1"), FreePort()), 0x02),
        };
        var dropped = new List<TorrentPeer> { new(IPAddress.Parse("172.16.4.4"), 12345) };

        var payload = PexMessage.Encode(added, dropped);
        var (back, droppedBack) = PexMessage.Decode(payload);

        Assert.Equal(3, back.Count);
        Assert.Equal("10.1.2.3", back[0].Peer.Address.ToString());
        Assert.Equal(51413, back[0].Peer.Port);
        Assert.Equal(0x01, back[0].Flags);
        Assert.Equal("192.168.0.240", back[1].Peer.Address.ToString());
        Assert.Equal(6881, back[1].Peer.Port);
        Assert.Equal(0x00, back[1].Flags);
        Assert.Equal(0x02, back[2].Flags);
        Assert.Single(droppedBack);
        Assert.Equal(12345, droppedBack[0].Port);
    }

    [Fact]
    public void Pex_Decode_Tolerates_Malformed_And_Empty_Payloads()
    {
        var (a1, d1) = PexMessage.Decode(Array.Empty<byte>());
        Assert.Empty(a1); Assert.Empty(d1);

        // junk bytes that are not bencoded at all
        var (a2, d2) = PexMessage.Decode(new byte[] { 0xFF, 0x00, 0x13, 0x37 });
        Assert.Empty(a2); Assert.Empty(d2);

        // bencoded dict but with a truncated "added" string (4 bytes = partial peer)
        var trunc = PexMessage.Encode(
            new List<PexMessage.PexPeer> { new(new TorrentPeer(IPAddress.Loopback, 9999), 0) },
            Array.Empty<TorrentPeer>());
        var dict = BencValue.Decode(trunc);
        dict.Set("added", BencValue.Of(new byte[] { 127, 0, 0, 1 }));  // 4 bytes — not 6
        var (a3, _) = PexMessage.Decode(dict.Encode());
        Assert.Empty(a3);   // partial entry skipped, never thrown

        // port 0 entries are filtered (never a valid endpoint)
        var zero = BencValue.OfDict().Set("added",
            BencValue.Of(new byte[] { 127, 0, 0, 1, 0, 0 }));
        var (a4, _) = PexMessage.Decode(zero.Encode());
        Assert.Empty(a4);
    }

    // ================================================================= handshake advertises ut_pex
    [Fact]
    public async Task Extension_Handshake_Advertises_UtPex_And_Pex_Is_Routable()
    {
        var infoHash = SHA1.HashData(Encoding.UTF8.GetBytes("nova-141-pex-handshake"));
        var port = FreePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var acceptTask = listener.AcceptTcpClientAsync();

        var ourId = TorrentSessionOptions.GeneratePeerId();
        var peer = new TorrentPeer(IPAddress.Loopback, port);
        var client = PeerWireClient.CreateConnected(peer, infoHash, Encoding.UTF8.GetBytes(ourId));

        var serverSide = await acceptTask;
        using var _s = serverSide;

        // the client speaks FIRST: start its handshake, THEN accept (AcceptAsync reads
        // the client's 68-byte handshake before replying — accepting before the client
        // writes would deadlock both sides)
        var hsTask = client.HandshakeAsync(CancellationToken.None);
        var server = await PeerWireClient.AcceptAsync(serverSide, infoHash,
            Encoding.UTF8.GetBytes(TorrentSessionOptions.GeneratePeerId()), -1, CancellationToken.None);
        Assert.True(await hsTask.WaitAsync(TimeSpan.FromSeconds(5)));

        // both sides sent their BEP-10 handshake right after the wire handshake —
        // each side must READ one message to parse the other's extensions
        var clientFirst = await client.ReadMessageAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(clientFirst);
        Assert.Equal(0, clientFirst!.Value.ExtendedId);
        var first = await server.ReadMessageAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(first);
        Assert.Equal(PeerWireClient.MsgExtended, first!.Value.Id);
        Assert.Equal(0, first.Value.ExtendedId);   // 0 = handshake

        // both sides must have announced ut_pex (fixed id 2)
        Assert.Equal(1, client.UtMetadataId);          // from the server's m dict
        Assert.True(client.PeerExtensions.ContainsKey("ut_metadata"));
        Assert.True(server.PeerExtensions.ContainsKey("ut_metadata"));
        Assert.Equal(2, client.UtPexId);
        Assert.Equal(2, server.UtPexId);

        // client → server PEX: receiver routes by the id IT declared
        var receiverId = server.UtPexId;
        Assert.Equal(2, receiverId);
        var want = new TorrentPeer(IPAddress.Parse("10.9.8.7"), 42424);
        await client.SendMessageInternalAsync(PeerWireClient.MsgExtended, receiverId,
            PexMessage.Encode(new[] { new PexMessage.PexPeer(want, 0) },
                Array.Empty<TorrentPeer>()), CancellationToken.None);

        var msg = await server.ReadMessageAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(msg);
        Assert.Equal(PeerWireClient.MsgExtended, msg!.Value.Id);
        Assert.Equal(receiverId, msg.Value.ExtendedId);
        var (added, dropped) = PexMessage.Decode(msg.Value.Payload);
        Assert.Single(added);
        Assert.Equal("10.9.8.7", added[0].Peer.Address.ToString());
        Assert.Equal(42424, added[0].Peer.Port);
        Assert.Empty(dropped);

        await server.DisposeAsync();
        await client.DisposeAsync();
    }

    // ================================================================= DHT peer store
    [Fact]
    public async Task Dht_Announce_Is_Stored_And_Served_Back_As_Values()
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("nova-141-dht-peerstore"));
        using var server = new DhtClient(FreePort());
        server.StartListening();
        using var client = new DhtClient(FreePort());
        client.StartListening();
        await Task.Delay(120);   // sockets settle

        var serverEp = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
        // 1) ping → the client's routing table learns the server's node id (needed for tokens)
        await client.PingAsync(serverEp, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(client.KnownNodes >= 1, "ping must populate the routing table");
        Assert.True(client.HasTokenFor(server.NodeId) is false or true);   // ping itself grants no token
        // 2) get_peers → token for the server (stored per node id)
        var first = await client.GetPeersAsync(hash, 4, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(first);   // nothing announced yet
        Assert.True(client.HasTokenFor(server.NodeId),
            "get_peers reply must yield an announce token for the server node");
        Assert.Equal(0, server.StoredPeerCount(hash));   // nothing announced yet

        // 3) announce → server must store the endpoint
        var myPort = FreePort();
        await client.AnnounceAsync(hash, myPort, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, server.StoredPeerCount(hash));

        // 4) get_peers again → values now serve the announced compact peer
        var peers = await client.GetPeersAsync(hash, 4, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Contains(peers, p => p.Port == myPort && p.Address.Equals(IPAddress.Loopback));

        // 5) bad token is rejected: craft an announce_peer with a garbage token
        var bogus = BencValue.OfDict()
            .Set("t", BencValue.Of("zz"))
            .Set("y", BencValue.Of("q"))
            .Set("q", BencValue.Of("announce_peer"))
            .Set("a", BencValue.OfDict()
                .Set("id", BencValue.Of(new byte[20]))
                .Set("info_hash", BencValue.Of(hash))
                .Set("port", BencValue.Of(myPort))
                .Set("token", BencValue.Of("garbage-token")));
        await server.HandleDatagramAsync(bogus.Encode(), serverEp, CancellationToken.None);
        Assert.Equal(1, server.StoredPeerCount(hash));   // unchanged
    }

    // ================================================================= UDP scrape
    [Fact]
    public async Task Udp_Tracker_Scrape_Parses_Stats_For_Multiple_Hashes()
    {
        var h1 = SHA1.HashData(Encoding.UTF8.GetBytes("nova-141-scrape-1"));
        var h2 = SHA1.HashData(Encoding.UTF8.GetBytes("nova-141-scrape-2"));
        var port = FreePort();
        using var tracker = new ScrapeTrackerHarness(port,
            new Dictionary<byte[], (int, int, int)>
            {
                [h1] = (12, 34, 5),   // seeders, completed, leechers
                [h2] = (0, 1, 0),
            });
        tracker.Start();
        await Task.Delay(120);

        using var client = new UdpTrackerClient();
        var result = await client.ScrapeAsync(new IPEndPoint(IPAddress.Loopback, port),
            new[] { h1, h2 }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(2, result.Count);
        Assert.Equal(12, result[0].Seeders);
        Assert.Equal(34, result[0].Completed);
        Assert.Equal(5, result[0].Leechers);
        Assert.Equal(0, result[1].Seeders);
        Assert.Equal(1, result[1].Completed);
        Assert.Equal(0, result[1].Leechers);
    }

    // ================================================================= file selection
    [Fact]
    public void Piece_Mask_For_Files_Maps_Overlapping_Pieces()
    {
        // piece length 100, total 350 → 4 pieces
        // files: A=[0..120) → pieces 0,1 ; B=[120..210) → pieces 1,2 ; C=[210..350) → pieces 2,3
        var meta = MakeMultiFileMeta(new[] { (120L, 0L), (90L, 120L), (140L, 210L) },
            pieceLength: 100);
        var mask = meta.PieceMaskForFiles(new[] { 0 });
        Assert.Equal(new[] { true, true, false, false }, mask);
        var maskAC = meta.PieceMaskForFiles(new[] { 0, 2 });
        Assert.Equal(new[] { true, true, true, true }, maskAC);
        var all = meta.PieceMaskForFiles(Array.Empty<int>());
        Assert.All(all, w => Assert.True(w));
        Assert.Equal(140, meta.SelectedFileBytes(new[] { 2 }));
        Assert.Equal(350, meta.SelectedFileBytes(Array.Empty<int>()));
    }

    [Fact]
    public void ParseSelectedFiles_Handles_Csv_Whitelist_And_Garbage()
    {
        Assert.Null(TorrentTask.ParseSelectedFiles(""));
        Assert.Null(TorrentTask.ParseSelectedFiles("  "));
        Assert.Equal(new[] { 0, 2, 5 }, TorrentTask.ParseSelectedFiles("0, 2,5"));
        Assert.Equal(new[] { 3 }, TorrentTask.ParseSelectedFiles("3,x,3"));   // dedup + garbage
        Assert.Null(TorrentTask.ParseSelectedFiles("-1,x"));                   // nothing valid
    }

    [Fact]
    public async Task E2E_MultiFile_Selection_Downloads_Only_Selected_Files()
    {
        // two files, 64 KiB pieces so the boundary piece is shared: A≈800 KB, B≈900 KB
        var a = RandomNumberGenerator.GetBytes(800_000);
        var b = RandomNumberGenerator.GetBytes(900_003);
        var contiguous = a.Concat(b).ToArray();

        var seedPort = FreePort();
        var announceUrl = $"http://127.0.0.1:{FreePort()}/announce";
        var tracker = new TrackerHarness(announceUrl, seedPort);
        await tracker.StartAsync();
        try
        {
            var meta = MakeMultiFileMeta(new[] { ((long)a.Length, 0L), ((long)b.Length, (long)a.Length) },
                pieceLength: 65536, contiguous: contiguous, name: "nova141sel",
                trackers: new[] { announceUrl });

            // seeder: contiguous temp at <dest>/<name>.novadl so OpenReadView can serve it
            var seedDest = Path.Combine(_dir, "seed", "nova141sel.novadl");
            Directory.CreateDirectory(Path.GetDirectoryName(seedDest)!);
            await File.WriteAllBytesAsync(seedDest, contiguous);
            var seederCts = new CancellationTokenSource();
            var seeder = new TorrentSession(meta, new TorrentSessionOptions
            {
                DestinationDir = Path.Combine(_dir, "seed"),
                Trackers = meta.Trackers,
                EnableDht = false,
                ListenPort = seedPort,
                MaxPeers = 8,
            });
            _ = Task.Run(() => seeder.SeedAsync(seederCts.Token));

            var dest = Path.Combine(_dir, "down");
            Directory.CreateDirectory(dest);
            await using var session = new TorrentSession(meta, new TorrentSessionOptions
            {
                DestinationDir = dest,
                Trackers = meta.Trackers,
                EnableDht = false,
                SelectedFiles = new[] { 0 },          // ONLY file A
            });
            var final = await session.DownloadAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(60));

            // only the selected file exists in the split folder
            var fileA = Path.Combine(final, "file_0.bin");
            var fileB = Path.Combine(final, "file_1.bin");
            Assert.True(File.Exists(fileA));
            Assert.False(File.Exists(fileB));
            Assert.Equal(a, await File.ReadAllBytesAsync(fileA));

            seederCts.Cancel();
            await seeder.DisposeAsync();
        }
        finally
        {
            tracker.Dispose();
        }
    }

    // ================================================================= super-seeding
    [Fact]
    public async Task E2E_SuperSeed_Serves_Byte_Exact_Through_Piece_Drip()
    {
        var payload = RandomNumberGenerator.GetBytes(1_400_007);   // ~22 pieces @64 KiB
        var payloadPath = Path.Combine(_dir, "seedsrc", "super.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        await File.WriteAllBytesAsync(payloadPath, payload);

        var seedPort = FreePort();
        var announceUrl = $"http://127.0.0.1:{FreePort()}/announce";
        var tracker = new TrackerHarness(announceUrl, seedPort);
        await tracker.StartAsync();
        try
        {
            var raw = TorrentCreator.CreateSingleFile(payloadPath, new[] { announceUrl },
                pieceLength: 65536);
            var meta = TorrentMeta.Parse(raw);

            var seederCts = new CancellationTokenSource();
            var logs = new List<string>();
            var seeder = new TorrentSession(meta, new TorrentSessionOptions
            {
                DestinationDir = Path.GetDirectoryName(payloadPath)!,
                Trackers = meta.Trackers,
                EnableDht = false,
                ListenPort = seedPort,
                SuperSeed = true,          // v1.4.1 BEP-16
            });
            seeder.Log += s => { lock (logs) logs.Add(s); };
            _ = Task.Run(() => seeder.SeedAsync(seederCts.Token));

            var dest = Path.Combine(_dir, "down");
            Directory.CreateDirectory(dest);
            await using var session = new TorrentSession(meta, new TorrentSessionOptions
            {
                DestinationDir = dest,
                Trackers = meta.Trackers,
                EnableDht = false,
            });
            var final = await session.DownloadAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(payload, await File.ReadAllBytesAsync(final));
            // the drip actually happened: at least one HAVE assignment log entry
            lock (logs)
                Assert.Contains(logs, l => l.Contains("super-seed: drip piece"));

            seederCts.Cancel();
            await seeder.DisposeAsync();
        }
        finally
        {
            tracker.Dispose();
        }
    }

    // ================================================================= helpers
    /// <summary>Builds a multi-file torrent metainfo from per-file (length, offset) tuples.
    /// Piece hashes are computed over the concatenation (contiguous stream).</summary>
    private static TorrentMeta MakeMultiFileMeta((long Length, long Offset)[] files,
        long pieceLength, byte[]? contiguous = null, string name = "nova-multi",
        string[]? trackers = null)
    {
        contiguous ??= RandomNumberGenerator.GetBytes((int)files.Sum(f => f.Length));
        var pieces = new List<byte>();
        for (var off = 0; off < contiguous.Length; off += (int)pieceLength)
        {
            var len = (int)Math.Min(pieceLength, contiguous.Length - off);
            pieces.AddRange(SHA1.HashData(contiguous.AsSpan(off, len)));
        }
        var filesList = files.Select(f => BencValue.OfDict()
                .Set("length", BencValue.Of(f.Length))
                .Set("path", BencValue.Of(new List<BencValue>
                    { BencValue.Of($"file_{Array.IndexOf(files, f)}.bin") })))
            .ToList();
        var info = BencValue.OfDict()
            .Set("files", BencValue.Of(filesList))
            .Set("name", BencValue.Of(name))
            .Set("piece length", BencValue.Of(pieceLength))
            .Set("pieces", BencValue.Of(pieces.ToArray()));
        var root = BencValue.OfDict()
            .Set("announce", BencValue.Of(trackers is { Length: > 0 } ? trackers[0] : ""))
            .Set("info", info);
        return TorrentMeta.Parse(root.SortKeys().Encode());
    }

    /// <summary>UDP tracker answering connect + scrape with fixed per-hash stats (BEP-15).</summary>
    private sealed class ScrapeTrackerHarness : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly Dictionary<string, (int Seeders, int Completed, int Leechers)> _stats;

        public ScrapeTrackerHarness(int port,
            Dictionary<byte[], (int, int, int)> stats)
        {
            _udp = new UdpClient(port);
            _stats = stats.ToDictionary(kv => Convert.ToHexString(kv.Key), kv => kv.Value);
        }

        public void Start()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var res = await _udp.ReceiveAsync();
                        if (res.Buffer.Length < 16) continue;
                        var txn = BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(12));
                        var action = BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(8));

                        if (action == 0 && res.Buffer.Length >= 16)
                        {
                            var reply = new byte[16];
                            BinaryPrimitives.WriteInt32BigEndian(reply, 0);
                            BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4), txn);
                            BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(8), 0x1234);
                            await _udp.SendAsync(reply, res.RemoteEndPoint);
                        }
                        else if (action == 2)
                        {
                            var n = (res.Buffer.Length - 16) / 20;
                            var reply = new byte[8 + 12 * n];
                            BinaryPrimitives.WriteInt32BigEndian(reply, 2);
                            BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4), txn);
                            for (var i = 0; i < n; i++)
                            {
                                var hash = Convert.ToHexString(
                                    res.Buffer.Skip(16 + 20 * i).Take(20).ToArray());
                                var s = _stats.TryGetValue(hash, out var st) ? st : (0, 0, 0);
                                var off = 8 + 12 * i;
                                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(off), s.Item1);
                                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(off + 4), s.Item2);
                                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(off + 8), s.Item3);
                            }
                            await _udp.SendAsync(reply, res.RemoteEndPoint);
                        }
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception) { /* keep serving */ }
                }
            });
        }

        public void Dispose() => _udp.Dispose();
    }

    /// <summary>Loopback HTTP tracker reporting one seeder to every requester.</summary>
    private sealed class TrackerHarness : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly int _seedPort;

        public TrackerHarness(string announceUrl, int seedPort)
        {
            var uri = new Uri(announceUrl);
            _seedPort = seedPort;
            _listener.Prefixes.Add($"http://{uri.Host}:{uri.Port}/");
        }

        public async Task StartAsync()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var ctx = await _listener.GetContextAsync();
                        var peers = new List<byte>();
                        peers.AddRange(IPAddress.Loopback.GetAddressBytes());
                        peers.Add((byte)(_seedPort >> 8));
                        peers.Add((byte)(_seedPort & 0xFF));
                        var head = Encoding.UTF8.GetBytes(
                            "d8:completei1e10:incompletei0e8:intervali3e5:peers" + peers.Count + ":");
                        var body = head.Concat(peers).Append((byte)'e').ToArray();
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                    catch (Exception) { /* stopping */ }
                }
            });
            await Task.Delay(150);
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* stopped */ }
        }
    }
}
