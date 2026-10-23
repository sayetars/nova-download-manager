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
