using System.Net;
using System.Security.Cryptography;
using Nova.Domain;
using Nova.Engine;
using Nova.Engine.Torrent;
using Nova.Persistence;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>
/// v1.4.0 BitTorrent engine integration: real DownloadEngine.Add → TaskRunner dispatch →
/// TorrentTask → completion events, over a local swarm (loopback tracker + own seeder).
/// </summary>
[Xunit.Collection("TorrentSerial")]
public class TorrentEngineTests : IAsyncLifetime
{
    private string _dir = null!;
    private byte[] _payload = null!;
    private string _payloadPath = null!;
    private TorrentMeta _meta = null!;
    private HttpListener _tracker = null!;
    private int _trackerPort;
    private int _seedPort;
    private TorrentSession? _seeder;
    private EngineFixture _fx = null!;
    private TestSink _sink = null!;
    private IDisposable _sub = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nova-bt-eng-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _payload = RandomNumberGenerator.GetBytes(900_001);
        _payloadPath = Path.Combine(_dir, "seed.bin");
        await File.WriteAllBytesAsync(_payloadPath, _payload);

        _seedPort = FreePort();
        _trackerPort = FreePort();

        // tracker → answers every announce with the seeder
        _tracker = new HttpListener();
        _tracker.Prefixes.Add($"http://127.0.0.1:{_trackerPort}/");
        _tracker.Start();
        _ = Task.Run(async () =>
        {
            while (_tracker.IsListening)
            {
                try
                {
                    var ctx = await _tracker.GetContextAsync();
                    var peers = new List<byte>();
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

        // seeder for the whole fixture
        var raw = TorrentCreator.CreateSingleFile(_payloadPath,
            new[] { $"http://127.0.0.1:{_trackerPort}/announce" }, pieceLength: 65536);
        _meta = TorrentMeta.Parse(raw);
        _seeder = new TorrentSession(_meta, new TorrentSessionOptions
        {
            DestinationDir = _dir,
            Trackers = _meta.Trackers,
            EnableDht = false,
            ListenPort = _seedPort,
            MaxPeers = 16,
        });
        _ = Task.Run(() => _seeder.SeedAsync(CancellationToken.None));
        await Task.Delay(250);

        _fx = new EngineFixture();
        // no live DHT inside unit tests (deterministic, offline)
        new SettingsRepository(_fx.Db).Set("bt_dht", "0");
        _sink = new TestSink();
        _sub = _fx.Bus.Subscribe(_sink);
    }

    public async Task DisposeAsync()
    {
        _sub.Dispose();
        await _fx.DisposeAsync();
        if (_seeder is not null) await _seeder.DisposeAsync();
        try { _tracker.Stop(); } catch { /* stopped */ }
        try { Directory.Delete(_dir, true); } catch { /* leftovers */ }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start(1);
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task<string> AddAndWaitCompleted(NewDownload spec)
    {
        var res = _fx.Engine.Add(spec);
        Assert.True(res.Ok, res.Error?.ToString());
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        var snap = _sink.WaitState(key, DownloadState.Completed, timeoutMs: 90_000);
        Assert.NotNull(snap);
        return key;
    }

    [Fact]
    public async Task Engine_Torrent_File_Task_Completes_Byte_Exact()
    {
        var torrentPath = Path.Combine(_dir, "seed.bin.torrent");
        await File.WriteAllBytesAsync(torrentPath, _meta.RawBytes);

        var key = await AddAndWaitCompleted(new NewDownload
        {
            Url = torrentPath,                                   // local .torrent path
            DestinationDir = Path.Combine(_dir, "dl1"),
        });

        var task = _fx.Engine.Get(key)!;
        Assert.Equal(DownloadKind.Torrent, task.Kind);
        Assert.Equal(_meta.InfoHashHex, task.BtInfoHash);
        Assert.Equal(_payload.Length, task.TotalBytes);
        Assert.Equal(_payload.Length, task.ReceivedBytes);
        Assert.True(File.Exists(task.FinalPath));
        Assert.Equal(_payload, await File.ReadAllBytesAsync(task.FinalPath));
        Assert.False(File.Exists(task.FinalPath + ".novadl"));
    }

    [Fact]
    public async Task Engine_Magnet_Task_Fetches_Metadata_And_Completes()
    {
        var magnet = TorrentMeta.BuildMagnet(_meta.InfoHash, "seed.bin", _meta.Trackers);

        var key = await AddAndWaitCompleted(new NewDownload
        {
            Url = magnet,                                        // magnet: with tracker
            DestinationDir = Path.Combine(_dir, "dl2"),
        });

        var task = _fx.Engine.Get(key)!;
        Assert.Equal(DownloadKind.Torrent, task.Kind);
        Assert.Equal(_meta.InfoHashHex, task.BtInfoHash);
        Assert.Equal("seed.bin", task.FileName);
        // the metadata sidecar persisted for resume/seeding
        Assert.True(File.Exists(Path.Combine(task.DestinationDir, _meta.InfoHashHex + ".torrent")));
        Assert.Equal(_payload, await File.ReadAllBytesAsync(task.FinalPath));
    }

    [Fact]
    public async Task Engine_Torrent_Snapshot_Exposes_BtFields()
    {
        var torrentPath = Path.Combine(_dir, "seed.bin.torrent");
        await File.WriteAllBytesAsync(torrentPath, _meta.RawBytes);
        var key = await AddAndWaitCompleted(new NewDownload
        {
            Url = torrentPath,
            DestinationDir = Path.Combine(_dir, "dl3"),
        });

        var task = _fx.Engine.Get(key)!;
        var snap = TaskSnapshot.From(task);
        Assert.Equal("torrent", snap.Kind);
        Assert.Equal(_meta.InfoHashHex, snap.BtInfoHash);

        // magnet with seed-after flag survives the Add → spec round-trip
        Assert.False(snap.SeedAfter);
        var res = _fx.Engine.Add(new NewDownload
        {
            Url = TorrentMeta.BuildMagnet(_meta.InfoHash, null, _meta.Trackers),
            DestinationDir = Path.Combine(_dir, "dl4"),
            SeedAfter = true,
            AutoStart = false,
        });
        Assert.True(res.Ok);
        Assert.True(_fx.Engine.Get(res.Value!.TaskKey)!.SeedAfter);
    }
}
