using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nova.Domain;
using Nova.Engine;
using Nova.Persistence;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>
/// v1.3.0 "Beyond IDM" suite: Metalink v4/v3 + Metalink/HTTP, checksum sidecars,
/// multi-source parallel download, cookies + custom headers, piece-hash repair,
/// sequential allocation, RSS/Atom feeds, archive extraction, filename tokens,
/// WebDAV listing, cookies.txt import.
/// </summary>
public class V13FeatureTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private EngineFixture _fx = null!;
    private TestSink _sink = null!;
    private IDisposable _sub = null!;

    public Task InitializeAsync()
    {
        _server = new TestHttpServer();
        _fx = new EngineFixture();
        _sink = new TestSink();
        _sub = _fx.Bus.Subscribe(_sink);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _sub.Dispose();
        await _fx.DisposeAsync();
        _server.Dispose();
    }

    private Task<string> StartAndWait(string url, Func<NewDownload, NewDownload>? configure = null, bool pumpThroughMirrorRotation = false)
    {
        var spec = configure?.Invoke(new NewDownload { Url = url }) ?? new NewDownload { Url = url };
        var res = _fx.Engine.Add(spec);
        Assert.True(res.Ok, res.Error?.ToString());
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        var snap = pumpThroughMirrorRotation
            ? _sink.WaitStatePumped(key, DownloadState.Completed, _fx.Engine)
            : _sink.WaitState(key, DownloadState.Completed);
        Assert.NotNull(snap);
        return Task.FromResult(key);
    }

    // ---- Metalink v4 ----------------------------------------------------------

    [Fact]
    public async Task Metalink4_Expands_To_Mirror_And_Verifies_Hash()
    {
        var data = TestBytes.Pattern(512 * 1024, seed: 7);
        _server.AddFile("real-file.bin", data);
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var meta = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <metalink xmlns="urn:ietf:params:xml:ns:metalink">
              <files>
                <file name="real-file.bin">
                  <size>{data.Length}</size>
                  <verification>
                    <hash type="sha256">{sha}</hash>
                  </verification>
                  <resources>
                    <url priority="1">{_server.UrlFor("real-file.bin")}</url>
                  </resources>
                </file>
              </files>
            </metalink>
            """;
        _server.AddText("payload.meta4", meta);

        var key = await StartAndWait(_server.UrlFor("payload.meta4"));
        var task = _fx.Engine.Get(key)!;
        Assert.Equal("real-file.bin", task.FileName);
        Assert.Equal($"sha256:{sha}", task.ExpectedHash);
        Assert.Equal(ValidationStatus.Passed, task.Validation);
        Assert.True(data.SequenceEqual(await File.ReadAllBytesAsync(task.FinalPath)));
    }

    [Fact]
    public void Metalink_ParseXml_Supports_V3_Pieces_And_Mirrors()
    {
        var meta = """
            <?xml version="1.0" encoding="UTF-8"?>
            <metalink xmlns="http://www.metalinker.org/" version="3.0">
              <files>
                <file name="big.iso">
                  <verification>
                    <hash type="md5">d41d8cd98f00b204e9800998ecf8427e</hash>
                    <pieces length="262144" type="sha1">
                      <hash>aaaabbbbccccddddeeeeffffaaaabbbbccccdddd</hash>
                      <hash>0000111122223333444455556666777788889999</hash>
                    </pieces>
                  </verification>
                  <resources>
                    <url type="http" priority="2">http://b.example/big.iso</url>
                    <url type="http" priority="1" maxconnections="4">http://a.example/big.iso</url>
                  </resources>
                </file>
              </files>
            </metalink>
            """;
        var meta1 = Metalink.ParseXml(meta);
        Assert.NotNull(meta1);
        Assert.Equal("big.iso", meta1!.FileName);
        Assert.Equal("md5", meta1.Hashes.Keys.Single());
        Assert.Equal(2, meta1.Pieces!.Hashes.Count);
        Assert.Equal(262144, meta1.Pieces.PieceSize);
        Assert.Equal("http://a.example/big.iso", meta1.Mirrors.OrderBy(m => m.Priority).First().Url);
        Assert.Equal(4, meta1.Mirrors.First(m => m.Url.StartsWith("http://a")).MaxConnections);
    }

    [Fact]
    public void MetalinkHttp_Link_And_Digest_Headers_Are_Parsed()
    {
        var link = "<http://mirror1.example/f.bin>; rel=duplicate; pri=1; geo=DE, <http://mirror2.example/f.bin>; rel=duplicate; pri=2";
        var parsed = Metalink.ParseMetalinkHttp(new[] { link }, "SHA-256=3q2+7w==, MD5=CY9rzUYh03PK3k6DJie09g==");
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Mirrors.Count);
        Assert.Equal("http://mirror1.example/f.bin", parsed.Value.Mirrors[0].Url);
        Assert.Equal("deadbeef", parsed.Value.Hashes["sha256"]);
    }

    // ---- checksum sidecars ----------------------------------------------------

    [Fact]
    public async Task Sha256_Sidecar_Is_Discovered_And_Verified()
    {
        var data = TestBytes.Pattern(256 * 1024, seed: 9);
        _server.AddFile("sidecar-target.bin", data);
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        _server.AddText("sidecar-target.bin.sha256", $"{sha}  sidecar-target.bin\n");

        var key = await StartAndWait(_server.UrlFor("sidecar-target.bin"));
        var task = _fx.Engine.Get(key)!;
        Assert.Equal($"sha256:{sha}", task.ExpectedHash);
        Assert.Equal(ValidationStatus.Passed, task.Validation);
    }

    [Fact]
    public void Sidecar_Parser_Handles_Coreutils_And_Sfv_Formats()
    {
        var hex64 = new string('a', 62) + "1c";   // valid 64-char sha256
        var sha = Metalink.ParseSidecar($"{hex64}  *file.bin\n", ".sha256", "file.bin");
        Assert.NotNull(sha);
        Assert.Equal(("sha256", hex64), sha!.Value);

        var bad = Metalink.ParseSidecar($"{new string('z', 64)}  file.bin\n", ".sha256", "file.bin");
        Assert.Null(bad);

        var nameMismatch = Metalink.ParseSidecar($"{hex64}  other.bin\n", ".sha256", "file.bin");
        Assert.Null(nameMismatch);

        var crc = Metalink.ParseSidecar("file.bin DEADBEEF\n", ".sfv", "file.bin");
        Assert.NotNull(crc);
        Assert.Equal(("crc32", "deadbeef"), crc!.Value);
    }

    // ---- cookies + custom headers ----------------------------------------------

    [Fact]
    public async Task Cookies_And_Extra_Headers_Are_Sent()
    {
        var data = TestBytes.Pattern(128 * 1024, seed: 3);
        _server.AddFile("authed.bin", data);
        var key = await StartAndWait(_server.UrlFor("authed.bin"), s => s with
        {
            Cookies = "session=xyz; premium=1",
            ExtraHeaders = """{"X-Nova-Test":"nova-header"}""",
        });
        var task = _fx.Engine.Get(key)!;
        Assert.True(task.Validation is ValidationStatus.NotChecked or ValidationStatus.Passed);

        var obs = _server.HeaderObservations.Where(o => o.Path.EndsWith("authed.bin")).ToList();
        Assert.NotEmpty(obs);
        Assert.Contains(obs, o => o.Cookie is not null && o.Cookie.Contains("session=xyz"));
        Assert.Contains(obs, o => o.Custom == "nova-header");
    }

    [Fact]
    public void Netscape_Cookie_File_Parses_To_Host_Headers()
    {
        string L(params string[] f) => string.Join('\t', f);
        var txt = string.Join('\n',
            "# Netscape HTTP Cookie File",
            L(".example.com", "TRUE", "/", "TRUE", "1893456000", "session", "abc"),
            "#HttpOnly_" + L(".example.com", "TRUE", "/", "TRUE", "1893456000", "ho", "debug"),
            L("login.example.com", "FALSE", "/", "FALSE", "1893456000", "user", "bob"));
        var jar = CookieFile.ParseNetscape(txt);
        Assert.Equal(2, jar.Count);
        Assert.Contains("session=abc; ho=debug", jar["example.com"]);
        Assert.Equal("user=bob", jar["login.example.com"]);
    }

    // ---- multi-source download -------------------------------------------------

    [Fact]
    public async Task Mirror_Fallback_Serves_When_Primary_Fails()
    {
        var data = TestBytes.Pattern(1024 * 1024, seed: 11);
        _server.AddFile("real-via-mirror.bin", data);
        // primary URL is never registered on the server → 404 → engine mirror failover
        var primary = _server.UrlFor("definitely-missing.bin");
        var mirror = _server.UrlFor("real-via-mirror.bin");

        var key = await StartAndWait(primary, s => s with { Mirrors = new[] { mirror } }, pumpThroughMirrorRotation: true);
        var path = _fx.Engine.Get(key)!.FinalPath;
        Assert.True(File.Exists(path));
        Assert.Equal(data.Length, new FileInfo(path).Length);
        Assert.True(data.SequenceEqual(await File.ReadAllBytesAsync(path)));
    }

    // ---- piece hashes + repair --------------------------------------------------

    [Fact]
    public async Task PieceHashes_Correct_Verify_Passes()
    {
        var data = TestBytes.Pattern(512 * 1024, seed: 21);
        _server.AddFile("pieced.bin", data);
        const int pieceSize = 128 * 1024;
        var hashes = new List<string>();
        for (var off = 0; off < data.Length; off += pieceSize)
        {
            var n = Math.Min(pieceSize, data.Length - off);
            using var part = new MemoryStream(data, off, n, writable: false);
            hashes.Add(await Hashing.ComputeAsync(part, "sha1", CancellationToken.None));
        }
        var info = new PieceHashInfo { Algo = "sha1", PieceSize = pieceSize, Hashes = hashes };

        var key = await StartAndWait(_server.UrlFor("pieced.bin"),
            s => s with { PieceHashes = info.ToJson() });
        var task = _fx.Engine.Get(key)!;
        Assert.True(data.SequenceEqual(await File.ReadAllBytesAsync(task.FinalPath)));
    }

    [Fact]
    public async Task PieceHashes_Wrong_Piece_Fails_Honestly()
    {
        var data = TestBytes.Pattern(512 * 1024, seed: 22);
        _server.AddFile("broken-piece.bin", data);
        var info = new PieceHashInfo
        {
            Algo = "sha1",
            PieceSize = 128 * 1024,
            Hashes = { new string('a', 40), new string('a', 40), new string('a', 40), new string('a', 40) },
        };

        var res = _fx.Engine.Add(new NewDownload
        {
            Url = _server.UrlFor("broken-piece.bin"),
            PieceHashes = info.ToJson(),
        });
        Assert.True(res.Ok);
        _fx.Engine.TryStartNext();
        var snap = _sink.WaitState(res.Value!.TaskKey, DownloadState.Failed, timeoutMs: 30_000);
        Assert.NotNull(snap);
        var task = _fx.Engine.Get(res.Value.TaskKey)!;
        Assert.Equal("piece_verify_failed", task.ErrorCode);
    }

    // ---- sequential allocation ---------------------------------------------------

    [Fact]
    public async Task Sequential_Task_Completes_Byte_Exact()
    {
        var data = TestBytes.Pattern(2 * 1024 * 1024, seed: 31);
        _server.AddFile("seq.bin", data);
        var key = await StartAndWait(_server.UrlFor("seq.bin"), s => s with { Sequential = true });
        var task = _fx.Engine.Get(key)!;
        Assert.True(task.Sequential);
        Assert.True(data.SequenceEqual(await File.ReadAllBytesAsync(task.FinalPath)));
    }

    // ---- RSS / Atom feeds ---------------------------------------------------------

    [Fact]
    public async Task Feed_Rule_Adds_New_Enclosures_Once()
    {
        var audio = TestBytes.Pattern(64 * 1024, seed: 41);
        _server.AddFile("episode1.mp3", audio);
        var rss = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel>
              <title>Podcast</title>
              <item>
                <title>Episode 1</title>
                <guid>ep-1</guid>
                <pubDate>Mon, 06 Oct 2026 10:00:00 GMT</pubDate>
                <enclosure url="{_server.UrlFor("episode1.mp3")}" length="{audio.Length}" type="audio/mpeg"/>
              </item>
            </channel></rss>
            """;
        _server.AddText("feed.xml", rss);

        var repo = new FeedRuleRepository(_fx.Db);
        var feed = new FeedService(_fx.Engine, repo, _fx.Http);
        var ruleId = repo.Insert(new FeedRule { Name = "pod", Url = _server.UrlFor("feed.xml"), Filters = "episode" });

        var first = await feed.RunRuleAsync(ruleId);
        Assert.Equal(1, first.Added);
        var again = await feed.RunRuleAsync(ruleId);
        Assert.Equal(0, again.Added);   // seen-state prevents re-download
        Assert.Equal(1, again.Seen);

        // task was created (auto-start → queued; nothing pumps the scheduler in this fixture)
        var added = _fx.Engine.All().Single(t => t.Url.EndsWith("episode1.mp3"));
        Assert.Equal("episode1.mp3", added.FileName);
        Assert.Equal(_server.UrlFor("episode1.mp3"), added.Url);
    }

    [Fact]
    public void Feed_Parser_Handles_RSS_And_Atom()
    {
        var rss = FeedService.ParseFeed("""
            <rss version="2.0"><channel>
              <item><title>A</title><guid>g1</guid><enclosure url="https://x/a.zip"/></item>
              <item><title>B</title><link>https://x/b.bin</link></item>
            </channel></rss>
            """);
        Assert.Equal(2, rss.Count);
        Assert.Equal("https://x/a.zip", rss[0].Url);
        Assert.Equal("https://x/b.bin", rss[1].Url);

        var atom = FeedService.ParseFeed("""
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><title>C</title><id>c1</id>
                <link rel="enclosure" href="https://x/c.mp4"/></entry>
            </feed>
            """);
        Assert.Single(atom);
        Assert.Equal("https://x/c.mp4", atom[0].Url);
    }

    // ---- archive extraction -------------------------------------------------------

    [Fact]
    public void Zip_Extract_Writes_All_Entries()
    {
        var dir = Path.Combine(_fx.DataDir, "zipwork");
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "bundle.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e1 = archive.CreateEntry("a/one.txt");
            using (var w = new StreamWriter(e1.Open())) w.Write("hello-1");
            var e2 = archive.CreateEntry("two.txt");
            using (var w = new StreamWriter(e2.Open())) w.Write("hello-2");
        }

        var dest = Path.Combine(dir, "out");
        var result = ArchiveTools.Extract(zipPath, dest, CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.Entries);
        Assert.Equal("hello-1", File.ReadAllText(Path.Combine(dest, "a", "one.txt")));
        Assert.Equal("hello-2", File.ReadAllText(Path.Combine(dest, "two.txt")));
    }

    // ---- filename tokens -------------------------------------------------------------

    [Fact]
    public void Filename_Tokens_Expand_With_Context()
    {
        var ctx = new FilenameTokens.Context
        {
            Index = 5,
            Url = "https://cdn.example.org/files/report.pdf",
            Category = "docs",
            Now = new DateTimeOffset(2026, 10, 24, 15, 30, 10, TimeSpan.Zero),
        };
        var expanded = FilenameTokens.Expand("docs/%date%/%index%-%title%.%ext%", ctx);
        Assert.Equal("docs/2026-10-24/005-report.pdf", expanded);
        Assert.True(FilenameTokens.HasTokens("%domain%/%title%"));
        Assert.False(FilenameTokens.HasTokens("plain-name.bin"));
    }

    // ---- WebDAV -------------------------------------------------------------------------

    [Fact]
    public void WebDav_Propfind_Is_Parsed_And_Schemes_Rewrite()
    {
        var xml = """
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/remote.php/webdav/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>/remote.php/webdav/Docs/</d:href>
                <d:propstat><d:prop><d:displayname>Docs</d:displayname>
                  <d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>/remote.php/webdav/photo.jpg</d:href>
                <d:propstat><d:prop><d:displayname>photo.jpg</d:displayname>
                  <d:getcontentlength>20480</d:getcontentlength></d:prop></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        var entries = WebDav.ParsePropfind(xml, "http://x/remote.php/webdav/");
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "Docs" && e.IsDirectory);
        var jpg = entries.Single(e => e.Name == "photo.jpg");
        Assert.False(jpg.IsDirectory);
        Assert.Equal(20480, jpg.Size);

        Assert.Equal("https://host/f", WebDav.Rewrite("davs://host/f"));
        Assert.Equal("http://host/f", WebDav.Rewrite("dav://host/f"));
    }

    // ---- hashing framework ---------------------------------------------------------------

    [Fact]
    public async Task Hashing_Crc32_Matches_Known_Vector()
    {
        // CRC32("123456789") = 0xCBF43926 (standard IEEE check value)
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes("123456789"));
        var crc = await Hashing.ComputeAsync(ms, "crc32", CancellationToken.None);
        Assert.Equal("cbf43926", crc);
    }

    [Fact]
    public void Hashing_ParseQualified_Infers_Algo_From_Length()
    {
        Assert.Equal(("sha256", new string('a', 64)), Hashing.ParseQualified(new string('a', 64))!.Value);
        Assert.Equal(("md5", new string('b', 32)), Hashing.ParseQualified(new string('b', 32))!.Value);
        Assert.Equal(("crc32", "deadbeef"), Hashing.ParseQualified("crc32:deadbeef")!.Value);
        Assert.Null(Hashing.ParseQualified("rot13:xyz"));
    }

    // ---- duplicate-add stays visible on the wire -----------------------------------------

    [Fact]
    public async Task Duplicate_Add_Returns_Existing_TaskKey()
    {
        var data = TestBytes.Pattern(64 * 1024, seed: 51);
        _server.AddFile("dup.bin", data);
        var url = _server.UrlFor("dup.bin");
        var r1 = _fx.Engine.Add(new NewDownload { Url = url });
        var r2 = _fx.Engine.Add(new NewDownload { Url = url });
        Assert.False(r1.Value!.Duplicate);
        Assert.True(r2.Value!.Duplicate);
        Assert.Equal(r1.Value.TaskKey, r2.Value.TaskKey);
        await Task.CompletedTask;
    }
}
