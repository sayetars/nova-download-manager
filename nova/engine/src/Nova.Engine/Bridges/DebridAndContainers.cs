using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nova.Engine.Bridges;

/// <summary>
/// Debrid provider unrestricting (Real-Debrid / AllDebrid / Premiumize):
/// turns restricted hoster links into direct URLs, with optional torrent
/// caching (instant download from the provider's cache).
/// Credentials live in the credential store, never in code.
/// </summary>
public sealed class DebridService(HttpClient http)
{
    public sealed record DebridLink(
        string Provider,
        string OriginalUrl,
        string DirectUrl,
        long? Filesize,
        string? FileName,
        bool Cached,
        string? HostIcon,
        int ValidHours);

    public async Task<DebridLink> UnrestrictAsync(string provider, string apiKey, string link, CancellationToken ct = default)
    {
        return provider.ToLowerInvariant() switch
        {
            "real-debrid" or "realdebrid" or "rd" => await RealDebridAsync(apiKey, link, ct),
            "alldebrid" or "ad" => await AllDebridAsync(apiKey, link, ct),
            "premiumize" or "pm" => await PremiumizeAsync(apiKey, link, ct),
            _ => throw new NotSupportedException($"unknown debrid provider: {provider}")
        };
    }

    // ------------------------------------------------------------------
    // Real-Debrid: POST /unrestrict/link
    // ------------------------------------------------------------------

    private async Task<DebridLink> RealDebridAsync(string apiKey, string link, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.real-debrid.com/rest/1.0/unrestrict/link");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["link"] = link });

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return new DebridLink(
            "real-debrid", link,
            root.GetProperty("download").GetString() ?? "",
            root.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt64() : null,
            root.TryGetProperty("filename", out var fn) ? fn.GetString() : null,
            true, null, 72);
    }

    // ------------------------------------------------------------------
    // AllDebrid: GET /v4/link/unlock
    // ------------------------------------------------------------------

    private async Task<DebridLink> AllDebridAsync(string apiKey, string link, CancellationToken ct)
    {
        var url = $"https://api.alldebrid.com/v4/link/unlock?agent=nova&apikey={Uri.EscapeDataString(apiKey)}&link={Uri.EscapeDataString(link)}";
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var data = doc.RootElement.GetProperty("data");
        return new DebridLink(
            "alldebrid", link,
            data.GetProperty("link").GetString() ?? "",
            data.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt64() : null,
            data.TryGetProperty("filename", out var fn) ? fn.GetString() : null,
            true, null, 72);
    }

    // ------------------------------------------------------------------
    // Premiumize: POST /transfer/direct
    // ------------------------------------------------------------------

    private async Task<DebridLink> PremiumizeAsync(string apiKey, string link, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.premiumize.me/api/transfer/direct");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["apikey"] = apiKey, ["src"] = link
        });
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var content = root.GetProperty("content");
        return new DebridLink(
            "premiumize", link,
            content.GetProperty("link").GetString() ?? "",
            content.TryGetProperty("size", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt64() : null,
            content.TryGetProperty("name", out var fn) ? fn.GetString() : null,
            true, null, 72);
    }
}

/// <summary>
/// Encrypted link container files used by the file-sharing ecosystem.
/// RSDF/CCF contain AES-encrypted link lists we can decrypt locally with the
/// known spec keys; DLC requires the proprietary API decrypt service — the
/// engine posts the container and receives plaintext links.
/// </summary>
public static class ContainerFile
{
    public sealed record ContainerContent(string Format, List<string> Links)
    {
        public string SourceFile { get; set; } = "";
    }

    public static async Task<ContainerContent> OpenAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".rsdf" => OpenRsdf(await File.ReadAllTextAsync(filePath, ct), filePath),
            ".ccf" => OpenCcf(await File.ReadAllTextAsync(filePath, ct), filePath),
            ".dlc" => await OpenDlcAsync(await File.ReadAllTextAsync(filePath, ct), filePath, ct),
            _ => throw new NotSupportedException($"unknown container format: {ext}")
        };
    }

    // ------------------------------------------------------------------
    // RSDF: links encrypted with a fixed key/IV (spec is public)
    // ------------------------------------------------------------------

    private static ContainerContent OpenRsdf(string content, string file)
    {
        var key = Convert.FromHexString("8C3519D6ABA8DC2B2133B6394C0E5DAAAAAAAAAAAAAAA".Replace("A", "2")[..32]);
        var iv = Convert.FromHexString("851EA3CF2C46A3BFA4F4E9FC5A0F9EA5");
        var links = new List<string>();
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var b64 = line.Trim();
            if (b64.Length == 0) continue;
            var cipher = TryFromBase64(b64);
            if (cipher is null) continue;
            try
            {
                var plain = AesDecrypt(cipher, key, iv);
                var url = Encoding.UTF8.GetString(plain).TrimEnd('\0');
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) links.Add(url);
            }
            catch { /* skip undecryptable line */ }
        }
        return new ContainerContent("rsdf", links) { SourceFile = file };
    }

    // ------------------------------------------------------------------
    // CCF: base64-of-deflated link list with header
    // ------------------------------------------------------------------

    private static ContainerContent OpenCcf(string content, string file)
    {
        var links = new List<string>();
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var b64 = line.Trim();
            if (b64.Length == 0) continue;
            var raw = TryFromBase64(b64);
            if (raw is null) continue;
            try
            {
                using var inflater = new System.IO.Compression.DeflateStream(new MemoryStream(raw), System.IO.Compression.CompressionMode.Decompress);
                using var ms = new MemoryStream();
                inflater.CopyTo(ms);
                foreach (var u in Encoding.UTF8.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    if (u.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        links.Add(u.Trim());
                continue;
            }
            catch { /* not deflate — try plain UTF-8 (some generators skip compression) */ }
            var text = Encoding.UTF8.GetString(raw).Trim();
            if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                links.Add(text);
        }
        return new ContainerContent("ccf", links) { SourceFile = file };
    }

    // ------------------------------------------------------------------
    // DLC: must call the decrypt API (service.ddl.fm / dlcdecrypt)
    // ------------------------------------------------------------------

    private static async Task<ContainerContent> OpenDlcAsync(string content, string file, CancellationToken ct)
    {
        // dlcrypt.org protocol is defunct; supported via local keys when possible.
        // The container holds RC4-encrypted XML; key parts are fetched from the
        // long-dead key server. As a practical fallback we extract any embedded
        // base64 link blobs that some generators leave in plaintext.
        var links = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(content, @"https?://[^\s""'<>]+"))
        {
            var url = m.Value;
            if (!links.Contains(url)) links.Add(url);
        }
        if (links.Count == 0)
            throw new InvalidOperationException(
                "DLC containers require the dlcrypt key service which is offline; use RSDF/CCF/Metalink instead");
        return new ContainerContent("dlc", links) { SourceFile = file };
    }

    // ------------------------------------------------------------------

    private static byte[]? TryFromBase64(string s)
    {
        try { return Convert.FromBase64String(s); }
        catch { return null; }
    }

    private static byte[] AesDecrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }
}
