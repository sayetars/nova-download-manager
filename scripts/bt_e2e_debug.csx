using System.Net;
using System.Security.Cryptography;
using Nova.Engine.Torrent;

var dir = Path.Combine("/tmp", "bt-e2e-debug-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var payload = RandomNumberGenerator.GetBytes(1_600_003);
var payloadPath = Path.Combine(dir, "payload.bin");
File.WriteAllBytes(payloadPath, payload);

int FindFreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start(1);
    var p = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

var seedPort = FindFreePort();
var trackerPort = FindFreePort();
var announceUrl = $"http://127.0.0.1:{trackerPort}/announce";

var raw = TorrentCreator.CreateSingleFile(payloadPath, new[] { announceUrl }, pieceLength: 65536);
var meta = TorrentMeta.Parse(raw);
Console.WriteLine($"meta: {meta.PieceCount} pieces, {meta.TotalLength} bytes, ih={meta.InfoHashHex[..12]}…");

// tracker harness
var tracker = new HttpListener();
tracker.Prefixes.Add($"http://127.0.0.1:{trackerPort}/");
tracker.Start();
_ = Task.Run(async () =>
{
    while (tracker.IsListening)
    {
        try
        {
            var ctx = await tracker.GetContextAsync();
            Console.WriteLine($"[tracker] announce: {ctx.Request.Url!.Query[..Math.Min(60, ctx.Request.Url.Query.Length)]}");
            var peers = new List<byte>();
            peers.AddRange(IPAddress.Loopback.GetAddressBytes());
            peers.Add((byte)(seedPort >> 8));
            peers.Add((byte)(seedPort & 0xFF));
            var head = System.Text.Encoding.UTF8.GetBytes(
                "d8:completei1e10:incompletei0e8:intervali5e5:peers" + peers.Count + ":");
            var body = head.Concat(peers).Append((byte)'e').ToArray();
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
        catch (Exception e) { Console.WriteLine($"[tracker] err {e.Message}"); }
    }
});

// seeder
var seederSession = new TorrentSession(meta, new TorrentSessionOptions
{
    DestinationDir = dir,
    Trackers = meta.Trackers,
    EnableDht = false,
    ListenPort = seedPort,
    MaxPeers = 16,
});
seederSession.Log += m => Console.WriteLine($"[seeder] {m}");
seederSession.PeerCountChanged += c => Console.WriteLine($"[seeder] peers={c}");
_ = Task.Run(() => seederSession.SeedAsync(CancellationToken.None));
await Task.Delay(300);

// downloader
var dest = Path.Combine(dir, "dl");
Directory.CreateDirectory(dest);
var dl = new TorrentSession(meta, new TorrentSessionOptions
{
    DestinationDir = dest,
    Trackers = meta.Trackers,
    EnableDht = false,
    MaxPeers = 8,
});
dl.Log += m => Console.WriteLine($"[dl] {m}");
dl.PeerCountChanged += c => Console.WriteLine($"[dl] peers={c}");
var progressTotal = 0L;
dl.Progress += (delta, rate) =>
{
    var t = Interlocked.Add(ref progressTotal, delta);
    if (t % (256 * 1024) < 16384) Console.WriteLine($"[dl] {t / 1024} KiB @ {rate / 1024} KiB/s");
};

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
try
{
    var final = await dl.DownloadAsync(cts.Token);
    Console.WriteLine($"FINAL: {final}");
    var got = await File.ReadAllBytesAsync(final);
    Console.WriteLine(got.SequenceEqual(payload) ? "BYTE-EXACT ✓" : "MISMATCH ✗");
}
catch (OperationCanceledException)
{
    Console.WriteLine($"TIMEOUT at {Interlocked.Read(ref progressTotal) / 1024} KiB of {payload.Length / 1024}");
}

await dl.DisposeAsync();
await seederSession.DisposeAsync();
try { Directory.Delete(dir, true); } catch { }
