using System.Net;
using System.Net.Http;
using Nova.Domain;
using Nova.Engine;
using Nova.Persistence;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>
/// v1.8.0 "IDM full-parity completion" suite: PAC (Jint), system proxy
/// detection, HTTP/2 offer, resume-validator-ignore (Downloads tab), and the
/// category "change folder on last selected" persistence.
/// </summary>
public class V180PacTests
{
    // ---- ParseResult ------------------------------------------------------------

    [Theory]
    [InlineData("DIRECT", null)]
    [InlineData("direct", null)]
    [InlineData("PROXY proxy.example.com:8080", "http", "proxy.example.com", 8080)]
    [InlineData("SOCKS socks5.example.com:1080", "socks5", "socks5.example.com", 1080)]
    [InlineData("SOCKS5 s5.example.com:1080", "socks5", "s5.example.com", 1080)]
    [InlineData("SOCKS4 s4.example.com:1080", "socks4", "s4.example.com", 1080)]
    [InlineData("PROXY a.example.com:3128; PROXY b.example.com:3128", "http", "a.example.com", 3128)]
    [InlineData("PROXY bad; SOCKS good.example.com:1080", "socks5", "good.example.com", 1080)] // skip malformed → next (PAC contract)
    [InlineData("PROXY noprimary.example.com; PROXY good.example.com:80", "http", "good.example.com", 80)] // missing port → skip
    [InlineData("GARBAGE verb", null)]
    [InlineData("", null)]
    public void ParseResult_Variants(string input, string? expectedMode, string? expectedHost = null, int expectedPort = 0)
    {
        var d = PacResolver.ParseResult(input);
        if (expectedMode is null)
        {
            Assert.Null(d);
        }
        else
        {
            Assert.NotNull(d);
            Assert.Equal(expectedMode, d!.Mode);
            Assert.Equal(expectedHost, d.Host);
            Assert.Equal(expectedPort, d.Port);
        }
    }

    // ---- resolver with real scripts -----------------------------------------------

    [Fact]
    public void Resolver_Direct_Script()
    {
        var r = new PacResolver("function FindProxyForURL(url, host) { return \"DIRECT\"; }");
        Assert.Null(r.Resolve("https://example.com/file.bin", "example.com"));
    }

    [Fact]
    public void Resolver_Static_Proxy_Script()
    {
        var r = new PacResolver("function FindProxyForURL(url, host) { return \"PROXY cache.corp.example:3128\"; }");
        var d = r.Resolve("https://example.com/file.bin", "example.com");
        Assert.NotNull(d);
        Assert.Equal("http", d!.Mode);
        Assert.Equal("cache.corp.example", d.Host);
        Assert.Equal(3128, d.Port);
        Assert.Equal(new Uri("http://cache.corp.example:3128"), d.ToProxyUri());
    }

    [Fact]
    public void Resolver_Conditional_Via_ShExpMatch()
    {
        var script = """
            function FindProxyForURL(url, host) {
                if (shExpMatch(host, "*.internal.example")) return "DIRECT";
                if (shExpMatch(url, "*youtube.com*")) return "SOCKS socks.internal.example:1080";
                return "PROXY webproxy.example:8080; DIRECT";
            }
            """;
        var r = new PacResolver(script);
        Assert.Null(r.Resolve("https://files.internal.example/x", "files.internal.example"));
        var yt = r.Resolve("https://www.youtube.com/watch?v=abc", "www.youtube.com");
        Assert.NotNull(yt);
        Assert.Equal("socks5", yt!.Mode);
        var other = r.Resolve("https://example.org/data.bin", "example.org");
        Assert.NotNull(other);
        Assert.Equal("http", other!.Mode);
        Assert.Equal(8080, other.Port);
    }

    [Fact]
    public void Resolver_Pac_Helper_Environment()
    {
        var script = """
            function FindProxyForURL(url, host) {
                if (isPlainHostName(host)) return "DIRECT";
                if (dnsDomainIs(host, ".corp.example")) return "DIRECT";
                if (dnsDomainLevels(host) > 3) return "PROXY deep.example:3128";
                if (isInNet("10.0.0.1", "10.0.0.0", "255.0.0.0")) return "PROXY lan.example:80";
                if (localHostOrDomainIs(host, "www.plainsite.example")) return "DIRECT";
                return "PROXY fallback.example:8080";
            }
            """;
        var r = new PacResolver(script);
        Assert.Null(r.Resolve("http://intranet/x", "intranet"));                       // isPlainHostName
        Assert.Null(r.Resolve("https://git.corp.example/x", "git.corp.example"));      // dnsDomainIs
        Assert.Equal("deep.example", r.Resolve("https://a.b.c.d.example/x", "a.b.c.d.example")!.Host); // levels
        Assert.Equal("lan.example", r.Resolve("https://host.example/x", "host.example")!.Host);        // isInNet literal
        Assert.Null(r.Resolve("https://www.plainsite.example/x", "www"));              // localHostOrDomainIs
    }

    [Fact]
    public void Resolver_Script_Without_Function_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new PacResolver("function other() { return 1; }"));
    }

    [Fact]
    public void Resolver_Runtime_Error_Falls_Back_To_Direct()
    {
        var r = new PacResolver("function FindProxyForURL(url, host) { return missingFunction(url); }");
        Assert.Null(r.Resolve("https://example.com/x", "example.com"));
    }

    // ---- selector (IWebProxy) -------------------------------------------------------

    [Fact]
    public void Selector_Direct_Returns_Destination()
    {
        var s = new PacProxySelector(new PacResolver("function FindProxyForURL(u,h){return 'DIRECT';}"));
        var dest = new Uri("https://example.com/file.bin");
        Assert.Equal(dest, s.GetProxy(dest));
        Assert.False(s.IsBypassed(dest));
    }

    [Fact]
    public void Selector_Proxy_Returns_Proxy_Uri_And_Caches()
    {
        var s = new PacProxySelector(new PacResolver("function FindProxyForURL(u,h){return 'PROXY p.example:3128';}"));
        var dest = new Uri("https://cdn.example.com/file.bin");
        var proxy = s.GetProxy(dest);
        Assert.Equal(new Uri("http://p.example:3128"), proxy);
        Assert.Equal(proxy, s.GetProxy(dest)); // cached path returns the same value
    }

    [Fact]
    public void Selector_Bypass_Wildcards()
    {
        var s = new PacProxySelector(
            new PacResolver("function FindProxyForURL(u,h){return 'PROXY p.example:3128';}"),
            new[] { "*.internal.example", "localhost" });
        Assert.True(s.IsBypassed(new Uri("http://git.internal.example/x")));
        Assert.True(s.IsBypassed(new Uri("http://localhost:8080/x")));
        Assert.False(s.IsBypassed(new Uri("https://example.com/x")));
    }

    // ---- source loading ---------------------------------------------------------------

    [Fact]
    public async Task PacSource_Loads_From_File_Path()
    {
        var path = Path.Combine(Path.GetTempPath(), "nova-test-" + Guid.NewGuid().ToString("N") + ".pac");
        await File.WriteAllTextAsync(path, "function FindProxyForURL(u,h){return 'PROXY file.example:80';}");
        try
        {
            var script = await PacSource.LoadAsync(path);
            var d = new PacResolver(script).Resolve("https://x.example/", "x.example");
            Assert.Equal("file.example", d!.Host);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PacSource_Missing_File_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => PacSource.LoadAsync("/nonexistent/nova-test.pac"));
    }

    [Fact]
    public async Task PacSource_Unsupported_Scheme_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => PacSource.LoadAsync("ftp://example.com/proxy.pac"));
    }

    // ---- ProxyConfig integration ---------------------------------------------------

    [Fact]
    public void ProxyConfig_Pac_Mode_Parsed_From_Settings()
    {
        var map = new Dictionary<string, string>
        {
            ["proxy.enabled"] = "true",
            ["proxy.mode"] = "pac",
            ["proxy.pac_url"] = "https://corp.example/proxy.pac",
        };
        var cfg = ProxyConfig.FromSettings(k => map.GetValueOrDefault(k));
        Assert.True(cfg.UsesPac);
        Assert.Equal("https://corp.example/proxy.pac", cfg.PacUrl);

        var manual = ProxyConfig.FromSettings(k => map.GetValueOrDefault(k) is { } v && k == "proxy.enabled" ? v : null);
        Assert.False(manual.UsesPac); // default mode stays manual
    }
}

public class V180SystemProxyTests
{
    [Theory]
    [InlineData("proxy.corp.example:8080", "http", "proxy.corp.example", 8080)]
    [InlineData("192.168.1.10:3128", "http", "192.168.1.10", 3128)]
    public void ParseWindowsProxyServer_Plain(string server, string scheme, string host, int port)
    {
        var parsed = SystemProxy.ParseWindowsProxyServer(server);
        Assert.NotNull(parsed);
        Assert.Equal(scheme, parsed!.Value.Scheme);
        Assert.Equal(host, parsed.Value.Host);
        Assert.Equal(port, parsed.Value.Port);
    }

    [Fact]
    public void ParseWindowsProxyServer_PerProtocol_Prefers_Http()
    {
        var parsed = SystemProxy.ParseWindowsProxyServer(
            "ftp=10.0.0.5:21;http=10.0.0.6:3128;https=10.0.0.7:3129;socks=10.0.0.8:1080");
        Assert.NotNull(parsed);
        Assert.Equal("http", parsed!.Value.Scheme);
        Assert.Equal("10.0.0.6", parsed.Value.Host);
        Assert.Equal(3128, parsed.Value.Port);
    }

    [Fact]
    public void ParseWindowsProxyServer_Socks_Only()
    {
        var parsed = SystemProxy.ParseWindowsProxyServer("socks=127.0.0.1:9050");
        Assert.NotNull(parsed);
        Assert.Equal("socks5", parsed!.Value.Scheme);
        Assert.Equal(9050, parsed.Value.Port);
    }

    [Theory]
    [InlineData("'manual'", "manual")]
    [InlineData("manual", "manual")]
    [InlineData("(uint32) 8080", "8080")]
    [InlineData("'192.168.1.1'", "192.168.1.1")]
    [InlineData("(string) 'auto'", "auto")]
    public void GsettingsValue_Parsing(string raw, string expected)
        => Assert.Equal(expected, SystemProxy.GsettingsValue(raw));

    [Fact]
    public void Scutil_Parsing()
    {
        var raw = """
            <dictionary> {
              HTTPEnable : 1
              HTTPProxy : 127.0.0.1
              HTTPPort : 8888
              HTTPSProxy : 127.0.0.1
              HTTPSPort : 8888
              SOCKSEnable : 0
              ProxyAutoConfigEnable : 0
            }
            """;
        Assert.Equal(1, SystemProxy.ParseScutilFlag(raw, "HTTPEnable"));
        Assert.Equal("127.0.0.1", SystemProxy.ParseScutilValue(raw, "HTTPProxy"));
        Assert.Equal("8888", SystemProxy.ParseScutilValue(raw, "HTTPPort"));
        Assert.Equal(0, SystemProxy.ParseScutilFlag(raw, "SOCKSEnable"));
        Assert.Null(SystemProxy.ParseScutilValue(raw, "ProxyAutoConfigURLString"));
    }

    [Fact]
    public void Read_Never_Throws()
    {
        var info = SystemProxy.Read(); // Linux sandbox: gsettings absent → Unavailable
        Assert.NotNull(info);
    }
}

public class V180HttpProtocolTests : IDisposable
{
    private readonly bool _original = HttpProtocol.UseHttp2;

    public void Dispose() => HttpProtocol.UseHttp2 = _original;

    [Fact]
    public void Apply_Https_Offers_H2_With_Fallback()
    {
        HttpProtocol.UseHttp2 = true;
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://example.com/file.bin");
        HttpProtocol.Apply(req);
        Assert.Equal(HttpVersion.Version20, req.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrHigher, req.VersionPolicy);
    }

    [Fact]
    public void Apply_Http_Stays_1_1()
    {
        HttpProtocol.UseHttp2 = true;
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://example.com/file.bin");
        HttpProtocol.Apply(req);
        Assert.Equal(HttpVersion.Version11, req.Version);
    }

    [Fact]
    public void Apply_Disabled_Does_Nothing()
    {
        HttpProtocol.UseHttp2 = false;
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://example.com/file.bin");
        HttpProtocol.Apply(req);
        Assert.Equal(HttpVersion.Version11, req.Version);
    }
}

/// <summary>IDM "Downloads" tab parity: ignore file modification time changes when resuming.</summary>
public class V180ResumeValidatorTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private EngineFixture _fx = null!;
    private TestSink _sink = null!;
    private IDisposable _sub = null!;

    public async Task InitializeAsync()
    {
        _server = new TestHttpServer();
        _fx = await EngineFixture.CreateAsync();
        _sink = new TestSink();
        _sub = _fx.Bus.Subscribe(_sink);
    }

    public async Task DisposeAsync()
    {
        _sub.Dispose();
        await _fx.DisposeAsync();
        _server.Dispose();
    }

    [Fact]
    public async Task Multi_Segment_Resume_Does_Not_Restart_From_Zero()
    {
        // v1.8.0 regression guard for the planMatches bug fix: a paused
        // multi-segment download must CONTINUE (old prefix preserved), not
        // silently restart (which re-downloaded everything since v1.1.0).
        var size = 4 * 1024 * 1024;
        var data1 = TestBytes.Pattern(size, seed: 77);
        var data2 = TestBytes.Pattern(size, seed: 88); // same size, different bytes, SAME etag
        _server.AddFile("msresume.bin", data1);
        _server.EtagValue = "\"ms-fixed\"";
        _server.ThrottleBytesPerSec = 500 * 1024;

        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("msresume.bin"), MaxConnections = 4 });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        await Wait.UntilAsync(() => _fx.Engine.Get(key)!.ReceivedBytes > 512 * 1024, timeoutMs: 20000);
        _fx.Engine.Pause(key);
        await Wait.UntilAsync(() => _fx.Engine.Get(key)!.State == DownloadState.Paused);
        var pausedAt = _fx.Engine.Get(key)!.ReceivedBytes;
        Assert.True(pausedAt > 0);

        // swap content behind the same validator: a restart would fetch 100% v2
        _server.AddFile("msresume.bin", data2);
        _server.EtagValue = "\"ms-fixed\"";
        _server.ThrottleBytesPerSec = 0;

        _fx.Engine.Resume(key);
        _fx.Engine.TryStartNext();
        _sink.WaitStatePumped(key, DownloadState.Completed, _fx.Engine, 60000);

        var got = await File.ReadAllBytesAsync(_fx.Engine.Get(key)!.FinalPath);
        Assert.Equal(size, got.Length);
        Assert.False(data2.SequenceEqual(got)); // old bytes preserved → genuine resume
    }

    [Fact]
    public async Task Validator_Change_With_Ignore_Set_Resumes_Old_Bytes()
    {
        var size = 2 * 1024 * 1024;
        var data1 = TestBytes.Pattern(size, seed: 11);
        var data2 = TestBytes.Pattern(size, seed: 22); // SAME size, different content
        _server.AddFile("ignorevalidator.bin", data1);
        _server.EtagValue = "\"iv-v1\"";
        _server.ThrottleBytesPerSec = 500 * 1024;

        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("ignorevalidator.bin"), MaxConnections = 2 });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        await Wait.UntilAsync(() => _fx.Engine.Get(key)!.ReceivedBytes > 128 * 1024, timeoutMs: 20000);
        _fx.Engine.Pause(key);
        await Wait.UntilAsync(() => _fx.Engine.Get(key)!.State == DownloadState.Paused);
        var receivedAtPause = _fx.Engine.Get(key)!.ReceivedBytes;
        Assert.True(receivedAtPause > 0);

        // same size, different etag → validator change
        _server.AddFile("ignorevalidator.bin", data2);
        _server.EtagValue = "\"iv-v2\"";
        _server.ThrottleBytesPerSec = 0;

        // IDM Downloads-tab checkbox: ignore the change, resume old bytes
        new SettingsRepository(_fx.Db).Set("engine.resume_ignore_validator_change", "true");

        _fx.Engine.Resume(key);
        _fx.Engine.TryStartNext();
        _sink.WaitStatePumped(key, DownloadState.Completed, _fx.Engine, 60000);

        var got = await File.ReadAllBytesAsync(_fx.Engine.Get(key)!.FinalPath);
        Assert.Equal(size, got.Length);             // fully downloaded
        Assert.False(data2.SequenceEqual(got));     // NOT restarted — old prefix preserved
        Assert.False(data1.SequenceEqual(got));     // …but the tail came from the new file
    }
}

public class V180CategoryDirTests : IAsyncLifetime
{
    private string _dir = null!;
    private NovaDatabase _db = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nova-catdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = new NovaDatabase(Path.Combine(_dir, "nova.db"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void UpdateCategoryDir_Persists_New_Default()
    {
        // insert a custom category (same statement the IPC save_category uses)
        lock (_db.Connection)
        using (var insert = _db.Connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO category_def(name, patterns, destination_dir, icon) VALUES(@n,@p,@d,@i)
                ON CONFLICT(name) DO UPDATE SET patterns=@p, destination_dir=@d, icon=@i
                """;
            insert.Parameters.AddValue("@n", "docs");
            insert.Parameters.AddValue("@p", "pdf,docx");
            insert.Parameters.AddValue("@d", "/old/dir");
            insert.Parameters.AddValue("@i", "folder");
            insert.ExecuteNonQuery();
        }

        // the v1.8.0 update_category_dir statement
        int updated;
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE category_def SET destination_dir=@d WHERE name=@n";
            cmd.Parameters.AddValue("@d", "/new/dir");
            cmd.Parameters.AddValue("@n", "docs");
            updated = cmd.ExecuteNonQuery();
        }
        Assert.Equal(1, updated);

        lock (_db.Connection)
        using (var sel = _db.Connection.CreateCommand())
        {
            sel.CommandText = "SELECT destination_dir FROM category_def WHERE name='docs'";
            Assert.Equal("/new/dir", sel.ExecuteScalar() as string);
        }
    }
}
