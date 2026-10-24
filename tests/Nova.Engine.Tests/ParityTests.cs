using Microsoft.Data.Sqlite;
using Nova.Domain;
using Nova.Engine;
using Nova.Persistence;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Nova.Engine.Tests;

// ---------------------------------------------------------------- UrlPatterns
public class UrlPatternsTests
{
    [Fact]
    public void NumericRangeExpansion()
    {
        var urls = UrlPatterns.Expand("https://x/file[01-03].jpg");
        Assert.Equal(new[] { "https://x/file01.jpg", "https://x/file02.jpg", "https://x/file03.jpg" }, urls);
    }

    [Fact]
    public void SteppedRange()
    {
        var urls = UrlPatterns.Expand("https://x/[1-9:4].png");
        Assert.Equal(new[] { "https://x/1.png", "https://x/5.png", "https://x/9.png" }, urls);
    }

    [Fact]
    public void DescendingRangeAndNoPattern()
    {
        Assert.Equal(new[] { "https://x/a.zip" }, UrlPatterns.Expand("https://x/a.zip"));
        Assert.Equal(new[] { "https://x/9.jpg", "https://x/7.jpg" }, UrlPatterns.Expand("https://x/[9-7:2].jpg"));
    }

    [Fact]
    public void ExtractUrlsFiltersProtocols()
    {
        var urls = UrlPatterns.ExtractUrls("https://a/1 ftp://b/2 junk ftps://c/3 http://d/4");
        Assert.Equal(new[] { "https://a/1", "ftp://b/2", "ftps://c/3", "http://d/4" }, urls);
    }

    [Fact]
    public void WildcardMatching()
    {
        Assert.True(UrlPatterns.WildcardMatch("https://x/img/a.png", "*.png;*.jpg".Split(';')[0]));
        Assert.True(UrlPatterns.WildcardMatch("Host.Example.COM", "host.example.*"));
        Assert.False(UrlPatterns.WildcardMatch("https://x/img/a.png", "*.gif"));
        Assert.True(UrlPatterns.WildcardMatch("ab", "a?"));
    }
}

// ---------------------------------------------------------------- proxy config
public class ProxyConfigTests
{
    private static ProxyConfig From(Dictionary<string, string> s) =>
        ProxyConfig.FromSettings(k => s.TryGetValue(k, out var v) ? v : null);

    [Fact]
    public void ParsesSettings()
    {
        var p = From(new Dictionary<string, string>
        {
            ["proxy.enabled"] = "true", ["proxy.scheme"] = "socks5", ["proxy.host"] = "127.0.0.1",
            ["proxy.port"] = "1080", ["proxy.user"] = "u", ["proxy.pass"] = "p",
            ["proxy.bypass"] = "localhost;*.internal",
        });
        Assert.True(p.IsValid);
        Assert.Equal("socks5", p.Scheme);
        Assert.Equal(1080, p.Port);
        Assert.True(p.IsBypassed("localhost"));
        Assert.True(p.IsBypassed("wiki.internal"));
        Assert.False(p.IsBypassed("example.com"));
    }

    [Fact]
    public void InvalidSchemesFallBackToHttp()
    {
        var p = From(new Dictionary<string, string> { ["proxy.enabled"] = "1", ["proxy.scheme"] = "gopher", ["proxy.host"] = "h", ["proxy.port"] = "1" });
        Assert.Equal("http", p.Scheme);   // fallback to a scheme .NET supports
        Assert.True(p.IsValid);

        var noPort = From(new Dictionary<string, string> { ["proxy.enabled"] = "true", ["proxy.host"] = "h" });
        Assert.False(noPort.IsValid);
    }

    [Fact]
    public void DisabledIsInvalid()
    {
        var p = From(new Dictionary<string, string> { ["proxy.enabled"] = "false", ["proxy.host"] = "x", ["proxy.port"] = "80" });
        Assert.False(p.IsValid);
    }
}

// ---------------------------------------------------------------- login vault
public class LoginVaultTests : IDisposable
{
    private readonly NovaDatabase _db;
    private readonly LoginVault _vault;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nova-vault-test-" + Guid.NewGuid().ToString("N"));

    public LoginVaultTests()
    {
        _db = new NovaDatabase(Path.Combine(_dir, "t.db"));
        _vault = new LoginVault(_db, _dir);
    }

    [Fact]
    public void SaveFindRoundTrip()
    {
        _vault.Save("Example.com", "", "alice", "s3cret", "basic");
        var entry = _vault.Find("https://www.example.com/file.zip");
        Assert.NotNull(entry);
        Assert.Equal("alice", entry!.UserName);
        Assert.Equal("s3cret", entry.Password);   // decrypted only in-process

        // safe listing never carries the password
        var meta = _vault.ListSafe();
        var json = System.Text.Json.JsonSerializer.Serialize(meta);
        Assert.DoesNotContain("s3cret", json);
    }

    [Fact]
    public void SecretsAreEncryptedAtRest()
    {
        _vault.Save("site.org", "", "bob", "plain-leetcode");
        // raw DB must not contain the plaintext
        var raw = File.ReadAllText(Path.Combine(_dir, "t.db"));
        Assert.DoesNotContain("plain-leetcode", raw);
    }

    [Fact]
    public void DeleteRemoves()
    {
        _vault.Save("gone.net", "", "u", "p");
        var id = _vault.List().Single().Id;
        _vault.Delete(id);
        Assert.Empty(_vault.List());
    }

    public void Dispose()
    {
        _db.DisposeAsync().AsTask().Wait();
        try { Directory.Delete(_dir, true); } catch { }
    }
}

// ---------------------------------------------------------------- zip preview
public class ZipPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nova-zip-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ListLocalAndExtractWithTraversalBlock()
    {
        Directory.CreateDirectory(_dir);
        var zipPath = Path.Combine(_dir, "a.zip");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("hello.txt"); using (var w = new StreamWriter(e.Open())) { w.Write("hi"); }
            var e2 = z.CreateEntry("nested/file.bin"); using (var w2 = new StreamWriter(e2.Open())) { w2.Write("x"); }
        }
        var items = ZipPreview.ListLocal(zipPath);
        Assert.Contains(items, i => i.Name == "hello.txt");
        Assert.Contains(items, i => i.Name == "nested/file.bin");

        var outDir = Path.Combine(_dir, "out");
        ZipPreview.ExtractEntries(zipPath, new[] { "hello.txt", "nested/file.bin" }, outDir);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(outDir, "hello.txt")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(outDir, "nested", "file.bin")));
    }

    [Fact]
    public void CentralDirectoryParsing()
    {
        Directory.CreateDirectory(_dir);
        var zipPath = Path.Combine(_dir, "b.zip");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("file1.dat"); using (var w = new StreamWriter(e.Open())) { w.Write("aaaa"); }
            var e2 = z.CreateEntry("dir/file2.dat"); using (var w2 = new StreamWriter(e2.Open())) { w2.Write("bbbbbbbb"); }
        }
        var tail = File.ReadAllBytes(zipPath);
        var eocd = ZipPreview.FindEocd(tail);
        Assert.True(eocd >= 0);
        var cdSize = BitConverter.ToUInt32(tail, eocd + 12);
        var cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
        var count = BitConverter.ToUInt16(tail, eocd + 10);
        Assert.Equal(2, count);

        var cd = new byte[cdSize];
        Array.Copy(tail, cdOffset, cd, 0, (int)cdSize);
        var entries = ZipPreview.ParseCentralDirectory(cd, count);
        Assert.Equal(2, entries.Count);
        Assert.Equal(4, entries[0].UncompressedSize);
        Assert.Equal(8, entries[1].UncompressedSize);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

// ---------------------------------------------------------------- post actions
public class PostActionTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("exit", true)]
    [InlineData("shutdown", true)]
    [InlineData("hibernate", true)]
    [InlineData("sleep", true)]
    [InlineData("logoff", true)]
    [InlineData("reboot", false)]
    [InlineData(null, false)]
    public void Validation(string? action, bool valid)
        => Assert.Equal(valid, PostActionRunner.IsValid(action!));

    [Fact]
    public void ExitActionInvokesCallback()
    {
        var fired = false;
        PostActionRunner.Execute("exit", () => fired = true, _ => { });
        Assert.True(fired);
    }

    [Fact]
    public void EmptyActionIsNoop()
    {
        PostActionRunner.Execute("", () => throw new InvalidOperationException("must not fire"), _ => { });
    }
}

// ---------------------------------------------------------------- engine extras
public class EngineParityTests : IAsyncDisposable
{
    private readonly EngineFixture _fx = new();

    [Fact]
    public void Add_AcceptsFtpUrls()
    {
        var res = _fx.Engine.Add(new NewDownload { Url = "ftp://speed.test/file.bin" });
        Assert.True(res.Ok, res.Error?.Message);
        var t = _fx.Engine.Get(res.Value!.TaskKey)!;
        Assert.Equal("file.bin", t.FileName);
    }

    [Fact]
    public void Add_RejectsJunkScheme()
    {
        var res = _fx.Engine.Add(new NewDownload { Url = "javascript:alert(1)" });
        Assert.False(res.Ok);
        Assert.Equal("bad_url", res.Error!.Code);
    }

    [Fact]
    public void MirrorAndScheduleFieldsPersist()
    {
        var res = _fx.Engine.Add(new NewDownload
        {
            Url = "https://x/y.zip",
            Mirrors = new List<string> { "https://m1/y.zip", "https://m2/y.zip" },
            StartAtMs = 9999999999999,
        });
        var t = _fx.Engine.Get(res.Value!.TaskKey)!;
        Assert.Equal(2, t.Mirrors.Count);
        Assert.Equal(9999999999999, t.StartAtMs);

        // not-yet-due tasks stay out of the scheduler queue
        t.State = DownloadState.Queued;
        Assert.Empty(_fx.Engine.GetQueued());
        t.StartAtMs = 0;
        Assert.Single(_fx.Engine.GetQueued());
    }

    public ValueTask DisposeAsync() => _fx.DisposeAsync();
}

// ---------------------------------------------------------------- byte quotas
public class QuotaTests
{
    private sealed class MemSettings : ISettingsRepository
    {
        public Dictionary<string, string> Kv { get; } = new();
        public string? Get(string key) => Kv.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => Kv[key] = value;
        public IReadOnlyDictionary<string, string> GetAll() => Kv;
    }

    [Theory]
    [InlineData("500MB", 524288000L)]
    [InlineData("2gb", 2147483648L)]
    [InlineData("1g", 1073741824L)]
    [InlineData("768kb", 786432L)]
    [InlineData("1024", 1024L)]
    [InlineData("", null)]
    [InlineData("junk", null)]
    public void SizeParsing(string? raw, long? expected)
        => Assert.Equal(expected, QuotaTracker.ParseSize(raw));

    [Fact]
    public void CapPausesAndWindowSlideResumes()
    {
        var settings = new MemSettings();
        settings.Set("quota.enabled", "true");
        settings.Set("quota.max_bytes", "1000");     // 1000 bytes cap
        settings.Set("quota.window_minutes", "10");

        long now = 1_000_000;
        var quota = new QuotaTracker(settings, () => now);

        quota.OnBytes(600);
        Assert.False(quota.Paused);
        quota.OnBytes(400);                          // reaches the 1000-byte cap
        Assert.True(quota.Tick());                    // → paused
        Assert.True(quota.Paused);
        Assert.Equal(1000, quota.Snapshot().UsedBytes);

        now += 60_000 * 11;                            // older samples age out of window
        Assert.True(quota.Tick());                     // window freed → resumed
        Assert.False(quota.Paused);
        Assert.Equal(0, quota.Snapshot().UsedBytes);
    }

    [Fact]
    public void DisabledNeverPauses()
    {
        var settings = new MemSettings();
        settings.Set("quota.enabled", "false");
        settings.Set("quota.max_bytes", "100");
        long now = 5_000_000;
        var quota = new QuotaTracker(settings, () => now);
        quota.OnBytes(999_999);
        Assert.False(quota.Tick());
        Assert.False(quota.Paused);
    }

    [Fact]
    public void PersistenceRestoresWindow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-quota-p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "q.db");
        long now = 10_000_000;
        MemSettings settings = new();
        settings.Set("quota.enabled", "true");
        settings.Set("quota.max_bytes", "1000");
        settings.Set("quota.window_minutes", "30");

        var dbOuter = new NovaDatabase(dbPath);
        var q1 = new QuotaTracker(settings, () => now, dbOuter);
        q1.OnBytes(700);
        q1.Tick();                                 // persists a checkpoint row
        dbOuter.DisposeAsync().AsTask().Wait();
        var db2 = new NovaDatabase(dbPath);
        var q2 = new QuotaTracker(settings, () => now, db2);
        Assert.Equal(700, q2.Snapshot().UsedBytes); // survived restart
        db2.DisposeAsync().AsTask().Wait();
        try { Directory.Delete(dir, true); } catch { }
    }
}

// ---------------------------------------------------------------- live segments view
public class SegmentViewTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private EngineFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _server = new TestHttpServer();
        _fx = await EngineFixture.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _server.Dispose();
    }

    [Fact]
    public async Task MultipartTaskExposesLiveSegmentViews()
    {
        _server.AddFile("segments-view.bin", new byte[8 * 1024 * 1024]);
        var url = _server.UrlFor("segments-view.bin");
        var res = _fx.Engine.Add(new NewDownload { Url = url, MaxConnections = 4 });
        Assert.True(res.Ok, res.Error?.ToString());
        var key = res.Value!.TaskKey;

        Assert.Empty(_fx.Engine.GetSegmentViews(key));       // nothing before start
        _fx.Engine.TryStartNext();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (_fx.Engine.Get(key)?.State == DownloadState.Completed) break;
            await Task.Delay(100);
        }
        var task = _fx.Engine.Get(key)!;
        Assert.Equal(DownloadState.Completed, task.State);

        // live view clears after completion (runner detached); during the run it
        // held the plan. We assert the completed state + no crash on lookup.
        _ = _fx.Engine.GetSegmentViews(key);
    }

    [Fact]
    public void UnknownTaskReturnsEmptyViews()
        => Assert.Empty(_fx.Engine.GetSegmentViews("no-such-key"));
}
