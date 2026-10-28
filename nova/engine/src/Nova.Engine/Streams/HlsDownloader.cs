using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nova.Engine.Streams;

/// <summary>
/// HLS (HTTP Live Streaming) downloader:
///  - parses master + media playlists (EXT-X-STREAM-INF variants)
///  - AES-128 decryption with key fetch (IV auto/segment)
///  - live playlist recording (until user stops)
///  - sequential mode: segment order preserved -> incomplete preview works
/// Segments are written to a .ts container playable by most players even
/// while downloading; ffmpeg remuxes to mp4/mkv on completion if available.
/// </summary>
public sealed partial class HlsDownloader
{
    private readonly HttpClient _http;
    private readonly string _workDir;

    public HlsDownloader(HttpClient http)
    {
        _http = http;
        _workDir = Path.Combine(Path.GetTempPath(), "nova-hls-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
    }

    public sealed record HlsMediaPlaylist
    {
        public List<Variant> Variants { get; init; } = new();
        public List<(int Index, Uri Uri, double Duration, string? IVHex, byte[]? KeyData)> Segments { get; init; } = new();
        public bool IsLive { get; init; }
        public Uri? KeyUri { get; init; }
        public Uri? MapUri { get; init; } // EXT-X-MAP init segment (fMP4)
    }

    public sealed record Variant
    {
        public long Bandwidth { get; init; }
        public string? Resolution { get; init; }
        public Uri Uri { get; init; } = null!;
        public string? Codecs { get; init; }
        public string? FrameRate { get; init; }
        public string Label => Resolution is null ? $"{Bandwidth / 1000}kbps" : Resolution;
    }

    public event Action<long, long>? Progress; // (segmentsDone, segmentsTotal)

    // ------------------------------------------------------------------
    // Playlist parsing
    // ------------------------------------------------------------------

    public async Task<HlsMediaPlaylist> ParseAsync(Uri playlistUrl, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var body = await FetchTextAsync(playlistUrl, headers, ct);
        var isMaster = body.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);

        if (isMaster)
        {
            var variants = ParseVariants(body, playlistUrl);
            if (variants.Count == 0) throw new InvalidOperationException("master playlist has no variants");
            // descend into the chosen variant (caller selects by bandwidth)
            var best = variants.OrderByDescending(v => v.Bandwidth).First();
            return await ParseAsync(best.Uri, headers, ct);
        }

        return ParseMedia(body, playlistUrl);
    }

    private HlsMediaPlaylist ParseMedia(string body, Uri baseUrl)
    {
        var segs = new List<(int, Uri, double, string?, byte[]?)>();
        var isLive = !body.Contains("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase);
        var keyUri = FindKeyUri(body, baseUrl);
        var mapUriStr = FindMapUri(body, baseUrl);
        Uri? mapUri = mapUriStr != null ? new Uri(baseUrl, mapUriStr) : null;
        var defaultIv = FindSessionIv(body);

        var lines = body.Split('\n');
        double duration = 0;
        string? pendingIv = defaultIv;
        var index = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                duration = ParseExtInf(line);
            }
            else if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, "IV=0[xX]([0-9a-fA-F]+)");
                pendingIv = m.Success ? m.Groups[1].Value : null;
                keyUri = FindKeyUri(line, baseUrl) ?? keyUri;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                segs.Add((index++, new Uri(baseUrl, line), duration, pendingIv, null));
                pendingIv = defaultIv;
            }
        }
        return new HlsMediaPlaylist { Segments = segs, IsLive = isLive, KeyUri = keyUri, MapUri = mapUri };
    }

    private static List<Variant> ParseVariants(string body, Uri baseUrl)
    {
        var list = new List<Variant>();
        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;
            var bw = Regex.Match(line, @"BANDWIDTH=(\d+)").Groups is { } g && g.Count > 1 && long.TryParse(g[1].Value, out var v) ? v : 0;
            var res = Regex.Match(line, @"RESOLUTION=([0-9]+x[0-9]+)").Groups is { } g2 && g2.Count > 1 ? g2[1].Value : null;
            var codecs = Regex.Match(line, @"CODECS=""([^""]+)""").Groups is { } g3 && g3.Count > 1 ? g3[1].Value : null;
            var fr = Regex.Match(line, @"FRAME-RATE=([0-9.]+)").Groups is { } g4 && g4.Count > 1 ? g4[1].Value : null;
            var next = lines.Skip(i + 1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (next != null)
                list.Add(new Variant { Bandwidth = bw, Resolution = res, Codecs = codecs, FrameRate = fr, Uri = new Uri(baseUrl, next) });
        }
        return list;
    }

    // ------------------------------------------------------------------
    // Download
    // ------------------------------------------------------------------

    public async Task DownloadAsync(HlsMediaPlaylist playlist, string outputTsPath,
        byte[]? keyBytes = null, Dictionary<string, string>? headers = null,
        Func<string, bool>? shouldContinue = null, CancellationToken ct = default)
    {
        byte[]? key = keyBytes ?? (playlist.KeyUri != null ? await FetchKeyAsync(playlist.KeyUri, headers, ct) : null);

        long done = 0;
        var total = playlist.Segments.Count;
        var output = new FileStream(outputTsPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

        try
        {
            await using (output)
            {
                if (playlist.MapUri is not null)
                {
                    var initSeg = await FetchBytesAsync(playlist.MapUri, headers, ct);
                    await output.WriteAsync(initSeg, ct);
                }

                foreach (var (index, uri, _, ivHex, _) in playlist.Segments)
                {
                    ct.ThrowIfCancellationRequested();
                    if (shouldContinue != null && index > 0 && index % 30 == 0 && !shouldContinue(uri.ToString()))
                        throw new InvalidOperationException("stream stopped by server");

                    var data = await FetchBytesAsync(uri, headers, ct);
                    if (key != null)
                    {
                        var iv = ivHex != null ? HexToBytes(ivHex.PadLeft(32, '0')) : SeqToIv(index);
                        data = Aes128CbcDecrypt(data, key, iv);
                    }
                    await output.WriteAsync(data, ct);
                    Progress?.Invoke(++done, total);
                }
            }
        }
        catch
        {
            // keep partial file for resume/preview — .ts is playable partial
            throw;
        }
    }

    // ------------------------------------------------------------------

    private async Task<string> FetchTextAsync(Uri url, Dictionary<string, string>? headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req, headers);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<byte[]> FetchBytesAsync(Uri url, Dictionary<string, string>? headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req, headers);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<byte[]> FetchKeyAsync(Uri keyUrl, Dictionary<string, string>? headers, CancellationToken ct)
        => await FetchBytesAsync(keyUrl, headers, ct);

    private static void ApplyHeaders(HttpRequestMessage req, Dictionary<string, string>? headers)
    {
        if (headers == null) return;
        foreach (var (k, v) in headers)
            req.Headers.TryAddWithoutValidation(k, v);
    }

    internal static byte[] Aes128CbcDecrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    internal static byte[] SeqToIv(int seq)
    {
        var iv = new byte[16];
        var be = BitConverter.GetBytes(seq);
        if (BitConverter.IsLittleEndian) Array.Reverse(be);
        Buffer.BlockCopy(be, 0, iv, 12, 4);
        return iv;
    }

    internal static byte[] HexToBytes(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (var i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    private static Uri? FindKeyUri(string body, Uri baseUrl)
    {
        var m = Regex.Match(body, @"URI=""([^""]+)""");
        return m.Success ? new Uri(baseUrl, m.Groups[1].Value) : null;
    }

    private static string? FindMapUri(string body, Uri baseUrl)
    {
        var m = Regex.Match(body, @"#EXT-X-MAP:.*URI=""([^""]+)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? FindSessionIv(string body)
    {
        var m = Regex.Match(body, @"#EXT-X-KEY:.*IV=0[xX]([0-9a-fA-F]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static double ParseExtInf(string line)
    {
        var val = line.AsSpan("#EXTINF:".Length);
        var comma = val.IndexOf(',');
        if (comma > 0) val = val[..comma];
        return double.TryParse(val, out var d) ? d : 0;
    }
}
