using System.Text;
using Nova.Domain;
using Nova.Engine;
using Nova.Persistence;
using Xunit;

namespace Nova.Engine.Tests;

public class DownloadEngineTests : IAsyncLifetime
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

    private async Task<string> StartAndWait(string url, string? fileName = null, string? sha = null, int? conns = null)
    {
        var res = _fx.Engine.Add(new NewDownload { Url = url, FileName = fileName, ExpectedSha256 = sha, MaxConnections = conns });
        Assert.True(res.Ok, res.Error?.ToString());
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        var snap = _sink.WaitState(key, DownloadState.Completed);
        Assert.NotNull(snap);
        return key;
    }

    // 1. small file ------------------------------------------------------------
    [Fact]
    public async Task Small_File_Downloads_And_Matches_Content()
    {
        var data = TestBytes.Pattern(256 * 1024);
        _server.AddFile("small.bin", data);
        var key = await StartAndWait(_server.UrlFor("small.bin"));
        var path = _fx.Engine.Get(key)!.FinalPath;
        Assert.True(File.Exists(path));
        Assert.Equal(data.Length, new FileInfo(path).Length);
        Assert.True(data.SequenceEqual(await File.ReadAllBytesAsync(path)));
    }

    // 2. multipart -------------------------------------------------------------
    [Fact]
    public async Task Large_File_Uses_Multipart_And_Matches_Content()
    {
        var data = TestBytes.Pattern(8 * 1024 * 1024, seed: 42);
        _server.AddFile("large.bin", data);
        var key = await StartAndWait(_server.UrlFor("large.bin"), conns: 4);
        var task = _fx.Engine.Get(key)!;
        Assert.Equal(4, task.SegmentCount); // 8MB / 512KB → min(4, 16) = 4 segments
        var got = await File.ReadAllBytesAsync(task.FinalPath);
        if (!data.SequenceEqual(got))
        {
            var first = Enumerable.Range(0, Math.Min(data.Length, got.Length)).First(i => data[i] != got[i]);
            Assert.Fail($"content mismatch at {first}: expected {data[first]:X2} got {got[first]:X2}; len exp={data.Length} got={got.Length}");
        }
    }

    // 3. no range support -------------------------------------------------------
    [Fact]
    public async Task Server_Without_Ranges_Falls_Back_To_Single_Connection()
    {
        _server.SupportsRanges = false;
        var data = TestBytes.Pattern(3 * 1024 * 1024);
        _server.AddFile("norange.bin", data);
        var key = await StartAndWait(_server.UrlFor("norange.bin"), conns: 8);
        var task = _fx.Engine.Get(key)!;
        Assert.Equal(1, task.SegmentCount);
        Assert.True(data.SequenceEqual(await File.ReadAllBytesAsync(task.FinalPath)));
    }

    // 4. pause / resume ----------------------------------------------------------
    [Fact]
    public async Task Pause_Then_Resume_Completes_With_Correct_Bytes()
    {
        var data = TestBytes.Pattern(6 * 1024 * 1024, seed: 99);
        _server.AddFile("pausable.bin", data);
        _server.ThrottleBytesPerSec = 700 * 1024; // ~9s total → deterministic pause window
        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("pausable.bin"), MaxConnections = 4 });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();

        await Wait.UntilAsync(() => _fx.Engine.Get(key)!.ReceivedBytes > 512 * 1024);
        _fx.Engine.Pause(key);
        await Wait.UntilAsync(() => _fx.Engine.Get(key)!.State == DownloadState.Paused, timeoutMs: 20000);
        var pausedAt = _fx.Engine.Get(key)!.ReceivedBytes;
        Assert.True(pausedAt > 0);
        Assert.True(pausedAt < data.Length);

        _server.ThrottleBytesPerSec = 0; // finish quickly
        _fx.Engine.Resume(key);
        _fx.Engine.TryStartNext();
        _sink.WaitStatePumped(key, DownloadState.Completed, _fx.Engine, 40000);
        Assert.True(data.SequenceEqual(await File.ReadAllBytesAsync(_fx.Engine.Get(key)!.FinalPath)));
    }

    // 5. crash/restart recovery ---------------------------------------------------
    [Fact]
    public async Task Engine_Restart_Recovers_Partial_Progress()
    {
        var data = TestBytes.Pattern(5 * 1024 * 1024, seed: 1234);
        _server.AddFile("recover.bin", data);
        _server.ThrottleBytesPerSec = 600 * 1024;

        string key;
        {
            var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("recover.bin"), MaxConnections = 2 });
            key = res.Value!.TaskKey;
            _fx.Engine.TryStartNext();
            await Wait.UntilAsync(() => _fx.Engine.Get(key)!.ReceivedBytes > 256 * 1024);
            var t = _fx.Engine.Get(key)!;
            Assert.True(t.ReceivedBytes > 0);
        }
        // simulate engine crash: dispose WITHOUT graceful pause (hard stop); keep data on disk
        _fx.DeleteOnDispose = false;
        await _fx.DisposeAsync();

        // new engine over same DB — recovery pass marks interrupted & requeues
        _fx = await EngineFixture.ReopenAsync(_fx.DataDir);
        _fx.DeleteOnDispose = true;
        _sub = _fx.Bus.Subscribe(_sink);
        _server.ThrottleBytesPerSec = 0;
        _sink.WaitStatePumped(key, DownloadState.Completed, _fx.Engine, 40000);
        var got = await File.ReadAllBytesAsync(_fx.Engine.Get(key)!.FinalPath);
        Assert.True(data.SequenceEqual(got));
    }

    // 6-7. 404 / 403 ---------------------------------------------------------------
    [Fact]
    public async Task Missing_File_Fails_As_NotFound()
    {
        var key = await StartAndWaitFailed(_server.UrlFor("missing.bin"));
        var task = _fx.Engine.Get(key)!;
        Assert.Equal("http_404", task.ErrorCode);
        Assert.Equal(ErrorCategory.NotFound, task.ErrorCategory);
    }

    [Fact]
    public async Task Forbidden_Fails_With_Auth_Category()
    {
        var data = TestBytes.Pattern(1024);
        _server.AddFile("secret.bin", data);
        _server.FailNextWith = 403;
        var key = await StartAndWaitFailed(_server.UrlFor("secret.bin"));
        var task = _fx.Engine.Get(key)!;
        Assert.Equal("http_403", task.ErrorCode);
        Assert.Equal(ErrorCategory.Forbidden, task.ErrorCategory);
    }

    private async Task<string> StartAndWaitFailed(string url, int maxRetries = 0)
    {
        var res = _fx.Engine.Add(new NewDownload { Url = url });
        var key = res.Value!.TaskKey;
        var t = _fx.Engine.Get(key)!;
        t.MaxRetries = maxRetries;
        _fx.Engine.TryStartNext();
        _sink.WaitStatePumped(key, DownloadState.Failed, _fx.Engine, 30000);
        return key;
    }

    // 8. retry then succeed (5xx once) ---------------------------------------------
    [Fact]
    public async Task Transient_500_Then_Success()
    {
        var data = TestBytes.Pattern(512 * 1024, seed: 5);
        _server.AddFile("flaky.bin", data);
        _server.FailNextWith = 500;
        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("flaky.bin") });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();

        // probe gets 500 → retrying → backoff (~2s) → probe succeeds → download completes.
        _sink.WaitStatePumped(key, DownloadState.Completed, _fx.Engine, 40000);
        Assert.True(File.Exists(_fx.Engine.Get(key)!.FinalPath));
    }

    // 9. query strings ------------------------------------------------------------
    [Fact]
    public async Task Query_String_Url_Downloads()
    {
        var data = TestBytes.Pattern(64 * 1024);
        _server.AddFile("q.bin", data);
        var key = await StartAndWait(_server.UrlFor("q.bin") + "?token=abc&exp=123&x=1");
        Assert.True(File.Exists(_fx.Engine.Get(key)!.FinalPath));
    }

    // 10. Persian filename ----------------------------------------------------------
    [Fact]
    public async Task Persian_Filename_From_Url_Is_Preserved()
    {
        var data = TestBytes.Pattern(32 * 1024);
        var persianName = Uri.EscapeDataString("ویدئوی-آزمایشی-نوا.mp4");
        _server.AddFile(persianName, data);
        var key = await StartAndWait(_server.UrlFor(persianName));
        var task = _fx.Engine.Get(key)!;
        Assert.EndsWith(".mp4", task.FileName);
        Assert.Contains("نوا", task.FileName);
        Assert.True(File.Exists(task.FinalPath));
    }

    // 11. validator change → restart -------------------------------------------------
    [Fact]
    public async Task Validator_Change_Restarts_Download()
    {
        var data1 = TestBytes.Pattern(2 * 1024 * 1024, seed: 1);
        var data2 = TestBytes.Pattern(2 * 1024 * 1024, seed: 2);
        _server.AddFile("changing.bin", data1);
        _server.EtagValue = "\"etag-v1\"";
        _server.ThrottleBytesPerSec = 500 * 1024;

        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("changing.bin"), MaxConnections = 2 });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        await Wait.UntilAsync(() => _fx.Engine.Get(key)!.ReceivedBytes > 128 * 1024);
        _fx.Engine.Pause(key);
        await Wait.UntilAsync(() => _fx.Engine.Get(key)!.State == DownloadState.Paused);

        // server now serves a DIFFERENT file with a different etag
        _server.AddFile("changing.bin", data2);
        _server.EtagValue = "\"etag-v2\"";
        _server.ThrottleBytesPerSec = 0;

        _fx.Engine.Resume(key);
        _fx.Engine.TryStartNext();
        _sink.WaitStatePumped(key, DownloadState.Completed, _fx.Engine, 40000);
        var got = await File.ReadAllBytesAsync(_fx.Engine.Get(key)!.FinalPath);
        Assert.True(data2.SequenceEqual(got)); // new version, not a mix of v1+v2
    }

    // 12. hash mismatch ---------------------------------------------------------------
    [Fact]
    public async Task Wrong_Expected_Hash_Fails_With_HashMismatch()
    {
        var data = TestBytes.Pattern(256 * 1024);
        _server.AddFile("hashed.bin", data);
        var wrongSha = new string('a', 64);
        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("hashed.bin"), ExpectedSha256 = wrongSha });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        _sink.WaitStatePumped(key, DownloadState.Failed, _fx.Engine, 30000);
        var task = _fx.Engine.Get(key)!;
        Assert.Equal("hash_mismatch", task.ErrorCode);
        Assert.Equal(ErrorCategory.HashMismatch, task.ErrorCategory);
    }

    [Fact]
    public async Task Correct_Expected_Hash_Passes()
    {
        var data = TestBytes.Pattern(256 * 1024);
        _server.AddFile("hashed2.bin", data);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        var key = await StartAndWait(_server.UrlFor("hashed2.bin"), sha: sha);
        var task = _fx.Engine.Get(key)!;
        Assert.Equal(ValidationStatus.Passed, task.Validation);
    }

    // 13. duplicate add ------------------------------------------------------------------
    [Fact]
    public void Duplicate_Url_Is_Detected()
    {
        _server.AddFile("dup.bin", TestBytes.Pattern(1024));
        var url = _server.UrlFor("dup.bin");
        var r1 = _fx.Engine.Add(new NewDownload { Url = url });
        var r2 = _fx.Engine.Add(new NewDownload { Url = url });
        Assert.False(r1.Value!.Duplicate);
        Assert.True(r2.Value!.Duplicate);
    }

    // 14. concurrent downloads --------------------------------------------------------
    [Fact]
    public async Task Three_Concurrent_Downloads_All_Complete()
    {
        var d1 = TestBytes.Pattern(1024 * 1024, 11);
        var d2 = TestBytes.Pattern(1024 * 1024, 22);
        var d3 = TestBytes.Pattern(1024 * 1024, 33);
        _server.AddFile("c1.bin", d1);
        _server.AddFile("c2.bin", d2);
        _server.AddFile("c3.bin", d3);

        var keys = new List<string>();
        foreach (var n in new[] { "c1.bin", "c2.bin", "c3.bin" })
        {
            var r = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor(n) });
            keys.Add(r.Value!.TaskKey);
            _fx.Engine.TryStartNext();
        }
        foreach (var k in keys)
            _sink.WaitStatePumped(k, DownloadState.Completed, _fx.Engine, 45000);
        foreach (var (k, d) in keys.Zip(new[] { d1, d2, d3 }))
            Assert.True(d.SequenceEqual(await File.ReadAllBytesAsync(_fx.Engine.Get(k)!.FinalPath)));
    }

    // 15. cancel while downloading -----------------------------------------------------
    [Fact]
    public async Task Cancel_Mid_Download_Stops_And_Deletes_Temp()
    {
        var data = TestBytes.Pattern(6 * 1024 * 1024, seed: 77);
        _server.AddFile("cancelme.bin", data);
        _server.ThrottleBytesPerSec = 700 * 1024;
        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("cancelme.bin"), MaxConnections = 4 });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        await Wait.UntilAsync(() => _fx.Engine.Get(key)!.ReceivedBytes > 256 * 1024);
        _fx.Engine.Cancel(key);
        _sink.WaitState(key, DownloadState.Cancelled, 15000);
        await Task.Delay(1500);
        var task = _fx.Engine.Get(key)!;
        Assert.False(File.Exists(task.FinalPath + ".novadl"));
        Assert.False(File.Exists(task.FinalPath));
    }

    // 16. redirect through 302 ---------------------------------------------------------
    [Fact]
    public async Task Redirect_Is_Followed_And_EffectiveUrl_Used()
    {
        var data = TestBytes.Pattern(128 * 1024);
        _server.AddFile("real.bin", data);
        _server.RedirectNextTo = _server.UrlFor("real.bin");
        _server.AddFile("redirect-me", Array.Empty<byte>());
        var key = await StartAndWait(_server.UrlFor("redirect-me"));
        var task = _fx.Engine.Get(key)!;
        Assert.EndsWith("real.bin", task.EffectiveUrl);
        Assert.True(File.Exists(task.FinalPath));
    }

    // 17. filename collision on disk ----------------------------------------------------
    [Fact]
    public async Task Colliding_Filename_Gets_Unique_Path()
    {
        var data = TestBytes.Pattern(64 * 1024);
        _server.AddFile("collide.bin", data);
        var key1 = await StartAndWait(_server.UrlFor("collide.bin"), fileName: "collide.bin");

        // pre-create the same name to force collision
        var finalPath = _fx.Engine.Get(key1)!.FinalPath;
        Assert.True(File.Exists(finalPath));
        _fx.Engine.Remove(key1, deleteFiles: false);
        // file remains on disk; add same download again
        var key2 = await StartAndWait(_server.UrlFor("collide.bin"), fileName: "collide.bin");
        var secondPath = _fx.Engine.Get(key2)!.FinalPath;
        Assert.NotEqual(finalPath, secondPath);
        Assert.True(File.Exists(secondPath));
        Assert.True(File.Exists(finalPath));
    }

    // 18. unknown content length -------------------------------------------------------
    [Fact]
    public async Task Unknown_Length_Downloads_To_Eof()
    {
        var data = TestBytes.Pattern(300 * 1024);
        _server.AddFile("unknown.bin", data);
        _server.UnknownLengthFor.Add("unknown.bin");
        var key = await StartAndWait(_server.UrlFor("unknown.bin"));
        var task = _fx.Engine.Get(key)!;
        Assert.Equal(data.Length, task.TotalBytes);
        Assert.True(data.SequenceEqual(await File.ReadAllBytesAsync(task.FinalPath)));
    }
}
