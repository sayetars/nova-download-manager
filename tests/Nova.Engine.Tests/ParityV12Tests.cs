using System.Net;
using System.Text;
using Nova.Domain;
using Nova.Engine;
using Nova.Persistence;
using Nova.Scheduler;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>v1.2.0 IDM-parity features: per-server caps, queue schedules, sync queues, IDM import.</summary>
public class PerServerPolicyTests
{
    private sealed class MemSettings : ISettingsRepository
    {
        public Dictionary<string, string> Kv { get; } = new();
        public string? Get(string key) => Kv.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => Kv[key] = value;
        public IReadOnlyDictionary<string, string> GetAll() => Kv;
    }

    [Theory]
    [InlineData("https://WWW.Example.com/a.zip", "example.com")]
    [InlineData("http://cdn.example.co.uk/f?x=1", "cdn.example.co.uk")]
    [InlineData("ftp://files.example.com/pub", "files.example.com")]
    [InlineData("not a url", "")]
    [InlineData("", "")]
    public void HostOf_Normalizes(string url, string expected)
        => Assert.Equal(expected, PerServerPolicy.HostOf(url));

    [Fact]
    public void DefaultCap_Zero_IsUnlimited()
    {
        var s = new MemSettings();
        Assert.Equal(0, PerServerPolicy.ParseDefault(null));
        Assert.True(PerServerPolicy.MayStart("a.com", new Dictionary<string, int> { ["a.com"] = 99 }, s));
    }

    [Fact]
    public void DefaultCap_Blocks_AtLimit()
    {
        var s = new MemSettings();
        s.Set("scheduler.per_server_max", "2");
        Assert.True(PerServerPolicy.MayStart("a.com", new Dictionary<string, int> { ["a.com"] = 1 }, s));
        Assert.False(PerServerPolicy.MayStart("a.com", new Dictionary<string, int> { ["a.com"] = 2 }, s));
        Assert.True(PerServerPolicy.MayStart("b.com", new Dictionary<string, int> { ["a.com"] = 2 }, s),
            "other hosts unaffected");
    }

    [Fact]
    public void Exception_Overrides_Default()
    {
        var s = new MemSettings();
        s.Set("scheduler.per_server_max", "1");
        s.Set("scheduler.server_exceptions", "[{\"host\":\"bigcdn.com\",\"max\":5},{\"host\":\"*.slow.net\",\"max\":1}]");
        Assert.Equal(5, PerServerPolicy.CapFor("bigcdn.com", s));
        Assert.Equal(5, PerServerPolicy.CapFor("www.bigcdn.com", s));
        Assert.True(PerServerPolicy.CapFor("a.slow.net", s) == 1, "wildcard *.slow.net");
        Assert.True(PerServerPolicy.CapFor("slow.net", s) == 1, "wildcard matches bare domain");
        Assert.True(PerServerPolicy.CapFor("other.com", s) == 1, "default applies");
    }

    [Fact]
    public void Malformed_Exceptions_Are_Ignored()
    {
        var s = new MemSettings();
        s.Set("scheduler.server_exceptions", "{not json");
        s.Set("scheduler.per_server_max", "3");
        Assert.Equal(3, PerServerPolicy.CapFor("x.com", s));
        Assert.True(PerServerPolicy.ParseExceptions("{not json").Count == 0);
    }
}

public class QueueScheduleTests
{
    private static readonly DateTimeOffset Wed10am = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero); // Wednesday
    private static readonly DateTimeOffset Wed23 = new(2026, 10, 21, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Thu4am = new(2026, 10, 22, 4, 0, 0, TimeSpan.Zero);    // Thursday

    [Fact]
    public void NoRules_AlwaysAllowed()
        => Assert.True(QueueSchedule.MayRun("", Wed10am));

    [Fact]
    public void PauseWindow_BlocksInside_AllowsOutside()
    {
        var rules = new List<QueueScheduleRule>
        {
            new() { Action = "pause", StartTime = "09:00", EndTime = "17:00" },
        };
        Assert.False(QueueSchedule.MayRun(rules, Wed10am));
        Assert.True(QueueSchedule.MayRun(rules, Wed23));
    }

    [Fact]
    public void RunWindow_OnlyInside()
    {
        var rules = new List<QueueScheduleRule>
        {
            new() { Action = "run", StartTime = "22:00", EndTime = "06:00" }, // overnight
        };
        Assert.True(QueueSchedule.MayRun(rules, Wed23));
        Assert.True(QueueSchedule.MayRun(rules, Thu4am));
        Assert.False(QueueSchedule.MayRun(rules, Wed10am));
    }

    [Fact]
    public void PauseBeatsRun_WhenBothActive()
    {
        var rules = new List<QueueScheduleRule>
        {
            new() { Action = "run", StartTime = "00:00", EndTime = "23:59" },
            new() { Action = "pause", StartTime = "09:00", EndTime = "17:00" },
        };
        Assert.False(QueueSchedule.MayRun(rules, Wed10am));
        Assert.True(QueueSchedule.MayRun(rules, Wed23));
    }

    [Fact]
    public void DisabledRules_Are_Ignored()
    {
        var rules = new List<QueueScheduleRule>
        {
            new() { Action = "pause", StartTime = "00:00", EndTime = "23:59", Enabled = false },
        };
        Assert.True(QueueSchedule.MayRun(rules, Wed10am));
    }

    [Fact]
    public void DayOfWeek_Gates()
    {
        var rules = new List<QueueScheduleRule>
        {
            new() { Action = "pause", DaysOfWeek = "3", StartTime = "00:00", EndTime = "23:59" }, // Wed only
        };
        Assert.False(QueueSchedule.MayRun(rules, Wed10am));
        Assert.True(QueueSchedule.MayRun(rules, Thu4am));
    }

    [Fact]
    public void Json_Roundtrip()
    {
        var rules = new List<QueueScheduleRule> { new() { Action = "pause", StartTime = "08:00", EndTime = "09:30" } };
        var json = QueueSchedule.Serialize(rules);
        Assert.Equal(rules.Count, QueueSchedule.Parse(json).Count);
        Assert.Equal("pause", QueueSchedule.Parse(json)[0].Action);
    }
}

public class IdmImportTests
{
    [Fact]
    public void Extracts_From_IefStyle_Export()
    {
        var ief = """
            [General]
            Version=1
            [DownloadList0]
            URL=https://example.com/file1.zip
            Description=file one
            [DownloadList1]
            URL=http://mirror.example.org/file2.rar
            """;
        var urls = IdmImport.ExtractUrls(ief);
        Assert.Equal(2, urls.Count);
        Assert.Equal("https://example.com/file1.zip", urls[0]);
        Assert.Equal("http://mirror.example.org/file2.rar", urls[1]);
        Assert.True(IdmImport.LooksLikeIef(ief));
    }

    [Fact]
    public void Extracts_From_PlainList_And_Dedupes()
    {
        var list = """
            https://a.example/x.iso
            https://a.example/x.iso
            # comment line
            https://b.example/y.7z.
            ftp://files.example/z.tar.gz
            """;
        var urls = IdmImport.ExtractUrls(list);
        Assert.Equal(3, urls.Count);
        Assert.True(urls[1] == "https://b.example/y.7z", "trailing dot trimmed");
        Assert.Equal("ftp://files.example/z.tar.gz", urls[2]);
    }

    [Fact]
    public void Empty_And_Junk_Produce_Nothing()
    {
        Assert.Empty(IdmImport.ExtractUrls(""));
        Assert.Empty(IdmImport.ExtractUrls("hello world nothing here"));
        Assert.False(IdmImport.LooksLikeIef("just text"));
    }
}

public class SyncServiceTests : IAsyncLifetime
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, (string? Etag, string? LastModified)> Validators { get; } = new();
        public HashSet<string> NoHead { get; } = new(); // respond 405 to HEAD
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add($"{request.Method} {url}");
            if (request.Method == HttpMethod.Head && NoHead.Contains(url))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }
            var (etag, lm) = Validators.TryGetValue(url, out var v) ? v : (null, null);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("x")),
            };
            if (etag is not null) resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            if (lm is not null) resp.Content.Headers.LastModified = DateTimeOffset.TryParse(lm, out var d) ? d : null;
            return Task.FromResult(resp);
        }
    }

    private sealed class FakeClock : IClock
    {
        public long Ms { get; set; } = 1_000_000;
        public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(Ms);
        public long UnixMs => Ms;
    }

    private sealed class MemSettings : ISettingsRepository
    {
        public Dictionary<string, string> Kv { get; } = new();
        public string? Get(string key) => Kv.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => Kv[key] = value;
        public IReadOnlyDictionary<string, string> GetAll() => Kv;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nova-sync-tests-" + Guid.NewGuid().ToString("N"));
    private NovaDatabase _db = null!;
    private EngineFixture _fx = null!;
    private FakeHandler _handler = null!;
    private SyncRuleRepository _repo = null!;
    private SyncService _svc = null!;
    private readonly FakeClock _clock = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _db = new NovaDatabase(Path.Combine(_dir, "t.db"));
        _fx = new EngineFixture();
        _handler = new FakeHandler();
        _repo = new SyncRuleRepository(_db);
        _svc = new SyncService(_fx.Engine, _repo, new HttpClient(_handler), _clock);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _svc.Dispose();
        _fx.DisposeAsync();
        _handler.Dispose();
        await _db.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private long AddRule(string urls, int intervalMinutes = 60)
        => _repo.Insert(new SyncRule { Name = "r", Urls = urls, IntervalMinutes = intervalMinutes });

    [Fact]
    public async Task FirstRun_AddsAllUrls_AndStoresValidators()
    {
        _handler.Validators["http://s.test/a.bin"] = ("\"v1\"", "Wed, 21 Oct 2026 10:00:00 GMT");
        _handler.Validators["http://s.test/b.bin"] = ("\"w1\"", "Wed, 21 Oct 2026 10:00:00 GMT");
        var id = AddRule("http://s.test/a.bin\nhttp://s.test/b.bin\n");

        var result = await _svc.RunRuleAsync(id, force: true);

        Assert.Equal(2, result.Checked);
        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(2, _fx.Engine.All().Count);
        var stored = _repo.GetValidators(id);
        Assert.Equal(("v1", "Wed, 21 Oct 2026 10:00:00 GMT"), stored["http://s.test/a.bin"]);
        Assert.True(_repo.Get(id)!.LastRunMs > 0);
    }

    [Fact]
    public async Task SecondRun_SameValidators_Skips()
    {
        _handler.Validators["http://s.test/a.bin"] = ("\"v1\"", "Wed, 21 Oct 2026 10:00:00 GMT");
        var id = AddRule("http://s.test/a.bin");
        await _svc.RunRuleAsync(id, force: true);

        var result = await _svc.RunRuleAsync(id, force: true);

        Assert.Equal(1, result.Checked);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Unchanged);
        Assert.Single(_fx.Engine.All(), t => t.Url == "http://s.test/a.bin");
    }

    [Fact]
    public async Task ChangedETag_ReAdds()
    {
        _handler.Validators["http://s.test/a.bin"] = ("\"v1\"", null);
        var id = AddRule("http://s.test/a.bin");
        await _svc.RunRuleAsync(id, force: true);

        _handler.Validators["http://s.test/a.bin"] = ("\"v2\"", null);
        var result = await _svc.RunRuleAsync(id, force: true);

        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.Added);
        // duplicate protection: same URL still tracked once as non-terminal
        Assert.Single(_fx.Engine.All(), t => t.State != DownloadState.Cancelled && t.State != DownloadState.Failed);
    }

    [Fact]
    public async Task Head405_FallsBackToRangedGet()
    {
        _handler.Validators["http://s.test/a.bin"] = ("\"v1\"", null);
        _handler.NoHead.Add("http://s.test/a.bin");
        var id = AddRule("http://s.test/a.bin");

        var result = await _svc.RunRuleAsync(id, force: true);

        Assert.Equal(1, result.Added);
        Assert.Contains("HEAD http://s.test/a.bin", _handler.Requests);
        Assert.Contains("GET http://s.test/a.bin", _handler.Requests);
    }

    [Fact]
    public async Task NoValidators_Are_TreatedAsUnchanged_AfterFirstSighting()
    {
        var id = AddRule("http://s.test/plain.bin");
        var first = await _svc.RunRuleAsync(id, force: true);
        var second = await _svc.RunRuleAsync(id, force: true);
        Assert.Equal(0, first.Added);
        Assert.Equal(0, second.Added);
    }

    [Fact]
    public async Task CheckDue_Respects_Interval_And_Enabled()
    {
        _handler.Validators["http://s.test/a.bin"] = ("\"v1\"", null);
        var id = AddRule("http://s.test/a.bin", intervalMinutes: 60);
        var rule = _repo.Get(id)!;
        rule.LastRunMs = _clock.UnixMs - 30 * 60_000; // ran 30 min ago
        _repo.Update(rule);

        await _svc.CheckDueAsync(); // not due yet
        var stored = _repo.GetValidators(id);
        Assert.Empty(stored);

        _clock.Ms += 31 * 60_000;
        await _svc.CheckDueAsync(); // now due
        Assert.Single(_fx.Engine.All());

        // disabled rules never run
        var r2 = _repo.Get(id)!;
        r2.Enabled = false;
        _repo.Update(r2);
        _clock.Ms += 10 * 60_000;
        _handler.Validators["http://s.test/a.bin"] = ("\"v9\"", null);
        var before = _fx.Engine.All().Count;
        await _svc.CheckDueAsync();
        Assert.Equal(before, _fx.Engine.All().Count);
    }
}

public class QueueSchedulePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nova-q-sched-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ScheduleJson_RoundTrips_Through_Repository()
    {
        var db = new NovaDatabase(Path.Combine(_dir, "t.db"));
        var queues = new QueueRepository(db);
        var q = queues.GetOrCreate("night");
        q.ScheduleJson = """[{"action":"run","start_time":"22:00","end_time":"06:00","enabled":true}]""";
        queues.Save(q);

        var reloaded = queues.GetOrCreate("night");
        Assert.Contains("run", reloaded.ScheduleJson);
        Assert.Contains("22:00", reloaded.ScheduleJson);
        db.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

/// <summary>Engine-level: ActivePerHost counts only running tasks.</summary>
public class ActivePerHostTests : IAsyncLifetime
{
    private readonly TestHttpServer _server = new();
    private EngineFixture _fx = null!;

    public Task InitializeAsync()
    {
        _server.AddFile("big.bin", new byte[4 * 1024 * 1024]);
        _server.AddFile("big2.bin", new byte[4 * 1024 * 1024]);
        _server.ThrottleBytesPerSec = 150 * 1024;
        _fx = new EngineFixture();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _server.Dispose();
    }

    [Fact]
    public async Task Counts_Running_Tasks_PerHost()
    {
        var a = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("big.bin") });
        var b = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("big2.bin") });
        Assert.True(_fx.Engine.TryStartTask(a.Value!.TaskKey));
        Assert.True(_fx.Engine.TryStartTask(b.Value!.TaskKey));

        // Runners are registered synchronously — no wait needed.
        var perHost = _fx.Engine.ActivePerHost();
        Assert.Equal(2, perHost.GetValueOrDefault("127.0.0.1", 0));
        Assert.Equal(2, _fx.Engine.ActiveCount);

        // give both runners a chance to hit the wire, then let the fixture clean up
        await Task.Delay(300);
    }
}
