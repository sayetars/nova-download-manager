using Nova.Core;
using Nova.Core.Models;
using Nova.Core.Protocol;
using Nova.Engine;
using Nova.Engine.Bridges;
using Nova.Ipc;
using Nova.Scheduler;
using Nova.Storage;

namespace Nova.Host;

/// <summary>
/// Bootstraps the whole system: storage, engine, scheduler, IPC server
/// (named pipe on Windows / UDS on Linux) and registers all RPC routes.
/// Also bridges engine events to every connected client.
/// </summary>
public sealed class EngineHost
{
    private readonly string _dbPath;
    private DownloadEngine _engine = null!;
    private IpcServer _ipc = null!;
    private Nova.Scheduler.Scheduler _scheduler = null!;
    private SettingsRepository _settingsRepo = null!;
    private Database _db = null!;
    private Timer? _speedBroadcaster;

    public EngineHost(string dbPath) => _dbPath = dbPath;

    public async Task RunAsync(CancellationToken ct)
    {
        var dataDir = GetDataDir();
        _db = new Database(_dbPath);
        _engine = new DownloadEngine(_dbPath);
        _scheduler = new Nova.Scheduler.Scheduler(_db, () => _engine);
        _settingsRepo = new SettingsRepository(_db);

        var dispatcher = BuildDispatcher();
        _ipc = new IpcServer(dispatcher);
        BridgeEvents();

        _speedBroadcaster = new Timer(_ =>
        {
            try
            {
                _ = _ipc.BroadcastAsync(MessageTypes.EvGlobalSpeed,
                    new { download = SumSpeed(), tasks = ActiveCount() });
            }
            catch { /* broadcast races with shutdown */ }
        }, null, 1000, 1000);

        Console.Error.WriteLine($"[nova] engine listening on {_ipc.Endpoint}");
        await _ipc.ListenAsync();
    }

    public static string GetDataDir()
        => Environment.GetEnvironmentVariable("NOVA_DATA_DIR")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");

    // ------------------------------------------------------------------
    // RPC routes — every handler is sync-safe (never blocks the dispatcher
    // longer than a DB write) and returns a serializable object.
    // ------------------------------------------------------------------

    private IpcDispatcher BuildDispatcher()
    {
        var d = new IpcDispatcher();

        d.Register(MessageTypes.Hello, _ => Task.FromResult<object?>(new
        {
            v = Json.ProtocolVersion,
            engine = "nova",
            version = "1.0.0",
            capabilities = new[]
            {
                "http", "https", "ftp", "ftps", "sftp", "hls", "dash", "ytdlp",
                "sitegrabber", "scheduler", "jalali", "debrid", "containers",
                "checksums", "speedlimit", "queues", "categories", "preview"
            }
        }));

        d.Register(MessageTypes.Ping, _ => Task.FromResult<object?>(new { pong = true }));

        d.Register<AddUrlRequest>(MessageTypes.AddUrl, (req, _) =>
            Task.FromResult<object?>(_engine.AddUrlAsync(req).GetAwaiter().GetResult()));

        d.Register<AddBatchRequest>(MessageTypes.AddBatch, (req, _) =>
            Task.FromResult<object?>(req.Urls
                .Select(u => _engine.AddUrlAsync(new AddUrlRequest(u, SaveDirectory: req.SaveDirectory, Category: req.Category))
                    .GetAwaiter().GetResult().Id)
                .ToList()));

        d.Register<AddVideoRequest>(MessageTypes.AddVideo, (req, _) =>
            Task.FromResult<object?>(_engine.AddVideoAsync(req).GetAwaiter().GetResult()));

        d.Register(MessageTypes.List, _ => Task.FromResult<object?>(_engine.List()));

        d.Register<string>(MessageTypes.Get, (id, _) =>
            Task.FromResult<object?>(_engine.Get(id) ?? throw new KeyNotFoundException($"task {id} not found")));

        d.Register<string>(MessageTypes.Pause, (id, _) => Task.FromResult<object?>(_engine.Pause(id)));
        d.Register<string>(MessageTypes.Resume, (id, _) => Task.FromResult<object?>(_engine.Resume(id)));
        d.Register<string>(MessageTypes.Cancel, (id, _) => Task.FromResult<object?>(_engine.Cancel(id, deleteFiles: false)));
        d.Register<string>(MessageTypes.Remove, (id, _) => Task.FromResult<object?>(_engine.Remove(id, deleteFiles: false)));
        d.Register<string>(MessageTypes.Retry, (id, _) => Task.FromResult<object?>(_engine.Resume(id)));

        d.Register<long>(MessageTypes.SetGlobalSpeedLimit, (limit, _) =>
        {
            _engine.SetGlobalSpeedLimit(limit);
            return Task.FromResult<object?>(true);
        });

        d.Register<object>(MessageTypes.AddTorrent, (_, _) =>
            throw new NotSupportedException("BitTorrent worker ships in the aria2-compat bundle (see ROADMAP)"));

        d.Register<object>(MessageTypes.AddMetalink, (_, _) =>
            throw new NotSupportedException("Metalink worker ships in the aria2-compat bundle (see ROADMAP)"));

        d.Register<GrabSiteRequest>(MessageTypes.GrabSite, (req, ct) =>
        {
            var grabber = new SiteGrabber(DownloadEngine.BuildHttpClient(Settings.LoadDefault()));
            var files = new List<object>();
            grabber.FileFound += f => files.Add(new { f.Url, f.AbsolutePath, f.SizeBytes });
            _ = Task.Run(() => grabber.GrabAsync(new SiteGrabber.GrabOptions
            {
                StartUrl = req.StartUrl,
                MaxDepth = req.MaxDepth,
                IncludeFilter = req.IncludeFilter,
                ExcludeFilter = req.ExcludeFilter,
                FollowOtherDomains = req.FollowOtherDomains,
                SaveDirectory = req.SaveDirectory ?? Path.Combine(GetDataDir(), "grabber")
            }, ct), ct);
            return Task.FromResult<object?>(new { started = true });
        });

        d.Register<DebridUnrestrictRequest>(MessageTypes.DebridUnrestrict, async (req, ct) =>
        {
            var link = await _engine.Debrid.UnrestrictAsync(req.Provider, req.ApiKey, req.Url, ct);
            return (object?)link;
        });

        d.Register<string>(MessageTypes.ContainerOpen, async (filePath, ct) =>
            (object?)await ContainerFile.OpenAsync(filePath, ct));

        d.Register<AddVideoRequest>(MessageTypes.ResolveTracks, async (req, ct) =>
            await ResolveTracksAsync(req, ct));

        d.Register<ScheduleUpsertRequest>(MessageTypes.ScheduleUpsert, (req, _) =>
        {
            var rule = new Nova.Scheduler.Scheduler.ScheduleRule
            {
                Id = req.Id ?? Guid.NewGuid().ToString("N"), Name = req.Name, Kind = req.Kind, TimeExpr = req.TimeExpr,
                DaysOfWeek = req.DaysOfWeek, SpeedLimitBytes = req.SpeedLimitBytes,
                QueueName = req.QueueName, Enabled = req.Enabled, Calendar = req.Calendar ?? "gregorian"
            };
            _scheduler.UpsertRule(rule);
            return Task.FromResult<object?>(rule);
        });

        d.Register<string>(MessageTypes.ScheduleDelete, (id, _) =>
        {
            _scheduler.DeleteRule(id);
            return Task.FromResult<object?>(true);
        });

        d.Register(MessageTypes.ScheduleList, _ => Task.FromResult<object?>(_scheduler.Rules()));

        d.Register(MessageTypes.SettingsGet, _ => Task.FromResult<object?>(Settings.LoadDefault()));

        d.Register<SettingsDocument>(MessageTypes.SettingsSet, (s, _) =>
        {
            _settingsRepo.Save(s);
            return Task.FromResult<object?>(true);
        });

        d.Register(MessageTypes.Shutdown, ct0 =>
        {
            ShutdownSignal = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                Environment.Exit(0);
            });
            return Task.FromResult<object?>(new { bye = true });
        });

        return d;
    }

    private async Task<object> ResolveTracksAsync(AddVideoRequest req, CancellationToken ct)
    {
        if (req.Protocol.Equals("hls", StringComparison.OrdinalIgnoreCase))
        {
            var hls = new Nova.Engine.Streams.HlsDownloader(DownloadEngine.BuildHttpClient(Settings.LoadDefault()));
            var pl = await hls.ParseAsync(new Uri(req.MediaUrl), req.Headers, ct);
            return pl.Variants.Select(v => new MediaTrack
            {
                Id = v.Uri.ToString(), Kind = "video", Label = v.Label,
                Bitrate = v.Bandwidth, SizeBytes = null
            }).ToList();
        }
        if (req.Protocol.Equals("dash", StringComparison.OrdinalIgnoreCase))
        {
            var http = DownloadEngine.BuildHttpClient(Settings.LoadDefault());
            var xml = await http.GetStringAsync(req.MediaUrl, ct);
            var manifest = new Nova.Engine.Streams.DashParser().Parse(xml, new Uri(req.MediaUrl));
            var tracks = new List<MediaTrack>();
            foreach (var set in manifest.Sets)
                foreach (var rep in set.Representations)
                    tracks.Add(new MediaTrack
                    {
                        Id = rep.Id, Kind = set.ContentType, Height = rep.Height,
                        Fps = rep.FrameRate?.ToString("0"), Bitrate = rep.Bandwidth,
                        Codec = rep.Codecs, Container = rep.Container,
                        Label = rep.Height is { } h ? $"{h}p" : $"{rep.Bandwidth / 1000}kbps"
                    });
            return tracks;
        }
        // yt-dlp resolution for pages we can't parse natively
        var info = await _engine.YtDlp.ExtractAsync(req.PageUrl, null, null, ct);
        if (info == null) return new List<MediaTrack>();
        return info.Formats.Where(f => f.Height is not null).Select(f => new MediaTrack
        {
            Id = f.FormatId, Kind = "video", Height = f.Height, Fps = f.Fps?.ToString("0"),
            Bitrate = f.Tbr is { } t ? (long)(t * 1000) : null, SizeBytes = f.Filesize,
            Codec = f.Vcodec, Label = f.Label
        }).ToList();
    }

    // ------------------------------------------------------------------
    // Event bridge: engine -> all IPC clients
    // ------------------------------------------------------------------

    private void BridgeEvents()
    {
        _engine.StateEvent += ev =>
        {
            try { _ = _ipc.BroadcastAsync(MessageTypes.EvStateChanged, ev); }
            catch { /* shutdown race */ }
        };
        _engine.ProgressEvent += p =>
        {
            try { _ = _ipc.BroadcastAsync(MessageTypes.EvProgress, p); }
            catch { /* shutdown race */ }
        };
    }

    private long SumSpeed() => _engine.List().Sum(t => t.SpeedBytesPerSec);
    private int ActiveCount() => _engine.List().Count(t => t.State == DownloadState.Downloading);

    /// <summary>Set when a shutdown request arrives — monitored by main().</summary>
    public static bool ShutdownSignal;
}
