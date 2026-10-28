using System.Reflection;
using System.Security.Cryptography;
using Nova.Domain;
using Nova.Engine;
using Nova.Ipc;
using Nova.Media;
using Nova.Persistence;
using Xunit;

namespace Nova.Engine.Tests;

// ---- T-021 (v1.7.0): version integrity + AES-128 HLS -------------------------

public class SemVersionTests
{
    [Fact]
    public void Parses_Core_Triplet()
    {
        var v = SemVersion.Parse("1.7.0");
        Assert.Equal((1, 7, 0), (v.Major, v.Minor, v.Patch));
        Assert.Null(v.PreRelease);
    }

    [Fact]
    public void Parses_Two_Component_And_Prerelease()
    {
        var v = SemVersion.Parse("2.1-beta.1+build.9");
        Assert.Equal((2, 1, 0), (v.Major, v.Minor, v.Patch));
        Assert.Equal("beta.1", v.PreRelease); // build metadata stripped
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.7.0", "1.6.0", 1)]
    [InlineData("1.7.0", "1.7.1", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]  // release > pre-release (SemVer §11)
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    public void Ordering(string a, string b, int expectedSign)
    {
        var cmp = SemVersion.Parse(a).CompareTo(SemVersion.Parse(b));
        Assert.Equal(expectedSign, Math.Sign(cmp));
        Assert.Equal(expectedSign < 0, SemVersion.Parse(a) < SemVersion.Parse(b));
        Assert.Equal(expectedSign >= 0, SemVersion.Parse(a).IsAtLeast(SemVersion.Parse(b)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1.2.3.4.5")]
    [InlineData("v1.2.3")]
    public void Invalid_Inputs_Return_Null(string? bad)
    {
        Assert.Null(SemVersion.TryParse(bad));
    }
}

public class VersionIntegrityTests
{
    /// <summary>Assembly version (stamped by Directory.Build.props) must equal VersionInfo (generated from version.json).
    /// Failing here means sync_versions.py was not run after editing version.json — the exact drift v1.7.0 prevents.</summary>
    [Fact]
    public void Assembly_Version_Matches_Generated_VersionInfo()
    {
        var asm = typeof(VersionInfo).Assembly.GetName().Version!;
        var vi = SemVersion.Parse(VersionInfo.Version);
        Assert.Equal((vi.Major, vi.Minor, vi.Patch), ((SemVersion)SemVersion.Parse($"{asm.Major}.{asm.Minor}.{asm.Build}")).AsTriplet());
    }

    [Fact]
    public void Ipc_Protocol_Constant_Matches_VersionInfo()
    {
        Assert.Equal(VersionInfo.IpcProtocol, Protocol.Version);
    }

    [Fact]
    public void UserAgent_Carries_Product_Version()
    {
        Assert.Equal($"NovaDownloadManager/{VersionInfo.Version}", VersionInfo.UserAgent);
    }

    [Fact]
    public void Status_Response_Defaults_Come_From_VersionInfo()
    {
        var r = new EngineStatusResponse();
        Assert.Equal(VersionInfo.Version, r.EngineVersion);
        Assert.Equal(VersionInfo.MinEngineForExtension, r.MinEngineForExtension);
        Assert.Equal(VersionInfo.Channel, r.Channel);
    }
}

internal static class SemTestExt
{
    public static (int, int, int) AsTriplet(this SemVersion v) => (v.Major, v.Minor, v.Patch);
}

public class HlsCryptTests
{
    [Fact]
    public void Encrypt_Decrypt_Round_Trip_With_Pkcs7()
    {
        var key = new byte[16];
        Random.Shared.NextBytes(key);
        var iv = new byte[16];
        Random.Shared.NextBytes(iv);
        var plain = TestBytes.Pattern(64 * 1024 + 7, 42); // not a multiple of 16 → padding exercised

        var cipher = HlsCrypt.EncryptSegmentForTest(plain, key, iv);
        Assert.Equal(0, cipher.Length % 16);
        Assert.NotEqual(plain.AsSpan(0, plain.Length).ToArray(), cipher);

        var decrypted = HlsCrypt.DecryptSegment(cipher, key, iv);
        Assert.Equal(plain.Length, decrypted.Length); // padding fully stripped
        Assert.True(plain.AsSpan().SequenceEqual(decrypted));
    }

    [Fact]
    public void Wrong_Key_Fails_Or_Garbles()
    {
        var key = new byte[16];
        var other = new byte[16];
        other[0] = 1;
        var iv = new byte[16];
        var plain = TestBytes.Pattern(1024, 1);
        var cipher = HlsCrypt.EncryptSegmentForTest(plain, key, iv);
        var decrypted = HlsCrypt.DecryptSegment(cipher, other, iv);
        Assert.False(plain.AsSpan().SequenceEqual(decrypted));
    }

    [Fact]
    public void Bad_Ciphertext_Length_Throws()
    {
        var key = new byte[16];
        var iv = new byte[16];
        Assert.ThrowsAny<CryptographicException>(() =>
            HlsCrypt.DecryptSegment(new byte[17], key, iv));
    }

    [Fact]
    public void ParseIv_Hex_And_Fallback()
    {
        var iv = HlsCrypt.ParseIv("0x000102030405060708090A0B0C0D0E0F");
        Assert.NotNull(iv);
        Assert.Equal(15, iv![15]);
        Assert.Null(HlsCrypt.ParseIv("0x1234")); // too short → caller falls back to sequence IV
        Assert.Null(HlsCrypt.ParseIv(null));
        Assert.Null(HlsCrypt.ParseIv(""));
    }

    [Fact]
    public void IvFromSequence_Is_BigEndian_128()
    {
        var iv = HlsCrypt.IvFromSequence(1);
        Assert.Equal(new byte[15].Concat(new byte[] { (byte)1 }).ToArray(), iv);
        var ivBig = HlsCrypt.IvFromSequence(0x1122334455667788L);
        Assert.Equal(0x11, ivBig[8]); // top byte of the 64-bit value lands at byte 8
        Assert.Equal(0x88, ivBig[15]);
    }
}

public class HlsKeyParsingTests
{
    [Fact]
    public void Aes128_Key_With_IV_Is_Parsed_And_Mapped()
    {
        var media = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:5000\n" +
                    "#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys/k.bin\",IV=0x00000000000000000000000000000001\n" +
                    "#EXTINF:4,\ns1.ts\n#EXTINF:4,\ns2.ts\n#EXT-X-ENDLIST\n";
        var p = HlsParser.Parse(media, "https://cdn/v/pl.m3u8");
        var m = p.Media!;
        Assert.True(m.Encrypted);
        Assert.False(m.DrmProtected);
        Assert.Single(m.Keys);
        Assert.Equal("AES-128", m.Keys[0].Method);
        Assert.Equal("https://keys/k.bin", m.Keys[0].Uri); // absolute stays
        Assert.NotNull(m.Keys[0].Iv);
        Assert.Equal(1, m.Keys[0].Iv![15]);
        Assert.Equal(5000, m.MediaSequence);
        Assert.Equal(new[] { 0, 0 }, m.SegmentKeys); // both segments map to key 0
    }

    [Fact]
    public void Relative_Key_Uri_Is_Resolved()
    {
        var media = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"keys/k.bin\"\n#EXTINF:4,\ns.ts\n#EXT-X-ENDLIST\n";
        var m = HlsParser.Parse(media, "https://cdn/v/pl.m3u8").Media!;
        Assert.Equal("https://cdn/v/keys/k.bin", m.Keys[0].Uri);
        Assert.Null(m.Keys[0].Iv); // → sequence IV fallback at download time
    }

    [Fact]
    public void Repeated_Identical_Key_Tags_Are_Deduped()
    {
        var media = "#EXTM3U\n" +
                    "#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\"\n#EXTINF:4,\ns1.ts\n" +
                    "#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\"\n#EXTINF:4,\ns2.ts\n" +
                    "#EXT-X-ENDLIST\n";
        var m = HlsParser.Parse(media, "https://cdn/pl.m3u8").Media!;
        Assert.Single(m.Keys);
        Assert.Equal(new[] { 0, 0 }, m.SegmentKeys);
    }

    [Fact]
    public void Method_None_Resets_Key_Mapping()
    {
        var media = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\"\n#EXTINF:4,\ne1.ts\n" +
                    "#EXT-X-KEY:METHOD=NONE\n#EXTINF:4,\np1.ts\n#EXT-X-ENDLIST\n";
        var m = HlsParser.Parse(media, "https://cdn/pl.m3u8").Media!;
        Assert.True(m.Encrypted);
        Assert.Equal(0, m.SegmentKeys[0]);
        Assert.Equal(-1, m.SegmentKeys[1]);
    }

    [Theory]
    [InlineData("SAMPLE-AES", null)]
    [InlineData("AES-128", "com.widevine")]
    public void Drm_Methods_Are_Classified_As_Protected(string method, string? keyformat)
    {
        var kf = keyformat is null ? "" : $",KEYFORMAT=\"{keyformat}\"";
        var media = $"#EXTM3U\n#EXT-X-KEY:METHOD={method},URI=\"k.bin\"{kf}\n#EXTINF:4,\ns.ts\n#EXT-X-ENDLIST\n";
        var m = HlsParser.Parse(media, "https://cdn/pl.m3u8").Media!;
        Assert.True(m.Encrypted);
        Assert.True(m.DrmProtected);
    }

    [Fact]
    public void Plain_Playlist_Has_No_Keys()
    {
        var media = "#EXTM3U\n#EXTINF:4,\ns.ts\n#EXT-X-ENDLIST\n";
        var m = HlsParser.Parse(media, "https://cdn/pl.m3u8").Media!;
        Assert.False(m.Encrypted);
        Assert.False(m.DrmProtected);
        Assert.Empty(m.Keys);
        Assert.Equal(new[] { -1 }, m.SegmentKeys);
    }
}

public class EncryptedHlsE2eTests : IAsyncLifetime
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
    public async Task Aes128_With_Explicit_IV_Downloads_And_Decrypts()
    {
        var key = TestBytes.Pattern(16, 9);
        var iv = HlsCrypt.IvFromSequence(7777);
        var p1 = TestBytes.Pattern(64 * 1024 + 3, 1);
        var p2 = TestBytes.Pattern(64 * 1024 + 11, 2);
        _server.AddFile("k.bin", key);
        _server.AddFile("s1.ts", HlsCrypt.EncryptSegmentForTest(p1, key, iv));
        _server.AddFile("s2.ts", HlsCrypt.EncryptSegmentForTest(p2, key, iv));
        _server.AddText("enc.m3u8",
            "#EXTM3U\n#EXT-X-TARGETDURATION:4\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\",IV=0x" + Convert.ToHexString(iv) + "\n" +
            "#EXTINF:4,\ns1.ts\n#EXTINF:4,\ns2.ts\n#EXT-X-ENDLIST\n");

        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("enc.m3u8"), Kind = DownloadKind.Hls });
        var taskKey = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        _sink.WaitState(taskKey, DownloadState.Completed, 20000);

        var expected = p1.Concat(p2).ToArray();
        var got = await File.ReadAllBytesAsync(_fx.Engine.Get(taskKey)!.FinalPath);
        Assert.Equal(expected.Length, got.Length);
        Assert.True(expected.SequenceEqual(got));
    }

    [Fact]
    public async Task Aes128_Sequence_IV_Fallback_Uses_Media_Sequence()
    {
        var key = TestBytes.Pattern(16, 5);
        const long seq = 4242; // non-zero base: IV must be derived per-segment from it
        var p1 = TestBytes.Pattern(32 * 1024 + 5, 3);
        var p2 = TestBytes.Pattern(32 * 1024 + 13, 4);
        _server.AddFile("k.bin", key);
        _server.AddFile("e1.ts", HlsCrypt.EncryptSegmentForTest(p1, key, HlsCrypt.IvFromSequence(seq)));
        _server.AddFile("e2.ts", HlsCrypt.EncryptSegmentForTest(p2, key, HlsCrypt.IvFromSequence(seq + 1)));
        _server.AddText("seq.m3u8",
            "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:" + seq + "\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\"\n" +
            "#EXTINF:4,\ne1.ts\n#EXTINF:4,\ne2.ts\n#EXT-X-ENDLIST\n");

        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("seq.m3u8"), Kind = DownloadKind.Hls });
        var taskKey = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        _sink.WaitState(taskKey, DownloadState.Completed, 20000);

        var expected = p1.Concat(p2).ToArray();
        var got = await File.ReadAllBytesAsync(_fx.Engine.Get(taskKey)!.FinalPath);
        Assert.True(expected.SequenceEqual(got));
    }

    [Fact]
    public async Task Mixed_Plain_And_Encrypted_Playlist_Assembles_In_Order()
    {
        var key = TestBytes.Pattern(16, 21);
        var iv = HlsCrypt.IvFromSequence(1);
        var plain1 = TestBytes.Pattern(16 * 1024 + 1, 10);
        var enc = TestBytes.Pattern(16 * 1024 + 7, 11);
        var plain2 = TestBytes.Pattern(16 * 1024 + 3, 12);
        _server.AddFile("k.bin", key);
        _server.AddFile("a.ts", plain1);
        _server.AddFile("b.ts", HlsCrypt.EncryptSegmentForTest(enc, key, iv));
        _server.AddFile("c.ts", plain2);
        _server.AddText("mix.m3u8",
            "#EXTM3U\n#EXTINF:4,\na.ts\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\",IV=0x" + Convert.ToHexString(iv) + "\n" +
            "#EXTINF:4,\nb.ts\n" +
            "#EXT-X-KEY:METHOD=NONE\n#EXTINF:4,\nc.ts\n#EXT-X-ENDLIST\n");

        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("mix.m3u8"), Kind = DownloadKind.Hls });
        var taskKey = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        _sink.WaitState(taskKey, DownloadState.Completed, 20000);

        var expected = plain1.Concat(enc).Concat(plain2).ToArray();
        var got = await File.ReadAllBytesAsync(_fx.Engine.Get(taskKey)!.FinalPath);
        Assert.True(expected.SequenceEqual(got));
    }

    [Fact]
    public async Task Sample_Aes_Still_Fails_As_Drm()
    {
        _server.AddText("drm.m3u8",
            "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k.bin\"\n#EXTINF:4,\ns.ts\n#EXT-X-ENDLIST\n");
        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("drm.m3u8"), Kind = DownloadKind.Hls });
        var taskKey = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        _sink.WaitState(taskKey, DownloadState.Failed, 15000);
        Assert.Equal("drm_protected", _fx.Engine.Get(taskKey)!.ErrorCode);
    }

    [Fact]
    public async Task Bad_Key_Length_Fails_With_Clear_Error()
    {
        _server.AddFile("short.bin", new byte[8]); // not 16 bytes
        _server.AddFile("s.ts", TestBytes.Pattern(1024, 1));
        _server.AddText("badkey.m3u8",
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"short.bin\"\n#EXTINF:4,\ns.ts\n#EXT-X-ENDLIST\n");
        var res = _fx.Engine.Add(new NewDownload { Url = _server.UrlFor("badkey.m3u8"), Kind = DownloadKind.Hls });
        var taskKey = res.Value!.TaskKey;
        _fx.Engine.TryStartNext();
        _sink.WaitState(taskKey, DownloadState.Failed, 15000);
        Assert.Equal("bad_key", _fx.Engine.Get(taskKey)!.ErrorCode);
    }
}
