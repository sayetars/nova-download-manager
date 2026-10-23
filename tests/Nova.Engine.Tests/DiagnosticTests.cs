using Nova.Domain;
using Nova.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Nova.Engine.Tests;

public class DiagnosticTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private TestHttpServer _server = null!;
    private EngineFixture _fx = null!;
    private TestSink _sink = null!;
    private IDisposable _sub = null!;

    public DiagnosticTests(ITestOutputHelper o) => _out = o;

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
    public async Task Trace_Hls_Flow()
    {
        var server = new TestHttpServer();
        var fx = await EngineFixture.CreateAsync(media: new Nova.Media.MediaPipeline());
        var sink = new TestSink();
        using var sub = fx.Bus.Subscribe(sink);

        var seg1 = TestBytes.Pattern(64 * 1024, 1);
        var seg2 = TestBytes.Pattern(64 * 1024, 2);
        server.AddFile("720/s1.ts", seg1);
        server.AddFile("720/s2.ts", seg2);
        var media = string.Join("\n",
            "#EXTM3U",
            "#EXT-X-TARGETDURATION:4",
            "#EXTINF:4.0,",
            "s1.ts",
            "#EXTINF:4.0,",
            "s2.ts",
            "#EXT-X-ENDLIST");
        var master = string.Join("\n",
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360",
            "360/index.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=2400000,RESOLUTION=1280x720",
            "720/playlist.m3u8");
        server.AddText("720/playlist.m3u8", media);
        server.AddText("master.m3u8", master);

        var res = fx.Engine.Add(new NewDownload { Url = server.UrlFor("master.m3u8"), Kind = DownloadKind.Hls });
        var key = res.Value!.TaskKey;
        fx.Engine.TryStartNext();

        for (var i = 0; i < 15; i++)
        {
            await Task.Delay(1000);
            var t = fx.Engine.Get(key);
            _out.WriteLine($"[{i}s] state={t?.State} seg={t?.SegmentsDone}/{t?.SegmentsTotal} err={t?.ErrorCode}:{t?.ErrorMessage} runner={fx.Engine.IsRunning(key)}");
            if (t?.State is DownloadState.Completed or DownloadState.Failed) break;
            fx.Engine.TryStartNext();
        }
        var last = fx.Engine.Get(key)!;
        _out.WriteLine($"FINAL: {last.State} {last.ErrorCode} {last.ErrorMessage}");
        Assert.Equal(DownloadState.Completed, last.State);
        await fx.DisposeAsync();
        server.Dispose();
    }
}
