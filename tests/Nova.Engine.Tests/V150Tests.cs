using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Data.Sqlite;
using Nova.Domain;
using Nova.Engine;
using Nova.Engine.Preview;
using Nova.Engine.Security;
using Nova.Engine.YtDlp;
using Nova.Persistence;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Xunit;
using PgpPublicKeyBc = Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKey;
using PgpPublicKeyRingBc = Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKeyRing;

namespace Nova.Engine.Tests;

// ============================================================================
// v1.5.0 G-track: G17 partial preview · G25 client certs · G26 metalink PGP ·
// G27 server profiles · G28 yt-dlp bridge
// ============================================================================

public sealed class V150PreviewTests
{
    private static string WriteTempFile(string name, byte[] data)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-v150-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    private static PreviewServer StartServer(string tempPath, SegmentView[] views, string? token = "tok123")
    {
        var task = new DownloadTask
        {
            TaskKey = "k1",
            FileName = Path.GetFileName(tempPath)[..^".novadl".Length],
            DestinationDir = Path.GetDirectoryName(tempPath)!,
            TotalBytes = 3000,
            Url = "http://example.com/f",
        };
        var server = new PreviewServer(k => k == "k1" ? task : null, k => views, _ => Array.Empty<DownloadSegment>(), token);
        server.Start();
        return server;
    }

    [Fact]
    public async Task Preview_GET_ClampsToContiguousPrefix()
    {
        // sparse file: 3000 bytes; downloaded [0..1000) and [2000..3000)
        var data = TestBytes.Pattern(3000);
        var temp = WriteTempFile("video.mp4.novadl", data);
        var views = new[]
        {
            new SegmentView(0, 0, 999, 1000),
            new SegmentView(1, 2000, 2999, 1000),
        };
        using var server = StartServer(temp, views);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }); // loopback: never via system proxy (T-026)

        var resp = await http.GetAsync($"http://127.0.0.1:{server.Port}/tok123/k1/video.mp4");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1000, resp.Content.Headers.ContentLength);
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(1000, body.Length);
        Assert.Equal(data[..1000], body);
        Assert.Equal("video/mp4", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes", resp.Headers.AcceptRanges.ToString());
    }

    [Fact]
    public async Task Preview_Range_ServesWithinContiguous()
    {
        var data = TestBytes.Pattern(3000);
        var temp = WriteTempFile("movie.ts.novadl", data);
        var views = new[] { new SegmentView(0, 0, 2999, 1000) };
        using var server = StartServer(temp, views);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }); // loopback: never via system proxy (T-026)

        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/tok123/k1/movie.ts");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 199);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.Equal(100, (await resp.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("bytes 100-199/1000", string.Join("", resp.Content.Headers.ContentRange?.ToString()));

        // beyond the contiguous prefix → 416
        var req2 = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/tok123/k1/movie.ts");
        req2.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1500, 1600);
        var resp2 = await http.SendAsync(req2);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, resp2.StatusCode);
    }

    [Fact]
    public async Task Preview_TokenAuth_And_UnknownTask()
    {
        var temp = WriteTempFile("a.mp4.novadl", new byte[100]);
        using var server = StartServer(temp, new[] { new SegmentView(0, 0, 99, 100) });
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }); // loopback: never via system proxy (T-026)

        var bad = await http.GetAsync($"http://127.0.0.1:{server.Port}/WRONG/k1/a.mp4");
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);

        var unknown = await http.GetAsync($"http://127.0.0.1:{server.Port}/tok123/nosuch/a.mp4");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Preview_HEAD_And_SuffixRange()
    {
        var temp = WriteTempFile("clip.mkv.novadl", TestBytes.Pattern(2000));
        var views = new[] { new SegmentView(0, 0, 1999, 700) };
        using var server = StartServer(temp, views);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }); // loopback: never via system proxy (T-026)

        var head = new HttpRequestMessage(HttpMethod.Head, $"http://127.0.0.1:{server.Port}/tok123/k1/clip.mkv");
        var h = await http.SendAsync(head);
        Assert.Equal(HttpStatusCode.OK, h.StatusCode);
        Assert.Equal(700, h.Content.Headers.ContentLength);

        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/tok123/k1/clip.mkv");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(null, 10); // last 10 of logical 700
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.Equal(10, (await resp.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public void Preview_GuessMime_VideoTypes()
    {
        Assert.Equal("video/mp4", PreviewServer.GuessMime("a.mp4"));
        Assert.Equal("video/mp2t", PreviewServer.GuessMime("stream.ts"));
        Assert.Equal("video/x-matroska", PreviewServer.GuessMime("x.mkv"));
        Assert.Equal("application/octet-stream", PreviewServer.GuessMime("z.bin"));
    }

    [Fact]
    public void Preview_ContiguousPrefix_StopsAtGaps()
    {
        var views = new[]
        {
            new SegmentView(0, 0, 499, 500),
            new SegmentView(1, 1000, 1999, 1000),
        };
        Assert.Equal(500, PreviewServer.ContiguousPrefix(views));

        var contiguous = new[]
        {
            new SegmentView(0, 0, 499, 500),
            new SegmentView(1, 500, 1499, 1000),
        };
        Assert.Equal(1500, PreviewServer.ContiguousPrefix(contiguous));
        Assert.Equal(-1, PreviewServer.ContiguousPrefix(Array.Empty<SegmentView>()));
    }
}

public sealed class V150YtDlpTests
{
    private const string SampleJson = """
    {
      "extractor_key": "Youtube", "id": "abc123", "title": "Test Video",
      "uploader": "Nova Tests", "duration": 215, "is_live": false,
      "webpage_url": "https://example.com/watch?v=abc123",
      "formats": [
        {"format_id": "sb0", "ext": "mhtml", "vcodec": "none", "acodec": "none", "protocol": "mhtml"},
        {"format_id": "140", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2",
         "protocol": "https", "filesize": 3400000, "abr": 128},
        {"format_id": "137", "ext": "mp4", "resolution": "1920x1080", "vcodec": "avc1.640028",
         "acodec": "none", "protocol": "https", "tbr": 2500, "filesize": 53000000},
        {"format_id": "18", "ext": "mp4", "resolution": "640x360", "vcodec": "avc1.42001E",
         "acodec": "mp4a.40.2", "protocol": "https", "tbr": 500, "filesize": 10000000,
         "format_note": "360p"},
        {"format_id": "94", "ext": "mp4", "resolution": "640x360", "vcodec": "avc1.42001E",
         "acodec": "mp4a.40.2", "protocol": "m3u8_native", "tbr": 800},
        {"format_id": "999", "ext": "mp4", "vcodec": "drm", "acodec": "mp4a.40.2", "protocol": "https"}
      ]
    }
    """;

    [Fact]
    public void YtDlp_ParseInfo_FiltersDrmAndStoryboards()
    {
        var info = YtDlpBridge.ParseInfo(SampleJson);

        Assert.Equal("Test Video", info.Title);
        Assert.False(info.IsLive);
        Assert.DoesNotContain(info.Formats, f => f.FormatId == "sb0");   // storyboard
        Assert.DoesNotContain(info.Formats, f => f.FormatId == "999");   // DRM
        Assert.Equal(4, info.Formats.Count);

        var progressive = info.Formats.Single(f => f.FormatId == "18");
        Assert.True(progressive.DirectDownloadable);
        Assert.Equal(10_000_000, progressive.EffectiveSize);

        var videoOnly = info.Formats.Single(f => f.FormatId == "137");
        Assert.False(videoOnly.DirectDownloadable); // video-only stream needs a merge — deliberately not offered
        Assert.Equal("1920x1080", videoOnly.Resolution);

        var hls = info.Formats.Single(f => f.FormatId == "94");
        Assert.True(hls.DirectDownloadable);      // m3u8 → engine media pipeline
        var audio = info.Formats.Single(f => f.FormatId == "140");
        Assert.True(audio.DirectDownloadable);    // audio-only progressive
    }

    [Fact]
    public void YtDlp_ParseInfo_LiveRefusedByBridge()
    {
        var live = SampleJson.Replace("\"is_live\": false", "\"is_live\": true");
        var info = YtDlpBridge.ParseInfo(live);
        Assert.True(info.IsLive); // bridge turns this into live_stream error before returning
    }

    private static async Task<string> MakeFakeBinaryAsync(string name, string shBody, string cmdBody)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-ytdlp-" + Guid.NewGuid().ToString("N"));
        return await FakeBinary.WriteAsync(dir, name, shBody, cmdBody);
    }

    [Fact]
    public async Task YtDlp_AnalyzeViaFakeBinary_ParsesAndMapsStderr()
    {
        var jsonFile = Path.Combine(Path.GetTempPath(), "nova-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(jsonFile, SampleJson);
        // POSIX: $1 --version → version; $4 = page URL → dump JSON; else stderr+exit 1.
        // Windows: %~1/%~4 equivalents (SHIFT-free positional reads are safe here —
        // the URL has no ")" and no percent).
        var bin = await MakeFakeBinaryAsync("yt-dlp", $"""
#!/bin/sh
if [ "$1" = "--version" ]; then echo "2026.01.01"; exit 0; fi
if [ "$4" = "https://example.com/watch?v=abc123" ]; then cat "{jsonFile}"; exit 0; fi
echo "Unsupported URL: $4" >&2; exit 1
""", $"""
@echo off
if "%~1"=="--version" (
  echo 2026.01.01
  exit /b 0
)
if "%~4"=="https://example.com/watch?v=abc123" (
  type "{jsonFile}"
  exit /b 0
)
echo Unsupported URL: %~4 1>&2
exit /b 1
""");

        var bridge = new YtDlpBridge(explicitPath: bin);
        Assert.Equal(bin, bridge.BinaryPath());
        Assert.Equal("2026.01.01", bridge.Version());

        var info = await bridge.AnalyzeAsync("https://example.com/watch?v=abc123", CancellationToken.None);
        Assert.Equal("abc123", info.Id);
        Assert.Equal(4, info.Formats.Count);

        var ex = await Assert.ThrowsAsync<YtDlpException>(
            () => bridge.AnalyzeAsync("https://nowhere.invalid/x", CancellationToken.None));
        Assert.Equal("ytdlp_analyze_failed", ex.Code);
    }

    [Fact]
    public async Task YtDlp_ResolveViaFakeBinary_ReturnsUrls()
    {
        var bin = await MakeFakeBinaryAsync("yt-dlp", """
#!/bin/sh
if [ "$1" = "-f" ] && [ "$2" = "18" ]; then
  echo "https://cdn.example.com/direct/video.mp4?sig=1"
  echo "https://cdn.example.com/direct/audio.m4a?sig=1"
  exit 0
fi
exit 1
""", """
@echo off
if "%~1"=="-f" (
  if "%~2"=="18" (
    echo https://cdn.example.com/direct/video.mp4?sig=1
    echo https://cdn.example.com/direct/audio.m4a?sig=1
    exit /b 0
  )
)
exit /b 1
""");
        var bridge = new YtDlpBridge(explicitPath: bin);
        var urls = await bridge.ResolveAsync("https://example.com/watch?v=abc123", "18", CancellationToken.None);
        Assert.Equal(2, urls.Count);
        Assert.StartsWith("https://cdn.example.com/", urls[0]);
    }

    [Fact]
    public async Task YtDlp_MissingBinary_StructuredError()
    {
        var bridge = new YtDlpBridge(explicitPath: "/nonexistent/nova-ytdlp-test");
        Assert.Null(bridge.BinaryPath());
        var ex = await Assert.ThrowsAsync<YtDlpException>(
            () => bridge.AnalyzeAsync("https://example.com/x", CancellationToken.None));
        Assert.Equal("ytdlp_missing", ex.Code);
    }
}

[Xunit.Collection("TorrentSerial")]
public sealed class V150ServerProfileTests
{
    private NovaDatabase MakeDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-prof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new NovaDatabase(Path.Combine(dir, "nova.db"));
    }

    [Fact]
    public async Task Profiles_EwmaAndScore()
    {
        await using var db = MakeDb();
        var repo = new ServerProfileRepository(db);
        using var svc = new ServerProfileService(repo);

        Assert.Equal(0, svc.Score("http://unknown.example/file"));

        svc.RecordSpeed("http://fast.example/file", 2_000_000, 1000); // 2 MB/s
        svc.RecordSpeed("http://fast.example/file2", 2_000_000, 1000);
        svc.RecordTtfb("http://fast.example/x", 50);
        svc.RecordResult("http://fast.example/x", ok: true);

        svc.RecordSpeed("http://slow.example/f", 100_000, 1000);     // 100 KB/s
        svc.RecordResult("http://slow.example/x", ok: true);

        var scoreFast = svc.Score("http://fast.example/anything");
        var scoreSlow = svc.Score("http://slow.example/anything");
        Assert.True(scoreFast > 0);
        Assert.True(scoreFast > scoreSlow);

        var snap = svc.Snapshot();
        var fast = snap.Single(p => p.Host == "fast.example");
        Assert.Equal(3, fast.OkCount); // 2 speed samples + 1 explicit result
        Assert.True(fast.EwmaBps >= 1_500_000);
    }

    [Fact]
    public async Task Profiles_PersistRoundtrip()
    {
        await using var db = MakeDb();
        var repo = new ServerProfileRepository(db);
        using (var svc = new ServerProfileService(repo))
        {
            svc.RecordSpeed("http://cdn.example/f", 3_000_000, 1000);
            svc.RecordTtfb("http://cdn.example/f", 120);
            svc.RecordResult("http://cdn.example/f", ok: true);
            svc.RecordResult("http://cdn.example/f", ok: false);
            svc.Flush();
        }
        using (var reopened = new ServerProfileService(repo))
        {
            var p = reopened.Snapshot().Single();
            Assert.Equal("cdn.example", p.Host);
            Assert.Equal(2, p.OkCount); // 1 speed sample + 1 result
            Assert.Equal(1, p.FailCount);
            Assert.True(p.EwmaBps > 0);
            Assert.True(p.EwmaTtfbMs > 0);
        }
    }

    [Fact]
    public async Task Profiles_OrderMirrors_PutsFastestFirst_Stable()
    {
        await using var db = MakeDb();
        var repo = new ServerProfileRepository(db);
        using var svc = new ServerProfileService(repo);

        svc.RecordSpeed("http://b.example/f", 5_000_000, 1000);
        svc.RecordSpeed("http://c.example/f", 1_000_000, 1000);

        var urls = new[] { "http://a.example/f", "http://b.example/f", "http://c.example/f" };
        var ordered = svc.OrderMirrors(urls);

        Assert.Equal("http://b.example/f", ordered[0]); // fastest measured first
        Assert.Equal("http://c.example/f", ordered[1]); // slower measured second
        Assert.Equal("http://a.example/f", ordered[2]);  // unknown hosts last, original order kept
    }

    [Fact]
    public async Task Profiles_EngineHook_RecordsDuringDownload()
    {
        using var server = new TestHttpServer();
        var data = TestBytes.Pattern(2 * 1024 * 1024);
        server.AddFile("big.bin", data);
        server.SupportsRanges = true;

        await using var fx = new EngineFixture();
        var repo = new ServerProfileRepository(fx.Db);
        using var svc = new ServerProfileService(repo);
        var withProfiles = new DownloadEngine(
            new DownloadTaskRepository(fx.Db), new SegmentRepository(fx.Db),
            new QueueRepository(fx.Db), new SettingsRepository(fx.Db), new EngineEventBus(),
            fx.Http, fx.DownloadsDir, 4, profiles: svc);
        var sink = new TestSink();
        withProfiles.Events.Subscribe(sink);
        await using var engine = withProfiles;

        var r = engine.Add(new NewDownload { Url = server.UrlFor("big.bin"), MaxConnections = 2 });
        engine.TryStartNext();
        sink.WaitStatePumped(r.Value!.TaskKey, DownloadState.Completed, engine);

        var host = server.BaseUri.Host;
        Assert.True(svc.Score(server.UrlFor("big.bin")) > 0, "profile score should be > 0 after a completed download");
        var snap = svc.Snapshot();
        Assert.Contains(snap, p => p.Host == host && p.BytesTotal >= 2 * 1024 * 1024);
    }
}

public sealed class V150ClientCertTests
{
    private NovaDatabase MakeDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new NovaDatabase(Path.Combine(dir, "nova.db"));
    }

    [Fact]
    public void ClientCert_HostMatches_WildcardAndExact()
    {
        Assert.True(ClientCertRepository.HostMatches("download.example.com", "*.example.com"));
        Assert.True(ClientCertRepository.HostMatches("download.example.com", "download.example.com"));
        Assert.False(ClientCertRepository.HostMatches("download.example.com", "*.other.com"));
        Assert.False(ClientCertRepository.HostMatches("download.example.com", "example.com"));
        Assert.False(ClientCertRepository.HostMatches("example.com", "*.example.com")); // needs a label before the dot
    }

    /// <summary>Local TLS server that REQUIRES a client certificate with the expected thumbprint.</summary>
    private sealed class MutualTlsServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _expectedThumbprint;
        public int Port { get; }
        public string? ReceivedClientThumbprint { get; private set; }

        public MutualTlsServer(X509Certificate2 serverCert, string expectedThumbprint)
        {
            _expectedThumbprint = expectedThumbprint;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = serverCert,
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        {
                            if (cert is X509Certificate2 c2)
                                ReceivedClientThumbprint = c2.Thumbprint;
                            return cert is X509Certificate2 c3 &&
                                   string.Equals(c3.Thumbprint, _expectedThumbprint, StringComparison.OrdinalIgnoreCase);
                        },
                    });
                    var buf = new byte[2048];
                    var n = await ssl.ReadAsync(buf);   // consume the request (headers at least)
                    var head = $"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nX-Read: {n}\r\nConnection: close\r\n\r\n";
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(head + "ok"));
                }
                catch { /* client without cert aborts the handshake — expected in one test */ }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private static (X509Certificate2 Server, X509Certificate2 Client, X509Certificate2 ClientPublicOnly)
        MakeCerts(string clientCn)
    {
        var caReq = new CertificateRequest("CN=Nova Test CA", RSA.Create(2048), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));

        // server cert (with private key — SslStream needs it to authenticate)
        using var serverRsa = RSA.Create(2048);
        var serverReq = new CertificateRequest("CN=127.0.0.1", serverRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        // T-026: EKUs are mandatory on Windows/SChannel (Linux OpenSSL is lenient) —
        // a TLS handshake with an EKU-less cert dies with "unexpected EOF".
        serverReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        using var serverCertNoKey = serverReq.Create(caCert, caCert.NotBefore, caCert.NotAfter, Guid.NewGuid().ToByteArray());
        var serverCert = serverCertNoKey.CopyWithPrivateKey(serverRsa);

        // client cert
        using var clientRsa = RSA.Create(2048);
        var clientReq = new CertificateRequest($"CN={clientCn}", clientRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        clientReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false)); // clientAuth (T-026)
        var clientCert = clientReq.Create(caCert, caCert.NotBefore, caCert.NotAfter, Guid.NewGuid().ToByteArray());
        var clientWithKey = clientCert.CopyWithPrivateKey(clientRsa);

        return (serverCert, clientWithKey, new X509Certificate2(clientCert));
    }

    [Fact]
    public async Task ClientCert_TlsHandshake_UsesBoundCertificate()
    {
        var (serverCert, clientCert, _) = MakeCerts("nova-test-user");

        // store binding: PEM pair in temp files (cross-platform source)
        var dir = Path.Combine(Path.GetTempPath(), "nova-pem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "client.crt");
        var keyPath = Path.Combine(dir, "client.key");
        await File.WriteAllTextAsync(certPath, clientCert.ExportCertificatePem());
        await File.WriteAllTextAsync(keyPath, clientCert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

        await using var db = MakeDb();
        var repo = new ClientCertRepository(db);
        var svc = new ClientCertificateService(repo);
        var savedId = repo.Save(new ClientCertBinding
        {
            HostPattern = "127.0.0.1",
            Source = "pem",
            CertPath = certPath,
            KeyPath = keyPath,
        }, "");
        Assert.True(savedId > 0);
        var found = svc.CertificateFor("127.0.0.1");
        Assert.NotNull(found);
        Assert.Equal(clientCert.Thumbprint, found!.Thumbprint);
        Assert.Null(svc.CertificateFor("elsewhere.example"));

        // shared HttpClient wired exactly like the engine wires it
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        svc.Configure(handler);
        using var httpWired = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        // validate() reports nothing for a good PEM binding
        Assert.Null(svc.Validate(new ClientCertBinding
        { HostPattern = "127.0.0.1", Source = "pem", CertPath = certPath, KeyPath = keyPath }, ""));

        using var tls = new MutualTlsServer(serverCert, clientCert.Thumbprint);
        // tests use a self-signed server cert — trust it by thumbprint
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is X509Certificate2 c && c.Thumbprint == serverCert.Thumbprint;

        var resp = await httpWired.GetAsync($"https://127.0.0.1:{tls.Port}/");
        Assert.Equal("ok", await resp.Content.ReadAsStringAsync());
        Assert.NotNull(tls.ReceivedClientThumbprint);
        Assert.Equal(clientCert.Thumbprint, tls.ReceivedClientThumbprint);
    }

    [Fact]
    public async Task ClientCert_WithoutBinding_HandshakeFails()
    {
        var (serverCert, _, _) = MakeCerts("someone");

        await using var db = MakeDb();
        var svc = new ClientCertificateService(new ClientCertRepository(db));
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        svc.Configure(handler);
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is X509Certificate2 c && c.Thumbprint == serverCert.Thumbprint;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        using var tls = new MutualTlsServer(serverCert, "0000000000000000000000000000000000000000");
        await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync($"https://127.0.0.1:{tls.Port}/"));
    }

    [Fact]
    public async Task ClientCert_RepoLifecycle()
    {
        await using var db = MakeDb();
        var repo = new ClientCertRepository(db);
        var id = repo.Save(new ClientCertBinding
        {
            HostPattern = "*.corp.example",
            Source = "pfx",
            CertPath = "/tmp/x.pfx",
        }, "secret1");
        var second = repo.Save(new ClientCertBinding
        {
            HostPattern = "*.corp.example",
            Source = "pem",
            CertPath = "/tmp/x.crt",
        }, "secret2");
        Assert.Equal(id, second); // upsert on host_pattern

        var list = repo.List();
        var row = Assert.Single(list);
        Assert.Equal("pem", row.Source);
        Assert.Equal("secret2", repo.PassphraseFor("*.corp.example"));
        Assert.True(repo.Delete(id));
        Assert.Empty(repo.List());
    }
}

[Xunit.Collection("TorrentSerial")]
public sealed class V150PgpTests
{
    private static (string ArmoredPublicKey, string ArmoredSecretKey, string UserId) MakeKey(string userId)
    {
        var kpg = new RsaKeyPairGenerator();
        kpg.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(0x10001), new SecureRandom(), 2048, 24));
        var kp = kpg.GenerateKeyPair();
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, kp.Public, kp.Private, DateTime.UtcNow);

        var ringGen = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification, pair, userId,
            SymmetricKeyAlgorithmTag.Aes256, "".ToCharArray(), true, null, null, new SecureRandom());

        static string ToArmored(byte[] encoded)
        {
            using var ms = new MemoryStream();
            using (var arm = new ArmoredOutputStream(ms))
                arm.Write(encoded);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        return (ToArmored(ringGen.GeneratePublicKeyRing().GetEncoded()), ToArmored(ringGen.GenerateSecretKeyRing().GetEncoded()), userId);
    }

    private static string SignDetached(byte[] data, string armoredSecretRing, string userId)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(armoredSecretRing));
        using var arm = new ArmoredInputStream(ms);
        var bundle = new PgpSecretKeyRingBundle(arm);
        foreach (PgpSecretKeyRing ring in bundle.GetKeyRings())
        {
            var sec = ring.GetSecretKey(); // primary key (the only one we generate)
            if (!sec.IsSigningKey) continue;
            var priv = sec.ExtractPrivateKeyUtf8("".ToCharArray());
            var gen = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
            gen.InitSign(PgpSignature.BinaryDocument, priv);
            gen.Update(data, 0, data.Length);
            var sig = gen.Generate();
            using var outMs = new MemoryStream();
            using (var outArm = new ArmoredOutputStream(outMs))
                sig.Encode(outArm);
            return Encoding.UTF8.GetString(outMs.ToArray());
        }
        throw new InvalidOperationException("no signing key");
    }

    [Fact]
    public void Pgp_SignVerify_Roundtrip_Tamper_NoKey()
    {
        var (pub, sec, id) = MakeKey("Nova Tester <tester@nova.example>");
        var data = TestBytes.Pattern(5000);
        var sig = SignDetached(data, sec, id);

        Assert.Equal(PgpVerifyOutcome.Verified, PgpVerifier.VerifyDetached(data, sig, new[] { pub }));
        // tampered data
        var bad = (byte[])data.Clone();
        bad[10] ^= 0xFF;
        Assert.Equal(PgpVerifyOutcome.Invalid, PgpVerifier.VerifyDetached(bad, sig, new[] { pub }));
        // no keys
        Assert.Equal(PgpVerifyOutcome.NoKey, PgpVerifier.VerifyDetached(data, sig, Array.Empty<string>()));
        // garbage signature
        Assert.Equal(PgpVerifyOutcome.Malformed, PgpVerifier.VerifyDetached(data, "not a signature", new[] { pub }));
    }

    [Fact]
    public void Pgp_FingerprintAndLooksLike()
    {
        var (pub, _, _) = MakeKey("FP Test <fp@nova.example>");
        Assert.True(PgpVerifier.LooksLikePublicKey(pub));
        var fp = PgpVerifier.FingerprintOf(pub);
        Assert.NotNull(fp);
        Assert.Equal(40, fp!.Length);
        Assert.False(PgpVerifier.LooksLikePublicKey("hello world"));
        Assert.Null(PgpVerifier.FingerprintOf("hello world"));
    }

    [Fact]
    public void Pgp_NormalizeSignaturePayload_Base64Armored()
    {
        var (pub, sec, id) = MakeKey("Norm Test <n@nova.example>");
        var data = TestBytes.Pattern(1000);
        var sig = SignDetached(data, sec, id);
        Assert.StartsWith("-----BEGIN PGP SIGNATURE-----", sig);

        var asBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sig));
        var normalized = PgpVerifier.NormalizeSignaturePayload(asBase64);
        Assert.StartsWith("-----BEGIN PGP SIGNATURE-----", normalized);

        // already-armored passes through
        Assert.Equal(sig.Trim(), PgpVerifier.NormalizeSignaturePayload("  " + sig + "\n"));
    }

    [Fact]
    public async Task Pgp_KeyringService_ImportListDelete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nova-pgpkr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await using var db = new NovaDatabase(Path.Combine(dir, "nova.db"));
        var svc = new PgpKeyringService(new PgpKeyringRepository(db));

        var (pub, _, _) = MakeKey("Keyring Test <kr@nova.example>");
        Assert.Null(svc.Import("My Release Key", pub));

        var key = Assert.Single(svc.List());
        Assert.Equal(40, key.Fingerprint.Length);
        Assert.Equal("My Release Key", key.Label);
        Assert.Equal(1, svc.ArmoredKeys().Count);

        Assert.NotNull(svc.Import("bad", "this is not a key"));
        Assert.True(svc.Delete(key.Id));
        Assert.Empty(svc.List());
    }

    [Fact]
    public void Metalink_ParsesPgpSignatureElement()
    {
        var (pub, sec, id) = MakeKey("Metalink Signer <ml@nova.example>");
        var data = TestBytes.Pattern(2048);
        var sig = SignDetached(data, sec, id);

        var xml = $"""
<?xml version="1.0" encoding="UTF-8"?>
<metalink version="4.0" xmlns="urn:ietf:params:xml:ns:metalink">
 <files>
  <file name="dist.tar.gz">
   <size>2048</size>
   <verification><hash type="sha256">{Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()}</hash></verification>
   <signature type="pgp">{Convert.ToBase64String(Encoding.UTF8.GetBytes(sig))}</signature>
   <resources maxconnections="4">
    <url type="http" priority="1">http://mirror1.example/dist.tar.gz</url>
    <url type="http" priority="2">http://mirror2.example/dist.tar.gz</url>
   </resources>
  </file>
 </files>
</metalink>
""";
        var meta = Metalink.ParseXml(xml);
        Assert.NotNull(meta);
        var normalized = PgpVerifier.NormalizeSignaturePayload(meta!.SignaturePgp);
        Assert.Equal(PgpVerifyOutcome.Verified, PgpVerifier.VerifyDetached(data, normalized, new[] { pub }));
    }

    [Fact]
    public async Task Pgp_EngineE2E_WarnPolicy_VerifiedAndTamperTolerated()
    {
        var (pub, sec, id) = MakeKey("E2E Signer <e2e@nova.example>");
        var data = TestBytes.Pattern(600 * 1024);

        using var server = new TestHttpServer();
        server.SupportsRanges = true;

        var sigOk = SignDetached(data, sec, id);
        var wrongData = TestBytes.Pattern(600 * 1024, seed: 99);
        var sigWrong = SignDetached(wrongData, sec, id);

        string MetalinkFor(byte[] payload, string sig) => $"""
<?xml version="1.0"?>
<metalink version="4.0" xmlns="urn:ietf:params:xml:ns:metalink">
 <files><file name="e2e.bin">
  <size>{payload.Length}</size>
  <verification><hash type="sha256">{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}</hash></verification>
  <signature type="pgp">{Convert.ToBase64String(Encoding.UTF8.GetBytes(sig))}</signature>
  <resources><url type="http" priority="1">{server.UrlFor("payload")}</url></resources>
 </file></files>
</metalink>
""";

        server.AddFile("ok.meta4", Encoding.UTF8.GetBytes(MetalinkFor(data, sigOk)));
        server.AddFile("wrong.meta4", Encoding.UTF8.GetBytes(MetalinkFor(data, sigWrong)));
        server.AddFile("payload", data);

        await using var fx = new EngineFixture();
        var settings = new SettingsRepository(fx.Db);
        var pgpKeys = new PgpKeyringService(new PgpKeyringRepository(fx.Db));
        Assert.Null(pgpKeys.Import("e2e", pub));
        settings.Set(PgpKeyringService.PolicySetting, "warn");
        var bus = new EngineEventBus();
        var sink = new TestSink();
        bus.Subscribe(sink);
        var engine = new DownloadEngine(
            new DownloadTaskRepository(fx.Db), new SegmentRepository(fx.Db),
            new QueueRepository(fx.Db), settings, bus, fx.Http, fx.DownloadsDir, 4,
            pgpKeys: pgpKeys);
        await using var e = engine;

        // valid signature → completes, pgp verified
        var r1 = engine.Add(new NewDownload { Url = server.UrlFor("ok.meta4") });
        sink.WaitStatePumped(r1.Value!.TaskKey, DownloadState.Completed, engine, 30000);
        Assert.Equal(PgpStatus.Verified, engine.Get(r1.Value.TaskKey)!.PgpStatus);

        // signature for different content → warn policy still completes, status failed
        var r2 = engine.Add(new NewDownload { Url = server.UrlFor("wrong.meta4"), FileName = "e2e2.bin" });
        sink.WaitStatePumped(r2.Value!.TaskKey, DownloadState.Completed, engine, 30000);
        Assert.Equal(PgpStatus.Failed, engine.Get(r2.Value.TaskKey)!.PgpStatus);
    }

    [Fact]
    public async Task Pgp_EngineE2E_RequirePolicy_FailsOnBadSignature()
    {
        var (pub, sec, id) = MakeKey("Req Signer <req@nova.example>");
        var data = TestBytes.Pattern(400 * 1024);
        var wrongData = TestBytes.Pattern(400 * 1024, seed: 42);
        var sigWrong = SignDetached(wrongData, sec, id);

        using var server = new TestHttpServer();
        server.SupportsRanges = true;
        server.AddFile("payload", data);
        server.AddFile("req.meta4", Encoding.UTF8.GetBytes($"""
<?xml version="1.0"?>
<metalink version="4.0" xmlns="urn:ietf:params:xml:ns:metalink">
 <files><file name="req.bin">
  <size>{data.Length}</size>
  <verification><hash type="sha256">{Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()}</hash></verification>
  <signature type="pgp">{Convert.ToBase64String(Encoding.UTF8.GetBytes(sigWrong))}</signature>
  <resources><url type="http" priority="1">{server.UrlFor("payload")}</url></resources>
 </file></files>
</metalink>
"""));

        await using var fx = new EngineFixture();
        var settings = new SettingsRepository(fx.Db);
        var pgpKeys = new PgpKeyringService(new PgpKeyringRepository(fx.Db));
        Assert.Null(pgpKeys.Import("req", pub));
        settings.Set(PgpKeyringService.PolicySetting, "require");
        var bus = new EngineEventBus();
        var sink = new TestSink();
        bus.Subscribe(sink);
        var engine = new DownloadEngine(
            new DownloadTaskRepository(fx.Db), new SegmentRepository(fx.Db),
            new QueueRepository(fx.Db), settings, bus, fx.Http, fx.DownloadsDir, 4,
            pgpKeys: pgpKeys);
        await using var e = engine;

        var r = engine.Add(new NewDownload { Url = server.UrlFor("req.meta4") });
        engine.TryStartNext();
        sink.WaitStatePumped(r.Value!.TaskKey, DownloadState.Failed, engine, 30000);
        var t = engine.Get(r.Value.TaskKey)!;
        Assert.Equal("pgp_failed", t.ErrorCode);
        Assert.Equal(PgpStatus.Failed, t.PgpStatus);
    }
}
