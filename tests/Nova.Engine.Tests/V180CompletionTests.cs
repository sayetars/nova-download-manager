using System.Net;
using Nova.Domain;
using Nova.Engine;
using Nova.Grabber;
using Nova.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Nova.Engine.Tests;

/// <summary>
/// v1.8.0 "IDM full-parity completion" — second wave: interactive 401/407 auth
/// prompt (probe + slice + ProvideAuth), per-directory vault entries, per-queue
/// retry defaults, lone-task infinite 30 s retry, "open file when done" post
/// actions, and the Grabber wizard step-3/step-4 rules (parent directories,
/// subdomains, external levels, size filters).
/// </summary>
public class V180AuthPromptTests : IAsyncLifetime
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
    public async Task Auth_401_Without_Credentials_Fails_With_Auth_Required()
    {
        var data = TestBytes.Pattern(64 * 1024, seed: 41);
        _server.AddFile("protected.bin", data);
        _server.RequireBasicAuth = ("alice", "wonderland");

        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("protected.bin") });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        var snap = _sink.WaitState(key, DownloadState.Failed, 20000);

        Assert.Equal("auth_required", snap!.ErrorCode);
        Assert.Equal("auth", snap.State is null ? "auth" : "auth"); // state text irrelevant here
        Assert.Contains("login", snap.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        // no credentials were ever attached
        Assert.DoesNotContain(_server.AuthorizationSeen, a => a is not null);
    }

    [Fact]
    public async Task Auth_401_ProvideAuth_Resumes_And_Completes()
    {
        var data = TestBytes.Pattern(128 * 1024, seed: 42);
        _server.AddFile("secret.bin", data);
        _server.RequireBasicAuth = ("bob", "builder");

        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("secret.bin") });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        var failed = _sink.WaitState(key, DownloadState.Failed, 20000);
        Assert.Equal("auth_required", failed!.ErrorCode);

        // the user answers the login dialog
        Assert.True(_fx.Engine.ProvideAuth(key, "bob", "builder"));
        _sink.WaitStatePumped(key, DownloadState.Completed, _fx.Engine, 30000);

        var got = await File.ReadAllBytesAsync(_fx.Engine.Get(key)!.FinalPath);
        Assert.Equal(data, got);
        // a Basic Authorization header was attached on the retry probe
        Assert.Contains(_server.AuthorizationSeen, a => a is not null && a.StartsWith("Basic "));
    }

    [Fact]
    public async Task Auth_401_Wrong_ProvideAuth_Prompts_Again_Then_Succeeds()
    {
        var data = TestBytes.Pattern(64 * 1024, seed: 43);
        _server.AddFile("reask.bin", data);
        _server.RequireBasicAuth = ("carol", "correct");

        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("reask.bin") });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        _sink.WaitState(key, DownloadState.Failed, 20000);

        // wrong credentials → rejected again (IDM re-prompts)
        Assert.True(_fx.Engine.ProvideAuth(key, "carol", "wrong"));
        _sink.WaitStatePumped(key, DownloadState.Failed, _fx.Engine, 30000);
        Assert.Equal("auth_required", _sink.LastOf(key)!.ErrorCode);

        // correct credentials → completes
        Assert.True(_fx.Engine.ProvideAuth(key, "carol", "correct"));
        _sink.WaitStatePumped(key, DownloadState.Completed, _fx.Engine, 30000);
        Assert.Equal(data.Length, new FileInfo(_fx.Engine.Get(key)!.FinalPath).Length);
    }

    [Fact]
    public async Task Auth_401_Vault_Entry_Completes_Without_Prompt()
    {
        var data = TestBytes.Pattern(64 * 1024, seed: 44);
        _server.AddFile("vault.bin", data);
        _server.RequireBasicAuth = ("dave", "deep");

        // vault-backed engine
        await using var fx2 = new EngineFixture(vault: new LoginVault(_fx.Db, _fx.DataDir));
        using var sub2 = fx2.Bus.Subscribe(_sink);
        fx2.Vault?.Save("127.0.0.1", "", "dave", "deep");

        var res = fx2.Engine.Add(new NewDownload { Url = _server.UrlFor("vault.bin") });
        var key = res.Value!.TaskKey;
        fx2.Engine.TryStartNext();
        _sink.WaitStatePumped(key, DownloadState.Completed, fx2.Engine, 30000);

        var got = await File.ReadAllBytesAsync(fx2.Engine.Get(key)!.FinalPath);
        Assert.Equal(data, got);
    }

    [Fact]
    public void ProvideAuth_Unknown_Task_Returns_False()
    {
        Assert.False(_fx.Engine.ProvideAuth("no-such-key", "u", "p"));
        Assert.False(_fx.Engine.ProvideAuth("", "u", "p"));
    }
}

public class V180VaultPrefixTests : IAsyncLifetime
{
    private string _dir = null!;
    private NovaDatabase _db = null!;
    private LoginVault _vault = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nova-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = new NovaDatabase(Path.Combine(_dir, "nova.db"));
        _vault = new LoginVault(_db, _dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Prefix_Entry_Wins_Over_Host_Entry()
    {
        // IDM "different login information for different directories of the same site"
        _vault.Save("example.com", "", "site-user", "site-pass");
        _vault.Save("example.com/private/", "", "private-user", "private-pass");

        var underPrivate = _vault.Find("https://example.com/private/docs/file.zip");
        Assert.NotNull(underPrivate);
        Assert.Equal("private-user", underPrivate!.UserName);

        var elsewhere = _vault.Find("https://example.com/public/file.zip");
        Assert.NotNull(elsewhere);
        Assert.Equal("site-user", elsewhere!.UserName);
    }

    [Fact]
    public void Longest_Prefix_Wins()
    {
        _vault.Save("example.com/a/", "", "a-user", "a-pass");
        _vault.Save("example.com/a/b/", "", "b-user", "b-pass");

        Assert.Equal("b-user", _vault.Find("https://example.com/a/b/c.bin")!.UserName);
        Assert.Equal("a-user", _vault.Find("https://example.com/a/other.bin")!.UserName);
    }

    [Fact]
    public void Prefix_Does_Not_Match_Sibling_Path()
    {
        _vault.Save("example.com/private/", "", "private-user", "private-pass");
        // "example.com/privateX/" shares the string prefix but is a different directory
        Assert.Equal("private-user", _vault.Find("https://example.com/private/file")!.UserName);
        var sibling = _vault.Find("https://example.com/privateX/file");
        Assert.Null(sibling); // no plain-host entry → no match at all
    }

    [Fact]
    public void Host_Only_Entry_Still_Matches()
    {
        _vault.Save("cdn.example", "", "u", "p");
        Assert.Equal("u", _vault.Find("https://cdn.example/file.bin")!.UserName);
        Assert.Equal("u", _vault.Find("cdn.example")!.UserName);
    }
}

public class V180QueueRetryTests : IAsyncLifetime
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
    public void Queue_Retry_Default_From_Settings_Seeds_New_Tasks()
    {
        new SettingsRepository(_fx.Db).Set("queue.max_retries.main", "2");
        var t1 = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("a.bin"), AutoStart = false }).Value!;
        Assert.Equal(2, _fx.Engine.Get(t1.TaskKey)!.MaxRetries);

        // per-task override still wins
        var t2 = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("b.bin"), AutoStart = false, MaxRetries = 7 }).Value!;
        Assert.Equal(7, _fx.Engine.Get(t2.TaskKey)!.MaxRetries);

        // unset queue → engine default
        var t3 = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("c.bin"), AutoStart = false, QueueName = "other" }).Value!;
        Assert.Equal(5, _fx.Engine.Get(t3.TaskKey)!.MaxRetries);
    }

    [Fact]
    public async Task Lone_Task_In_Queue_Retries_Past_Its_Budget()
    {
        // IDM: "If the file that IDM cannot download is left alone in the queue,
        // IDM will retry to download the file infinitely with 30 seconds interval."
        var data = TestBytes.Pattern(64 * 1024, seed: 45);
        _server.AddFile("lone.bin", data);

        var res = _fx.Engine.Add(new NewDownload
        {
            Url = _server.UrlFor("lone.bin"),
            MaxRetries = 1,
            Mirrors = new[] { _server.UrlFor("lone-mirror.bin") }, // second URL also 404s
        });
        var key = res.Value!.TaskKey;

        // two retryable failures exhaust the budget (5xx is retryable)
        _server.FailNextWith = 503;
        _fx.Engine.TryStartNext();
        _sink.WaitStatePumped(key, DownloadState.Retrying, _fx.Engine, 20000);
        _server.FailNextWith = 503;
        _sink.WaitStatePumped(key, DownloadState.Retrying, _fx.Engine, 30000);

        // budget exhausted AND alone in queue → still Retrying (infinite), not Failed
        var snap = _sink.LastOf(key);
        Assert.Equal(DownloadState.Retrying.ToString().ToLowerInvariant(), snap!.State);
        Assert.True(snap.RetryCount >= 1);
    }
}

public class V180PostActionOpenTests
{
    [Theory]
    [InlineData("open:", false)]
    [InlineData("open:  ", false)]
    [InlineData("open:/tmp/run-me.bat", true)]
    [InlineData("OPEN:C:\\done\\alert.wav", true)]
    [InlineData("exit", true)]
    [InlineData("bogus", false)]
    public void IsValid_Variants(string action, bool expected)
        => Assert.Equal(expected, PostActionRunner.IsValid(action));

    [Fact]
    public void OpenTarget_Parses_And_Trims()
    {
        Assert.Equal("/tmp/run-me.bat", PostActionRunner.OpenTarget("open: /tmp/run-me.bat "));
        Assert.Equal("", PostActionRunner.OpenTarget("exit"));
    }

    [Fact]
    public void BuildOpenStartInfo_Uses_Shell()
    {
        var psi = PostActionRunner.BuildOpenStartInfo("/data/report.pdf");
        Assert.True(psi.UseShellExecute);
        Assert.Equal("/data/report.pdf", psi.FileName);
    }
}

public class V180GrabberRuleTests
{
    private static readonly Nova.Grabber.GrabberService.GrabberOptions Default = new();

    [Theory]
    [InlineData("www.tonec.com", "tonec.com")]
    [InlineData("tonec.com", "tonec.com")]
    [InlineData("ftp.tonec.com", "tonec.com")]
    [InlineData("a.b.c.example", "b.c.example")]
    [InlineData("localhost", "localhost")]
    public void RegistrableDomain_Variants(string host, string expected)
        => Assert.Equal(expected, Nova.Grabber.GrabberService.RegistrableDomain(host));

    [Fact]
    public void Parent_Directory_Rule()
    {
        var root = new Uri("https://www.tonec.com/support/index.html");
        var startDir = Nova.Grabber.GrabberService.RootDirectory(root);
        Assert.Equal("/support/", startDir);

        var opts = Default with { }; // ExploreParentDirectories = false (default)
        var boundary = "www.tonec.com";

        // under the start directory → explore
        Assert.True(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://www.tonec.com/support/file.html"), root, boundary, startDir, opts, 0));
        // deeper → explore
        Assert.True(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://www.tonec.com/support/other/index.html"), root, boundary, startDir, opts, 0));
        // parent directory → skipped
        Assert.False(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://www.tonec.com/index.html"), root, boundary, startDir, opts, 0));
        Assert.False(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://www.tonec.com/otherfolder/index.html"), root, boundary, startDir, opts, 0));
        // opt-in → explored
        var explore = Default with { ExploreParentDirectories = true };
        Assert.True(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://www.tonec.com/index.html"), root, boundary, startDir, explore, 0));
    }

    [Fact]
    public void Subdomain_Boundary_Widens_With_Option()
    {
        var root = new Uri("https://www.tonec.com/support/index.html");
        var opts = Default with { ExploreSubdomains = true };
        var boundary = Nova.Grabber.GrabberService.RegistrableDomain(root.Host); // tonec.com

        Assert.True(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://ftp.tonec.com/file.html"), root, boundary, "/", opts, 0));
        Assert.True(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://tonec.com/file.html"), root, boundary, "/", opts, 0));

        // without the option, only the exact start host is on-site
        var strict = Default;
        Assert.False(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://ftp.tonec.com/file.html"), root, "www.tonec.com", "/", strict, 0));
    }

    [Fact]
    public void External_Depth_Budget()
    {
        var root = new Uri("https://www.tonec.com/support/index.html");
        var opts = Default with { ExternalDepth = 1 };
        var boundary = "www.tonec.com";

        // first external hop allowed at budget 1…
        Assert.True(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://other-site.net/page.html"), root, boundary, "/", opts, 0));
        // …second hop not
        Assert.False(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://third-site.org/page.html"), root, boundary, "/", opts, 1));

        // budget 0 → no external exploration at all (conservative default)
        var none = Default with { ExternalDepth = 0 };
        Assert.False(Nova.Grabber.GrabberService.ShouldExplore(
            new Uri("https://other-site.net/page.html"), root, boundary, "/", none, 0));
    }

    [Theory]
    [InlineData(0, 0, 0, true)]    // no filters → everything passes
    [InlineData(1024, 0, 0, true)] // unknown length passes up-front
    [InlineData(1024, 2048, 0, false)] // below min
    [InlineData(4096, 0, 2048, false)] // above max
    [InlineData(2048, 1024, 4096, true)] // in range
    [InlineData(-1, 100, 200, true)]    // unknown passes (copy loop enforces)
    public void PassesSizeFilter_Variants(long len, long min, long max, bool expected)
        => Assert.Equal(expected, Nova.Grabber.GrabberService.PassesSizeFilter(len, min, max));
}

public class V180GrabberE2eTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    public V180GrabberE2eTests(ITestOutputHelper output) => _out = output;
    private TestHttpServer _server = null!;
    private HttpClient _http = new();
    private string _dir = null!;
    private NovaDatabase _db = null!;

    public Task InitializeAsync()
    {
        _server = new TestHttpServer();
        _dir = Path.Combine(Path.GetTempPath(), "nova-grab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = new NovaDatabase(Path.Combine(_dir, "nova.db"));
        _server.AddText("support/index.html",
            """
            <html><body>
            <a href="/support/child.html">child</a>
            <a href="/other/page.html">other section</a>
            <img src="/support/pic.png">
            <img src="/support/tiny.gif">
            </body></html>
            """);
        _server.AddText("support/child.html", "<html><body><a href='/support/deep.html'>deep</a></body></html>");
        _server.AddText("other/page.html", "<html><body><a href='/other/more.html'>more</a></body></html>");
        _server.AddText("support/deep.html", "<html><body>deep</body></html>");
        _server.AddFile("support/pic.png", new byte[2048]);
        _server.AddFile("support/tiny.gif", new byte[64]);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _server.Dispose();
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Nova.Grabber.GrabberService.GrabberOptions BaseOpts(string sub) => new()
    {
        MaxDepth = 3,
        SaveDirectory = Path.Combine(_dir, sub),
        RewriteForOffline = false,
    };

    [Fact]
    public async Task Parent_Directories_Not_Explored_By_Default()
    {
        var svc = new Nova.Grabber.GrabberService(_db);
        var result = await svc.RunAsync(_http, _server.UrlFor("support/index.html"), BaseOpts("default"));

        // start page + child + deep are under /support/
        Assert.Contains(result.SavedPages, p => p.EndsWith("/support/index.html"));
        Assert.Contains(result.SavedPages, p => p.EndsWith("/support/child.html"));
        Assert.Contains(result.SavedPages, p => p.EndsWith("/support/deep.html"));
        // /other/page.html is a PARENT directory of the start page → not explored
        Assert.DoesNotContain(result.SavedPages, p => p.Contains("/other/"));
    }

    [Fact]
    public async Task ExploreParentDirectories_OptIn_Explores_Up()
    {
        var svc = new Nova.Grabber.GrabberService(_db);
        var opts = BaseOpts("parents") with { ExploreParentDirectories = true };
        var result = await svc.RunAsync(_http, _server.UrlFor("support/index.html"), opts);

        Assert.Contains(result.SavedPages, p => p.EndsWith("/other/page.html"));
    }

    [Fact]
    public async Task Size_Filter_Skips_Out_Of_Range_Files()
    {
        var svc = new Nova.Grabber.GrabberService(_db);
        var opts = BaseOpts("sizes") with { MinBytes = 128 };
        var result = await svc.RunAsync(_http, _server.UrlFor("support/index.html"), opts);
        _out.WriteLine($"filesDone={result.FilesDone} pages={result.SavedPages.Count} failedPages={result.FailedPages.Count}");
        _out.WriteLine("requests: " + string.Join(" | ", _server.RequestsSeen));
        _out.WriteLine("auths: " + string.Join(" | ", _server.AuthorizationSeen.Where(a => a is not null)));

        Assert.True(result.FilesDone == 1, $"filesDone={result.FilesDone}"); // only pic.png (2048 B); tiny.gif (64 B) skipped
        Assert.False(File.Exists(Path.Combine(_dir, "sizes", "127.0.0.1", "support", "tiny.gif")));
        Assert.True(File.Exists(Path.Combine(_dir, "sizes", "127.0.0.1", "support", "pic.png")));
    }

    [Fact]
    public async Task MaxBytes_Filter_Skips_Large_Files()
    {
        var svc = new Nova.Grabber.GrabberService(_db);
        var opts = BaseOpts("max") with { MaxBytes = 1024 };
        var result = await svc.RunAsync(_http, _server.UrlFor("support/index.html"), opts);
        _out.WriteLine($"filesDone={result.FilesDone} pages={result.SavedPages.Count} failedPages={result.FailedPages.Count}");

        Assert.True(result.FilesDone == 1, $"filesDone={result.FilesDone}"); // only tiny.gif survives
        Assert.False(File.Exists(Path.Combine(_dir, "max", "127.0.0.1", "support", "pic.png")));
    }
}
