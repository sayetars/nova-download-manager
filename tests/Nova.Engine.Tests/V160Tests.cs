using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Nova.Domain;
using Nova.Engine.Torrent;
using Nova.Engine.YtDlp;
using Nova.Persistence;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>v1.6.0: handshake reserved-flag bits, DHT peer-store persistence,
/// and the yt-dlp native-merge runner (fake binary E2E).</summary>
public class V160Tests : IAsyncLifetime
{
    private string _tmp = "";

    public Task InitializeAsync()
    {
        // NOTE: /tmp is mounted noexec in some sandboxes — create scratch space next to
        // the test binary (a filesystem where Process.Start of scripts is allowed).
        _tmp = Path.Combine(AppContext.BaseDirectory, "v160tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        DhtPeerStoreRegistry.Reset();
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ reserved flag bits
    [Fact]
    public void ReservedBits_WeAdvertise_Only_What_We_Implement()
    {
        var r = PeerWireClient.OurReserved.ToArray();
        Assert.Equal(8, r.Length);
        Assert.Equal(0x10, r[5]);          // BEP-10 extension protocol
        Assert.Equal(0x01, r[7]);          // BEP-5 DHT
        Assert.Equal(0, r[7] & 0x04);      // FAST not claimed
        Assert.Equal(0, r[0] | r[1] | r[2] | r[3] | r[4] | r[6]);
    }

    [Fact]
    public void ReservedBits_ParseHelpers()
    {
        var dhtFast = new byte[8];
        dhtFast[5] = 0x10; dhtFast[7] = 0x05;           // EXT + DHT + FAST
        Assert.True(PeerWireClient.ParseSupportsDht(dhtFast));
        Assert.True(PeerWireClient.ParseSupportsExtensionProtocol(dhtFast));
        Assert.True(PeerWireClient.ParseSupportsFast(dhtFast));

        var none = new byte[8];                          // legacy client
        Assert.False(PeerWireClient.ParseSupportsDht(none));
        Assert.False(PeerWireClient.ParseSupportsExtensionProtocol(none));
        Assert.False(PeerWireClient.ParseSupportsFast(none));

        var extOnly = new byte[8];
        extOnly[5] = 0x10;
        Assert.False(PeerWireClient.ParseSupportsDht(extOnly));
        Assert.True(PeerWireClient.ParseSupportsExtensionProtocol(extOnly));
    }

    [Fact]
    public async Task Handshake_Parses_Peer_Reserved_Flags_Both_Sides()
    {
        var infoHash = RandomNumberGenerator.GetBytes(20);
        var serverPeerId = System.Text.Encoding.UTF8.GetBytes("-SV1600-serverpeer1");
        var clientPeerId = System.Text.Encoding.UTF8.GetBytes("-CL1600-clientpeer1");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            await using var wire = await PeerWireClient.AcceptAsync(tcp, infoHash, serverPeerId,
                -1, CancellationToken.None);
            return (wire.PeerSupportsDht, wire.PeerSupportsExtensionProtocol);
        });

        var ep = (IPEndPoint)listener.LocalEndpoint;
        await using var client = PeerWireClient.CreateConnected(
            new TorrentPeer(ep.Address, ep.Port), infoHash, clientPeerId);
        Assert.True(await client.HandshakeAsync(CancellationToken.None));

        // the peer is OUR own AcceptAsync → it must see our v1.6.0 advertisement…
        var serverView = await accepted;
        Assert.True(serverView.PeerSupportsDht);
        Assert.True(serverView.PeerSupportsExtensionProtocol);
        // …and we must see the peer's (our) advertisement back
        Assert.True(client.PeerSupportsDht);
        Assert.True(client.PeerSupportsExtensionProtocol);
    }

    // ------------------------------------------------------------ DHT peer store
    [Fact]
    public void DhtClient_PeerStore_Export_Import_Roundtrip()
    {
        using var dht = new DhtClient();
        var hashHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        var compact1 = new byte[] { 127, 0, 0, 1, 0x1A, 0xE1 };
        var compact2 = new byte[] { 192, 168, 1, 7, 0x1B, 0x59 };
        var compact3 = new byte[] { 10, 1, 2, 3, 0x1C, 0x01 };

        var entries = new List<PeerStoreEntry>
        {
            new(hashHex, compact1, DateTime.UtcNow.AddMinutes(20)),
            new(hashHex, compact2, DateTime.UtcNow.AddMinutes(10)),
            new("DEADBEEF", compact3, DateTime.UtcNow.AddMinutes(-1)),  // expired → dropped
            new("BAADF00D", new byte[3], DateTime.UtcNow.AddMinutes(5)), // bad length → dropped
        };
        dht.ImportPeerStore(entries);

        var infoHash = Convert.FromHexString(hashHex);
        Assert.Equal(2, dht.StoredPeerCount(infoHash));

        var exported = dht.ExportPeerStore();
        Assert.Equal(2, exported.Count);
        Assert.All(exported, e => Assert.Equal(hashHex, e.InfoHashHex));
        Assert.Contains(exported, e => e.Compact.AsSpan().SequenceEqual(compact1));
        Assert.Contains(exported, e => e.Compact.AsSpan().SequenceEqual(compact2));

        // re-import into a fresh node keeps only live rows
        using var dht2 = new DhtClient();
        dht2.ImportPeerStore(exported);
        Assert.Equal(2, dht2.StoredPeerCount(infoHash));
    }

    [Fact]
    public async Task DhtPeerStoreDb_Persists_Across_Reopen()
    {
        var dbPath = Path.Combine(_tmp, "dht.db");
        var hashHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        var compact = new byte[] { 203, 0, 113, 9, 0x22, 0xB0 };

        await using (var db = new NovaDatabase(dbPath))
        {
            Assert.Equal(11, db.SchemaVersion);
            var store = new DhtPeerStoreDb(db);
            store.SaveAll(new List<PeerStoreEntry>
            {
                new(hashHex, compact, DateTime.UtcNow.AddMinutes(25)),
                new(hashHex, new byte[] { 1, 2, 3, 4, 5, 6 }, DateTime.UtcNow.AddMinutes(-1)),
            });
        }

        await using (var db2 = new NovaDatabase(dbPath))   // "restart"
        {
            var loaded = new DhtPeerStoreDb(db2).LoadAll();
            var row = Assert.Single(loaded);          // expired row must not come back
            Assert.Equal(hashHex, row.InfoHashHex);
            Assert.True(row.Compact.AsSpan().SequenceEqual(compact));
        }
    }

    [Fact]
    public void DhtPeerStoreRegistry_Configure_And_Reset()
    {
        try
        {
            var saved = new List<IReadOnlyList<PeerStoreEntry>>();
            IReadOnlyList<PeerStoreEntry> Load() => new List<PeerStoreEntry>
                { new("AABB", new byte[] { 1, 1, 1, 1, 0, 1 }, DateTime.UtcNow.AddMinutes(5)) };
            void Save(IReadOnlyList<PeerStoreEntry> e) => saved.Add(e);

            DhtPeerStoreRegistry.Configure(Load, Save);
            Assert.Single(Load());                    // delegates installed and callable
        }
        finally
        {
            DhtPeerStoreRegistry.Reset();
        }
    }

    // ------------------------------------------------------------ yt-dlp selection + progress
    [Fact]
    public void YtDlpNativeSelection_Parse_Encode_Phases()
    {
        var json = """
            {"source":"ytdlp","mode":"native","page":"https://example.com/watch?v=1",
             "format_id":"137+251","title":"Big Buck Bunny","ext":"mp4","total_bytes":1048576}
            """;
        var sel = YtDlpNativeSelection.TryParse(json);
        Assert.NotNull(sel);
        Assert.Equal("137+251", sel!.FormatId);
        Assert.Equal(2, sel.Phases);
        Assert.Equal(1048576, sel.TotalBytes);

        var round = YtDlpNativeSelection.TryParse(sel.Encode());
        Assert.NotNull(round);
        Assert.Equal(sel.FormatId, round!.FormatId);
        Assert.Equal(sel.Title, round.Title);

        // non-native / malformed payloads are rejected
        Assert.Null(YtDlpNativeSelection.TryParse(null));
        Assert.Null(YtDlpNativeSelection.TryParse("{\"source\":\"engine\",\"mode\":\"native\"}"));
        Assert.Null(YtDlpNativeSelection.TryParse("{\"source\":\"ytdlp\",\"mode\":\"native\"}"));
        Assert.Null(YtDlpNativeSelection.TryParse("not json"));
        Assert.Equal(1, YtDlpNativeSelection.TryParse(
            """{"source":"ytdlp","mode":"native","format_id":"18"}""")!.Phases);
    }

    [Theory]
    [InlineData("[download]  42.3% of ~ 10.00MiB at 1.23MiB/s ETA 00:12", true, 0.423, 10485760)]
    [InlineData("[download]   4.50MiB at 1.20MiB/s ETA 00:04", false, 0, 4718592)]
    [InlineData("[download] 100% of 10.00MiB in 00:05", true, 1.0, 10485760)]
    [InlineData("[download] Destination: /tmp/x.mp4", false, 0, 0)]
    public void YtDlpProgressLine_Parse_Variants(string line, bool hasFraction,
        double frac, long totalOrRaw)
    {
        var pl = YtDlpProgressLine.TryParse(line);
        if (!hasFraction && totalOrRaw == 0)
        {
            Assert.Null(pl);
            return;
        }
        Assert.NotNull(pl);
        Assert.Equal(hasFraction, pl!.HasFraction);
        if (hasFraction)
        {
            Assert.Equal(frac, pl.Fraction, 3);
            Assert.Equal(totalOrRaw, pl.PhaseTotalBytes);
        }
        else
        {
            Assert.Equal(totalOrRaw, pl.PhaseReceivedBytes);
        }
    }

    // ------------------------------------------------------------ yt-dlp runner E2E (fake binary)
    [Fact]
    public async Task YtDlpRunner_FakeBinary_Merges_Two_Phases_And_Reports_Progress()
    {
        // POSIX variant: parse -o from argv, emit yt-dlp progress lines, write both
        // format files and concatenate them into the merged output.
        const string sh = """
            #!/bin/sh
            out=""
            prev=""
            for a in "$@"; do
              if [ "$prev" = "-o" ]; then out="$a"; fi
              prev="$a"
            done
            base=$(printf '%s' "$out" | sed 's/\.%(ext)s$//')
            f1="$base.f137.mp4"
            f2="$base.f251.m4a"
            merged="$base.mp4"
            echo "[download] Destination: $f1"
            echo "[download]  40.0% of    1.00MiB at    1.00MiB/s ETA 00:01"
            echo "[download] 100.0% of    1.00MiB in 00:01"
            head -c 1048576 /dev/zero > "$f1"
            echo "[download] Destination: $f2"
            echo "[download]  50.0% of  512.00KiB at    1.00MiB/s ETA 00:00"
            echo "[download] 100.0% of  512.00KiB in 00:00"
            head -c 524288 /dev/zero > "$f2"
            echo "[Merger] Merging formats into \"$merged\""
            cat "$f1" "$f2" > "$merged"
            exit 0
            """;
        // Windows variant: same behaviour in batch. Args are parsed with a SHIFT loop
        // (a ")" inside the ".%(ext)s" template would break FOR-set parsing); "%%" is a
        // literal percent in .cmd files; fsutil makes exact-size zero files; copy /b
        // concatenates. ".%(ext)s" is the 7-char suffix stripped from the -o value.
        const string cmd = """
            @echo off
            setlocal enabledelayedexpansion
            set "out="
            :parse
            if "%~1"=="" goto parsed
            if "%~1"=="-o" set "out=%~2"
            shift
            goto parse
            :parsed
            rem ".%(ext)s" is EIGHT trailing chars: . % ( e x t ) s
            set "base=!out:~0,-8!"
            set "f1=!base!.f137.mp4"
            set "f2=!base!.f251.m4a"
            set "merged=!base!.mp4"
            echo [download] Destination: !f1!
            echo [download]  40.0%% of    1.00MiB at    1.00MiB/s ETA 00:01
            echo [download] 100.0%% of    1.00MiB in 00:01
            fsutil file createnew "!f1!" 1048576 >nul
            echo [download] Destination: !f2!
            echo [download]  50.0%% of  512.00KiB at    1.00MiB/s ETA 00:00
            echo [download] 100.0%% of  512.00KiB in 00:00
            fsutil file createnew "!f2!" 524288 >nul
            echo [Merger] Merging formats into "!merged!"
            copy /b "!f1!"+"!f2!" "!merged!" >nul
            endlocal
            exit /b 0
            """;
        var bin = await FakeBinary.WriteAsync(_tmp, "fake-ytdlp", sh, cmd);

        var dest = Path.Combine(_tmp, "dest");
        Directory.CreateDirectory(dest);
        var task = new DownloadTask
        {
            Url = "https://example.com/watch?v=abc",
            FileName = "myvideo",
            DestinationDir = dest,
        };
        var sel = new YtDlpNativeSelection
        {
            Page = "https://example.com/watch?v=abc",
            FormatId = "137+251",
            Title = "Test Video",
        };

        long progressBytes = 0;
        var bridge = new YtDlpBridge(explicitPath: bin);
        var finalPath = await YtDlpRunner.RunAsync(task, sel, bridge,
            new YtDlpRunner.Hooks { OnProgress = (delta, _) => Interlocked.Add(ref progressBytes, delta) },
            CancellationToken.None, CancellationToken.None, CancellationToken.None);

        Assert.Equal(Path.Combine(dest, "myvideo.mp4"), finalPath);
        var size = new FileInfo(finalPath).Length;
        Assert.Equal(1048576 + 524288, size);
        Assert.Equal(size, task.ReceivedBytes);
        Assert.Equal(size, task.TotalBytes);
        Assert.True(progressBytes > 0, "progress hook must fire");
    }

    [Fact]
    public async Task YtDlpRunner_FakeBinary_Failure_Maps_To_Structured_Error()
    {
        const string sh = """
            #!/bin/sh
            echo "ERROR: unable to download video data: HTTP Error 403: Forbidden" >&2
            exit 1
            """;
        const string cmd = """
            @echo off
            echo ERROR: unable to download video data: HTTP Error 403: Forbidden 1>&2
            exit /b 1
            """;
        var bin = await FakeBinary.WriteAsync(_tmp, "fake-ytdlp-fail", sh, cmd);

        var task = new DownloadTask
        {
            Url = "https://example.com/watch?v=xyz",
            FileName = "vid",
            DestinationDir = _tmp,
        };
        var bridge = new YtDlpBridge(explicitPath: bin);
        var ex = await Assert.ThrowsAsync<YtDlpException>(() => YtDlpRunner.RunAsync(task,
            new YtDlpNativeSelection { FormatId = "18" }, bridge,
            new YtDlpRunner.Hooks { OnProgress = (_, _) => { } },
            CancellationToken.None, CancellationToken.None, CancellationToken.None));
        Assert.Equal(ErrorCategory.Media, ex.Category);
    }
}
