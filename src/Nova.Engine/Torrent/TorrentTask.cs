using System.Net;
using Nova.Domain;

namespace Nova.Engine.Torrent;

/// <summary>
/// TaskRunner adapter for BitTorrent tasks: resolves metadata (local .torrent, magnet
/// sidecar, or swarm via ut_metadata), configures the session from task properties +
/// engine settings, and reports progress in the same (deltaBytes, rateBps) contract
/// as FtpTask/SftpTask.
/// </summary>
public static class TorrentTask
{
    /// <summary>True for magnet URIs and local .torrent paths/file-URLs.</summary>
    public static bool IsTorrentSource(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return true;
        var raw = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? url[7..]
            : url;
        return raw.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
               && File.Exists(raw);
    }

    /// <summary>Runs a torrent download to completion. Returns the final path (file or folder).</summary>
    public static async Task<string> RunAsync(DownloadTask task, Action<long, long> onProgress,
        CancellationToken ct, Func<string, string?>? settings = null,
        IReadOnlyList<TorrentPeer>? staticPeers = null)
    {
        var meta = await ResolveMetaAsync(task, ct, settings, onProgress, staticPeers);

        task.TotalBytes = meta.TotalLength;
        task.SegmentsTotal = meta.PieceCount;
        task.SegmentCount = meta.PieceCount;
        if (string.IsNullOrEmpty(task.FileName) || task.FileName == "download")
            task.FileName = meta.Name;
        task.BtInfoHash = meta.InfoHashHex;

        var metaTimeout = ParseLong(settings?.Invoke("bt_metadata_timeout"));
        var listenPort = ParseLong(settings?.Invoke("bt_listen_port"));
        var opt = new TorrentSessionOptions
        {
            DestinationDir = task.DestinationDir,
            Sequential = task.Sequential,
            SeedAfterComplete = task.SeedAfter,
            MaxPeers = task.MaxConnections > 0 ? Math.Clamp(task.MaxConnections, 2, 64) : 12,
            Trackers = meta.Trackers,
            StaticPeers = staticPeers ?? Array.Empty<TorrentPeer>(),
            EnableDht = settings?.Invoke("bt_dht") is { Length: > 0 } s ? s != "0" : true,
            UploadCapBytes = ParseLong(settings?.Invoke("bt_upload_cap")),
            MetadataTimeout = TimeSpan.FromSeconds(metaTimeout > 0 ? metaTimeout : 60),
        };
        if (opt.SeedAfterComplete || listenPort > 0)
        {
            opt.ListenPort = (int)(listenPort > 0 ? listenPort : FindFreePort());
        }
        else if (settings?.Invoke("bt_listen") == "1")
        {
            opt.ListenPort = FindFreePort();
        }

        var session = new TorrentSession(meta, opt);
        try
        {
            session.Progress += (delta, rate) => onProgress(delta, rate);
            var finalPath = await session.DownloadAsync(ct);

            // resume-safe counters reflect verified pieces
            task.ReceivedBytes = meta.TotalLength;
            task.SegmentsDone = meta.PieceCount;
            return finalPath;
        }
        finally
        {
            if (opt.SeedAfterComplete && session.Completed)
            {
                // keep serving after the task completes; engine shutdown stops it
                TorrentSeederRegistry.Register(task.TaskKey, session);
            }
            else
            {
                await session.DisposeAsync();
            }
        }
    }

    private static long ParseLong(string? s) =>
        long.TryParse(s, out var v) ? v : 0;

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start(1);
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<TorrentMeta> ResolveMetaAsync(DownloadTask task,
        CancellationToken ct, Func<string, string?>? settings,
        Action<long, long> onProgress, IReadOnlyList<TorrentPeer>? staticPeers)
    {
        // 1) .torrent file (local path or file:// URL)
        if (!task.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var path = task.Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? task.Url[7..]
                : task.Url;
            var raw = await File.ReadAllBytesAsync(path, ct);
            return TorrentMeta.Parse(raw);
        }

        var magnet = TorrentMeta.ParseMagnet(task.Url);

        // 2) persisted sidecar from an earlier magnet run (resume / already fetched)
        var sidecar = SidecarPath(task, magnet.InfoHashHex);
        if (File.Exists(sidecar))
            try
            {
                return TorrentMeta.Parse(await File.ReadAllBytesAsync(sidecar, ct));
            }
            catch (InvalidDataException) { /* corrupt — refetch */ }

        // 3) fetch metadata from the swarm (BEP-9 ut_metadata via tracker/DHT/static peers)
        var metaTimeout = ParseLong(settings?.Invoke("bt_metadata_timeout"));
        var opt = new TorrentSessionOptions
        {
            Trackers = magnet.Trackers,
            StaticPeers = staticPeers ?? Array.Empty<TorrentPeer>(),
            EnableDht = settings?.Invoke("bt_dht") is { Length: > 0 } s ? s != "0" : true,
            MetadataTimeout = TimeSpan.FromSeconds(metaTimeout > 0 ? metaTimeout : 60),
        };
        var meta = await TorrentSession.FetchMetadataAsync(magnet, opt, ct);
        if (string.IsNullOrEmpty(task.FileName) || task.FileName == "download")
            task.FileName = meta.Name;
        // persist for resume + seeding
        Directory.CreateDirectory(task.DestinationDir);
        await File.WriteAllBytesAsync(SidecarPath(task, meta.InfoHashHex), meta.RawBytes, ct);
        return meta;
    }

    internal static string SidecarPath(DownloadTask task, string infoHashHex) =>
        Path.Combine(string.IsNullOrWhiteSpace(task.DestinationDir) ? "." : task.DestinationDir,
            $"{infoHashHex}.torrent");
}

/// <summary>Keeps seeding sessions alive after task completion until engine shutdown
/// or explicit stop. Values are disposed when removed.</summary>
public static class TorrentSeederRegistry
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, TorrentSession> Sessions = new();
    private static readonly List<(string Key, Task Task)> SeedTasks = new();

    public static void Register(string taskKey, TorrentSession session)
    {
        var task = Task.Run(() => session.SeedAsync(CancellationToken.None), CancellationToken.None);
        lock (Lock)
        {
            Sessions[taskKey] = session;
            SeedTasks.Add((taskKey, task));
        }
    }

    public static void Stop(string taskKey)
    {
        TorrentSession? session;
        lock (Lock)
        {
            if (!Sessions.Remove(taskKey, out session)) return;
            SeedTasks.RemoveAll(t => t.Key == taskKey);
        }
        _ = session.DisposeAsync();
    }

    public static int ActiveCount
    {
        get { lock (Lock) return Sessions.Count; }
    }

    /// <summary>Stops everything (engine shutdown).</summary>
    public static void StopAll()
    {
        List<TorrentSession> all;
        lock (Lock)
        {
            all = Sessions.Values.ToList();
            Sessions.Clear();
            SeedTasks.Clear();
        }
        foreach (var s in all) _ = s.DisposeAsync();
    }
}
