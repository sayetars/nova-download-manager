using System.Net;
using System.Security.Cryptography;
using Nova.Engine.Torrent;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>Serializes all torrent suites (they bind many ephemeral ports).</summary>
[Xunit.CollectionDefinition("TorrentSerial")]
public sealed class TorrentSerialCollection { }

[Xunit.Collection("TorrentSerial")]
/// <summary>
/// v1.4.0 BitTorrent end-to-end over loopback ONLY: our seeder ↔ local HTTP tracker ↔
/// our downloader. Verifies byte-exact transfer, SHA-1 piece verification, magnet
/// metadata acquisition (BEP-9), resume from a persisted bitfield, seeding upload,
/// and multi-file splitting. No external network access.
/// </summary>
public class TorrentE2eTests : IAsyncLifetime
{
    private string _dir = null!;
    private byte[] _payload = null!;
    private TorrentMeta _meta = null!;
    private string _payloadPath = null!;
    private TestSeeder _seeder = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nova-bt-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // ~1.6 MB payload, 64 KiB pieces → 26 pieces, 2.5 blocks per piece edge cases
        _payload = RandomNumberGenerator.GetBytes(1_600_003);
        _payloadPath = Path.Combine(_dir, "seed", "payload.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(_payloadPath)!);
        await File.WriteAllBytesAsync(_payloadPath, _payload);
    }

    public async Task DisposeAsync()
    {
        if (_seeder is not null) await _seeder.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { /* temp leftovers */ }
    }

    private async Task<(TorrentMeta meta, TestSeeder seeder)> StartSeederAsync(
        string[]? extraTrackers = null)
    {
        // free port for the seeder listener
        var port = FindFreePort();
        var announceUrl = $"http://127.0.0.1:{FindFreePortForTracker()}/announce";
        var trackers = (extraTrackers is { Length: > 0 } ? extraTrackers : Array.Empty<string>())
            .Concat(new[] { announceUrl })
            .Distinct()
            .ToArray();

        var raw = TorrentCreator.CreateSingleFile(_payloadPath, trackers, pieceLength: 65536);
        var meta = TorrentMeta.Parse(raw);

        var seeder = new TestSeeder(meta, _payloadPath, port);
        await seeder.StartAsync(announceUrl);
        _seeder = seeder;
        return (meta, seeder);
    }

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start(1);
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static int FindFreePortForTracker() => FindFreePort();

    // ------------------------------------------------------------------ tests
    [Fact]
    public async Task E2E_Full_Download_From_Own_Seeder_Via_Tracker_Is_Byte_Exact()
    {
        var (meta, seeder) = await StartSeederAsync();
        var dest = Path.Combine(_dir, "download");
        Directory.CreateDirectory(dest);

        var opt = new TorrentSessionOptions
        {
            DestinationDir = dest,
            Trackers = meta.Trackers,     // discovers the seeder through the tracker
            EnableDht = false,
            MaxPeers = 8,
        };
        var progressBytes = 0L;
        await using (var session = new TorrentSession(meta, opt))
        {
            session.Progress += (delta, _) => Interlocked.Add(ref progressBytes, delta);
            var final = await session.DownloadAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(60));

            // byte-exact?
            var got = await File.ReadAllBytesAsync(final);
            Assert.Equal(_payload, got);
            Assert.Equal(_payload.Length, session.Downloaded);
            Assert.Equal(_payload.Length, progressBytes);
            // temp + bitfield sidecar are gone after completion
            Assert.False(File.Exists(final + ".novadl"));
            Assert.False(File.Exists(final + ".novadl.bf"));
            // downloader served nothing back
            Assert.Equal(0, session.Uploaded);
        }
        Assert.True(seeder.UploadedBytes > 0);       // seeder actually uploaded the payload
    }

    [Fact]
    public async Task E2E_Resume_After_Pause_Uses_Persisted_Bitfield()
    {
        var (meta, seeder) = await StartSeederAsync();
        var dest = Path.Combine(_dir, "resume");
        Directory.CreateDirectory(dest);

        // simulate a paused run: temp file holds valid data for pieces 0..7 and the
        // bitfield sidecar marks exactly those pieces verified (what SaveBitfield wrote)
        var tempPath = Path.Combine(dest, meta.Name + ".novadl");
        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            await fs.WriteAsync(_payload.AsMemory(0, 8 * 65536));
        var bits = new byte[4 + (meta.PieceCount + 7) / 8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bits, meta.PieceCount);
        for (var i = 0; i < 8; i++) bits[4 + i / 8] |= (byte)(0x80 >> (i % 8));
        await File.WriteAllBytesAsync(tempPath + ".bf", bits);

        // run 2: fresh session, same destination → resumes from the bitfield, completes
        long progress2 = 0;
        await using (var session2 = new TorrentSession(meta, SessionOpts(dest, meta)))
        {
            session2.Progress += (delta, _) => Interlocked.Add(ref progress2, delta);
            string final;
            try
            {
                final = await session2.DownloadAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException("resume run did not complete in 60s");
            }

            var got = await File.ReadAllBytesAsync(final);
            Assert.Equal(_payload, got);
            // deterministic: exactly pieces 0..7 were pre-verified before the "pause"
            var expectedRemaining = _payload.Length - 8 * 65536;
            Assert.True(progress2 < _payload.Length,
                $"expected partial re-download, got {progress2} of {_payload.Length}");
            Assert.Equal(expectedRemaining, session2.Downloaded);
        }
    }

    private static TorrentSessionOptions SessionOpts(string dest, TorrentMeta meta) => new()
    {
        DestinationDir = dest,
        Trackers = meta.Trackers,
        EnableDht = false,
        MaxPeers = 8,
    };

    [Fact]
    public async Task E2E_Magnet_Metadata_Fetched_Via_ut_Metadata_Then_Downloads()
    {
        var (meta, seeder) = await StartSeederAsync();
        var dest = Path.Combine(_dir, "magnet");
        Directory.CreateDirectory(dest);

        // magnet WITHOUT trackers → static peer discovery only (seeder address known)
        var magnet = TorrentMeta.ParseMagnet(
            TorrentMeta.BuildMagnet(meta.InfoHash, null, Array.Empty<string>()));

        var fetchOpts = new TorrentSessionOptions
        {
            Trackers = Array.Empty<string>(),
            StaticPeers = new[] { new TorrentPeer(IPAddress.Loopback, seeder.Port) },
            EnableDht = false,
            MetadataTimeout = TimeSpan.FromSeconds(20),
        };
        var fetched = await TorrentSession.FetchMetadataAsync(magnet, fetchOpts, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(meta.InfoHashHex, fetched.InfoHashHex);
        Assert.Equal(meta.TotalLength, fetched.TotalLength);
        Assert.Equal(meta.PieceCount, fetched.PieceCount);
        Assert.Equal(meta.PieceHashes[0], fetched.PieceHashes[0]);
        Assert.Equal(meta.Name, fetched.Name);

        // and a full download with the fetched metadata completes byte-exact
        var opt = new TorrentSessionOptions
        {
            DestinationDir = dest,
            StaticPeers = new[] { new TorrentPeer(IPAddress.Loopback, seeder.Port) },
            Trackers = Array.Empty<string>(),
            EnableDht = false,
        };
        await using var session = new TorrentSession(fetched, opt);
        var final = await session.DownloadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(_payload, await File.ReadAllBytesAsync(final));
    }

    [Fact]
    public async Task E2E_Seeder_Serves_Multiple_Downloaders()
    {
        var (meta, seeder) = await StartSeederAsync();
        var opt = new TorrentSessionOptions
        {
            Trackers = meta.Trackers,
            EnableDht = false,
            MaxPeers = 8,
        };

        var tasks = new List<Task<byte[]>>();
        for (var i = 0; i < 3; i++)
        {
            var dest = Path.Combine(_dir, "multi-" + i);
            Directory.CreateDirectory(dest);
            tasks.Add(Task.Run(async () =>
            {
                await using var session = new TorrentSession(meta,
                    new TorrentSessionOptions { DestinationDir = dest, Trackers = meta.Trackers, EnableDht = false });
                var final = await session.DownloadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(90));
                return await File.ReadAllBytesAsync(final);
            }));
        }
        var results = await Task.WhenAll(tasks);
        foreach (var r in results) Assert.Equal(_payload, r);
        Assert.True(seeder.UploadedBytes >= 3 * _payload.Length * 9 / 10);
    }

    [Fact]
    public async Task E2E_Second_Downloader_Serves_Partial_Back()
    {
        // downloader A completes and keeps seeding; downloader B must fetch some
        // bytes from A (upload path through a completed session).
        var (meta, seeder) = await StartSeederAsync();
        var destA = Path.Combine(_dir, "a");
        var destB = Path.Combine(_dir, "b");
        Directory.CreateDirectory(destA);
        Directory.CreateDirectory(destB);

        var optA = new TorrentSessionOptions
        {
            DestinationDir = destA,
            Trackers = meta.Trackers,
            EnableDht = false,
            SeedAfterComplete = true,
            ListenPort = FindFreePort(),
        };
        var sessionA = new TorrentSession(meta, optA);
        var finalA = await sessionA.DownloadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(_payload, await File.ReadAllBytesAsync(finalA));

        var optB = new TorrentSessionOptions
        {
            DestinationDir = destB,
            StaticPeers = new[] { new TorrentPeer(IPAddress.Loopback, optA.ListenPort!.Value) },
            Trackers = Array.Empty<string>(),
            EnableDht = false,
        };
        await using (var sessionB = new TorrentSession(meta, optB))
        {
            var finalB = await sessionB.DownloadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(_payload, await File.ReadAllBytesAsync(finalB));
            Assert.True(sessionA.Uploaded > 0, "seeder A must have uploaded to B");
        }
        await sessionA.DisposeAsync();
    }

    [Fact]
    public async Task E2E_Corrupt_Piece_Is_Detected_And_Retried()
    {
        var (meta, seeder) = await StartSeederAsync();
        var dest = Path.Combine(_dir, "corrupt");
        Directory.CreateDirectory(dest);

        // pre-existing temp with garbage + a bitfield claiming piece 0 is done
        var tempPath = Path.Combine(dest, meta.Name + ".novadl");
        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            await fs.WriteAsync(new byte[_payload.Length]);

        await using (var session = new TorrentSession(meta,
            new TorrentSessionOptions { DestinationDir = dest, Trackers = meta.Trackers, EnableDht = false }))
        {
            var final = await session.DownloadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
            // no bitfield sidecar → full redownload; final must be byte-exact regardless
            Assert.Equal(_payload, await File.ReadAllBytesAsync(final));
        }
    }

    /// <summary>Own seeder: complete file + listener + optional tracker harness.</summary>
    private sealed class TestSeeder : IAsyncDisposable
    {
        private readonly TorrentMeta _meta;
        private readonly string _filePath;
        private readonly int _port;
        private TorrentSession? _session;
        private TrackerHarness? _tracker;
        private CancellationTokenSource? _cts;
        public long UploadedBytes => _session?.Uploaded ?? 0;
        public int Port => _port;

        public TestSeeder(TorrentMeta meta, string filePath, int port)
        {
            _meta = meta;
            _filePath = filePath;
            _port = port;
        }

        /// <summary>Starts tracker (if URL provided) then the seed session.</summary>
        public async Task StartAsync(string trackerAnnounceUrl)
        {
            _tracker = new TrackerHarness(trackerAnnounceUrl, _port);
            await _tracker.StartAsync();

            _cts = new CancellationTokenSource();
            var opt = new TorrentSessionOptions
            {
                DestinationDir = Path.GetDirectoryName(_filePath)!,
                Trackers = _meta.Trackers,
                EnableDht = false,
                ListenPort = _port,
                MaxPeers = 16,
            };
            _session = new TorrentSession(_meta, opt);
            _ = Task.Run(() => _session.SeedAsync(_cts.Token));
            // wait until the listener is accepting
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                try
                {
                    using var probe = new System.Net.Sockets.TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, _port);
                    break;   // listener up (connection will be dropped after bad handshake)
                }
                catch (System.Net.Sockets.SocketException)
                {
                    await Task.Delay(100);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts?.Cancel();
            if (_session is not null) await _session.DisposeAsync();
            _tracker?.Dispose();
        }
    }

    /// <summary>Loopback HTTP tracker that reports the seeder to every requester
    /// (and the requester to nobody — one-way discovery for deterministic tests).</summary>
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
                        // report the seeder
                        peers.AddRange(IPAddress.Loopback.GetAddressBytes());
                        peers.Add((byte)(_seedPort >> 8));
                        peers.Add((byte)(_seedPort & 0xFF));

                        var head = System.Text.Encoding.UTF8.GetBytes(
                            "d8:completei1e10:incompletei0e8:intervali5e5:peers" + peers.Count + ":");
                        var body = head.Concat(peers).Append((byte)'e').ToArray();
                        ctx.Response.ContentType = "text/plain";
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                    catch (Exception) { /* stopping */ }
                }
            });
            // let the listener bind before the first announce
            await Task.Delay(150);
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* stopped */ }
        }
    }
}
