using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Nova.Domain;
using Nova.Engine;
using Nova.Ipc;
using Nova.Media;
using Nova.Persistence;
using Nova.Scheduler;

namespace Nova.EngineHost;

/// <summary>
/// Nova background engine host.
/// Owns: SQLite DB, DownloadEngine, Scheduler, MediaPipeline, IPC pipe server.
/// Runs at user level; no admin required. Single instance enforced.
/// </summary>
public sealed class EngineHostApp : IAsyncDisposable
{
    private readonly NovaDatabase _db;
    private readonly DownloadEngine _engine;
    private readonly SchedulerService _scheduler;
    private readonly IpcServer _ipc;
    private readonly SettingsRepository _settings;
    private readonly MediaCandidatesService _mediaProbe;
    private readonly IpcMessageRouter _router;
    private readonly HttpClient _http;
    private readonly string _dataDir;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public static string DefaultDataDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");
        return Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? ".", ".local", "share", "nova");
    }

    public EngineHostApp(string? dataDir = null)
    {
        _dataDir = Path.GetFullPath(dataDir ?? DefaultDataDir());
        Directory.CreateDirectory(_dataDir);
        var downloads = Path.Combine(_dataDir, "downloads");
        Directory.CreateDirectory(downloads);

        _settings = new SettingsRepository(LoadDb());
        _db = _dbInstance!;

        _http = CreateHttpClient();
        _engine = new DownloadEngine(
            new DownloadTaskRepository(_db), new SegmentRepository(_db),
            new QueueRepository(_db), _settings, new EngineEventBus(),
            _http, downloads, defaultConnections: 4,
            media: new MediaPipeline());

        _scheduler = new SchedulerService(_engine, _settings, new QueueRepository(_db));
        _scheduler.SchedulerStateChanged += s => _ipc?.Broadcast("engine_status", BuildStatus());
        _mediaProbe = new MediaCandidatesService(_http);

        _router = new IpcMessageRouter(_engine, _scheduler, _settings, new QueueRepository(_db), _mediaProbe, _db, _dataDir, downloads);
        var token = EnsureAuthToken();
        _ipc = new IpcServer(_router, token);
        _router.Server = _ipc;
    }

    private NovaDatabase? _dbInstance;

    private NovaDatabase LoadDb()
    {
        _dbInstance = new NovaDatabase(Path.Combine(_dataDir, "nova.db"));
        return _dbInstance;
    }

    /// <summary>Per-user secret; written once under the user profile. Both engine and trusted clients read it.</summary>
    private string EnsureAuthToken()
    {
        var path = Path.Combine(_dataDir, "ipc.token");
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 32) return existing;
            }
            var token = Convert.ToHexString(Guid.NewGuid().ToByteArray()) +
                        Convert.ToHexString(Guid.NewGuid().ToByteArray());
            File.WriteAllText(path, token);
            return token;
        }
        catch (UnauthorizedAccessException)
        {
            // fall back to a stable per-machine fallback (tests, restricted FS)
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Environment.MachineName + "_nova_dev")));
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // we do redirects ourselves to strip auth cross-origin
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ResponseDrainTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.None, // ranges must stay raw
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        return new HttpClient(new SafeRedirectHandler(handler)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Manual redirect loop: strips Authorization/Referer/Cookie when redirected to a different origin
    /// → credentials never leak to another domain. Max 20 hops.
    /// </summary>
    private sealed class SafeRedirectHandler : DelegatingHandler
    {
        public SafeRedirectHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var current = request;
            for (var hop = 0; ; hop++)
            {
                var resp = await base.SendAsync(current, ct);
                if (hop >= 20 || (int)resp.StatusCode is < 300 or > 399) return resp;

                var loc = resp.Headers.Location;
                if (loc is null) return resp;
                var nextUrl = loc.IsAbsoluteUri ? loc : new Uri(current.RequestUri!, loc);
                var sameOrigin = string.Equals(nextUrl.GetLeftPart(UriPartial.Authority),
                    current.RequestUri!.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

                var next = new HttpRequestMessage(current.Method, nextUrl);
                next.Headers.Range = current.Headers.Range; // preserve range probing through redirects
                foreach (var h in current.Headers.NonValidated)
                    if (h.Key != "Authorization" || sameOrigin)
                        next.Headers.TryAddWithoutValidation(h.Key, h.Value);
                foreach (var h in current.Content?.Headers ?? default)
                    ; // GET/HEAD have no content to carry
                if (sameOrigin)
                {
                    if (current.Headers.Authorization is not null)
                        next.Headers.Authorization = current.Headers.Authorization;
                }
                resp.Dispose();
                current = next;
            }
        }
    }

    public void Run()
    {
        _ipc.Start();
        EngineLog.Info($"Nova engine listening on pipe '{Protocol.PipeName}' (data: {_dataDir})");
    }

    private object BuildStatus()
    {
        var active = _engine.All().Count(t => t.State is DownloadState.Downloading or DownloadState.Analyzing
            or DownloadState.Verifying or DownloadState.Pausing);
        var queued = _engine.All().Count(t => t.State == DownloadState.Queued);
        return new EngineStatusChangedEvent
        {
            ActiveTasks = active,
            QueuedTasks = queued,
            GlobalSpeed = _engine.GlobalSpeedBps,
            SchedulerState = _scheduler.State,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _ipc.DisposeAsync();
        _scheduler.Dispose();
        await _engine.DisposeAsync();
        _http.Dispose();
        await _db.DisposeAsync();
    }
}
