using Nova.Domain;
using Nova.Engine;
using Nova.Ipc;
using Nova.Media;
using Nova.Persistence;
using Nova.Scheduler;
using Xunit;

namespace Nova.Engine.Tests;

public class HlsParserTests
{
    [Fact]
    public void Master_Playlist_Variants_Are_Parsed_With_Relative_Resolution()
    {
        var master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1280000,RESOLUTION=640x360,CODECS="avc1.42e01e,mp4a.40.2"
            http://cdn.example/360.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=4128000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
            http://cdn.example/1080.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=64000
            audio/lo.m3u8
            """;
        var p = HlsParser.Parse(master, "https://cdn.example/master.m3u8");
        Assert.Equal(HlsKind.Master, p.Kind);
        Assert.Equal(3, p.Variants.Count);
        Assert.Equal(4128000, p.Variants[1].Bandwidth);
        Assert.Equal(1920, p.Variants[1].Width);
        Assert.Equal(1080, p.Variants[1].Height);
        Assert.Equal("http://cdn.example/1080.m3u8", p.Variants[1].Url);
        Assert.Equal("https://cdn.example/audio/lo.m3u8", p.Variants[2].Url); // relative resolved
    }

    [Fact]
    public void Media_Playlist_Segments_And_Keys_Are_Parsed()
    {
        var media = """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXTINF:5.76,
            seg-01.ts
            #EXTINF:5.76,
            seg-02.ts?token=abc
            #EXT-X-ENDLIST
            """;
        var p = HlsParser.Parse(media, "https://cdn.example/v/playlist.m3u8");
        Assert.Equal(HlsKind.Media, p.Kind);
        Assert.Equal(2, p.Media!.SegmentUrls.Count);
        Assert.Equal("https://cdn.example/v/seg-01.ts", p.Media.SegmentUrls[0]);
        Assert.EndsWith("token=abc", p.Media.SegmentUrls[1]);
        Assert.False(p.Media.Encrypted);
        Assert.True(p.Media.HasEndList); // VOD, not live
    }

    [Fact]
    public void Encrypted_Stream_Is_Flagged()
    {
        var media = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:4,\ns1.ts\n#EXT-X-ENDLIST\n";
        var p = HlsParser.Parse(media, "https://x/y.m3u8");
        Assert.True(p.Media!.Encrypted);
    }

    [Fact]
    public void Live_Stream_Has_No_Endlist()
    {
        var media = "#EXTM3U\n#EXTINF:4,\ns1.ts\n";
        var p = HlsParser.Parse(media, "https://x/y.m3u8");
        Assert.True(p.Media!.IsLive);
    }
}

public class DashParserTests
{
    [Fact]
    public void SegmentTemplate_Mpd_Video_Representations_Parsed()
    {
        var mpd = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT10S" type="static">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate timescale="1000" duration="2000" initialization="init-$RepresentationID$.mp4" media="seg-$RepresentationID$-$Number$.m4s"/>
                  <Representation id="480" bandwidth="800000" width="854" height="480" codecs="avc1.42E01E"/>
                  <Representation id="1080" bandwidth="3500000" width="1920" height="1080" codecs="avc1.640028"/>
                </AdaptationSet>
                <AdaptationSet contentType="audio">
                  <SegmentTemplate timescale="44100" duration="88200" initialization="ainit-$RepresentationID$.m4a" media="aseg-$RepresentationID$-$Number$.m4s"/>
                  <Representation id="aac64" bandwidth="64000" audioSamplingRate="44100" codecs="mp4a.40.2"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var m = DashParser.Parse(mpd, "https://cdn/v/manifest.mpd");
        Assert.Equal(2, m.Representations.Count(r => r.Type == "video"));
        Assert.Single(m.Representations.Where(r => r.Type == "audio"));

        var top = m.Representations.Where(r => r.Type == "video").OrderByDescending(r => r.Bandwidth).First();
        Assert.Equal("1080", top.Id);
        Assert.NotNull(top.InitUrl);
        Assert.Equal(5, top.SegmentUrls.Count); // 10s / (2000ms @ 1000) = 5 segments
        Assert.DoesNotContain('$', top.SegmentUrls[0]);
    }

    [Fact]
    public void Content_Protection_Flags_Drm()
    {
        var mpd = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT4S">
              <Period>
                <AdaptationSet mimeType="video/mp4">
                  <ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011"/>
                  <Representation id="v1" bandwidth="1"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var m = DashParser.Parse(mpd, "https://x.mpd");
        Assert.True(m.HasEncryption);
    }

    [Fact]
    public void Iso_Duration_Parsing()
    {
        Assert.Equal(3661.5, DashParser.ParseIsoDuration("PT1H1M1.5S"));
        Assert.Null(DashParser.ParseIsoDuration(""));
    }
}

public class MediaPipelineSelectionTests
{
    [Fact]
    public void SelectVariant_Prefers_Highest_Bandwidth_Or_Exact_Height()
    {
        var variants = new List<HlsVariant>
        {
            new() { Url = "a", Bandwidth = 500_000, Height = 360 },
            new() { Url = "b", Bandwidth = 2_000_000, Height = 720 },
            new() { Url = "c", Bandwidth = 3_500_000, Height = 1080 },
        };
        Assert.Equal("c", MediaPipeline.SelectVariant(variants, new MediaSelection())!.Url);
        Assert.Equal("b", MediaPipeline.SelectVariant(variants, new MediaSelection { PreferredHeight = 720 })!.Url);
        Assert.Equal("a", MediaPipeline.SelectVariant(variants, new MediaSelection { PreferLowest = true })!.Url);
    }
}

public class HlsDownloadTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private EngineFixture _fx = null!;
    private TestSink _sink = null!;
    private IDisposable _sub = null!;

    public async Task InitializeAsync()
    {
        _server = new TestHttpServer();
        _fx = await EngineFixture.CreateAsync(media: new MediaPipeline());
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
    public async Task Hls_Vod_Stream_Downloads_And_Assembles()
    {
        // master with 2 variants (picking highest = 720)
        var seg1 = TestBytes.Pattern(64 * 1024, 1);
        var seg2 = TestBytes.Pattern(64 * 1024, 2);
        var seg3 = TestBytes.Pattern(64 * 1024, 3);
        _server.AddFile("720/s1.ts", seg1);
        _server.AddFile("720/s2.ts", seg2);
        _server.AddFile("720/s3.ts", seg3);
        _server.AddText("720/playlist.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:4\n" +
            "#EXTINF:4.0,\ns1.ts\n#EXTINF:4.0,\ns2.ts\n#EXTINF:4.0,\ns3.ts\n#EXT-X-ENDLIST\n");
        _server.AddText("master.m3u8",
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\n360/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=2400000,RESOLUTION=1280x720\n720/playlist.m3u8\n");

        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("master.m3u8"), Kind = DownloadKind.Hls });
        Assert.True(res.Ok);
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        _sink.WaitState(key, DownloadState.Completed, 20000);

        var task = _fx.Engine.Get(key)!;
        Assert.Equal(3, task.SegmentsTotal);
        Assert.Equal(3, task.SegmentsDone);
        Assert.EndsWith(".ts", task.FileName); // playlist name not kept as final name

        var expected = seg1.Concat(seg2).Concat(seg3).ToArray();
        var got = await File.ReadAllBytesAsync(task.FinalPath);
        Assert.Equal(expected.Length, got.Length);
        Assert.True(expected.SequenceEqual(got));
    }

    [Fact]
    public async Task Encrypted_Hls_Is_Rejected()
    {
        _server.AddText("enc.m3u8",
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\"\n#EXTINF:4,\ns.ts\n#EXT-X-ENDLIST\n");
        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("enc.m3u8"), Kind = DownloadKind.Hls });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        _sink.WaitState(key, DownloadState.Failed, 15000);
        var task = _fx.Engine.Get(key)!;
        Assert.Equal("drm_protected", task.ErrorCode);
    }

    [Fact]
    public async Task Live_Hls_Is_Rejected()
    {
        _server.AddText("live.m3u8", "#EXTM3U\n#EXTINF:4,\ns.ts\n");
        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("live.m3u8"), Kind = DownloadKind.Hls });
        var key = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        _sink.WaitState(key, DownloadState.Failed, 15000);
        var task = _fx.Engine.Get(key)!;
        Assert.Equal("live_stream", task.ErrorCode);
    }
}
