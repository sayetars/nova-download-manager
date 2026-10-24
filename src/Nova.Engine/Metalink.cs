using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;
using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// v1.3.0 multi-source integrity: Metalink v4 (RFC 5854) + v3 + Metalink/HTTP (RFC 6249),
/// checksum sidecars (.sha256/.sha512/.md5/.sfv) and filename token templates.
/// </summary>
public static class Metalink
{
    public sealed class MirrorUrl
    {
        public string Url { get; init; } = "";
        public int Priority { get; init; } = 999999;
        public int? MaxConnections { get; init; }
        public string? Location { get; init; }
    }

    public sealed class MetalinkFile
    {
        public string FileName { get; init; } = "";
        public long Size { get; init; } = -1;
        /// <summary>Whole-file hashes by algo (lowercase hex).</summary>
        public Dictionary<string, string> Hashes { get; init; } = new();
        public PieceHashInfo? Pieces { get; init; }
        public List<MirrorUrl> Mirrors { get; init; } = new();
        /// <summary>v1.5.0 (G26): PGP signature payload (armored/base64) from &lt;signature type="pgp"&gt;.</summary>
        public string SignaturePgp { get; init; } = "";
    }

    private static readonly string[] MetalinkMimeTypes =
    {
        "application/metalink4+xml", "application/metalink+xml", "application/metalink4+xml;charset=utf-8",
    };

    public static bool LooksLikeMetalink(string url, string? contentType) =>
        (contentType is not null && MetalinkMimeTypes.Any(m => m.StartsWith(contentType.Split(';')[0].Trim(), StringComparison.OrdinalIgnoreCase)))
        || url.Split('?')[0].EndsWith(".meta4", StringComparison.OrdinalIgnoreCase)
        || url.Split('?')[0].EndsWith(".metalink", StringComparison.OrdinalIgnoreCase);

    // ---- Metalink/XML (v4 + v3) ---------------------------------------------

    public static MetalinkFile? ParseXml(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return null; }
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "metalink") return null;

        var files = new List<MetalinkFile>();
        var filesEl = root.Elements().Where(e => e.Name.LocalName == "files");
        foreach (var filesContainer in filesEl)
        {
            foreach (var fileEl in filesContainer.Elements().Where(e => e.Name.LocalName == "file"))
            {
                var name = (string?)fileEl.Attribute("name") ?? "";
                long size = -1;
                var hashes = new Dictionary<string, string>();
                PieceHashInfo? pieces = null;
                var mirrors = new List<MirrorUrl>();
                var signature = "";

                foreach (var el in fileEl.Elements())
                {
                    switch (el.Name.LocalName)
                    {
                        case "size":
                            if (long.TryParse(el.Value.Trim(), out var s)) size = s;
                            break;
                        case "verification":
                            foreach (var h in el.Elements().Where(e => e.Name.LocalName == "hash"))
                            {
                                var type = ((string?)h.Attribute("type") ?? "").ToLowerInvariant();
                                var val = h.Value.Trim().ToLowerInvariant();
                                if (Hashing.IsSupported(type) && val.Length > 0 && !hashes.ContainsKey(type))
                                    hashes[type] = val;
                            }
                            foreach (var p in el.Elements().Where(e => e.Name.LocalName == "pieces"))
                            {
                                var plen = long.TryParse((string?)p.Attribute("length"), out var pv) ? pv : 0;
                                var ptype = ((string?)p.Attribute("type") ?? "").ToLowerInvariant();
                                var phashes = p.Elements().Where(e => e.Name.LocalName == "hash")
                                    .Select(h => h.Value.Trim().ToLowerInvariant())
                                    .Where(v => v.Length > 0).ToList();
                                if (plen > 0 && Hashing.IsSupported(ptype) && phashes.Count > 0)
                                    pieces ??= new PieceHashInfo { Algo = ptype, PieceSize = plen, Hashes = phashes };
                            }
                            break;
                        case "signature":
                        {
                            // RFC 5854: <signature type="pgp">…</signature> (armored or base64)
                            var sigType = ((string?)el.Attribute("type") ?? "").ToLowerInvariant();
                            var sigValue = el.Value.Trim();
                            if (sigType == "pgp" && sigValue.Length > 0 && sigValue.Length < 1024 * 1024)
                                signature = sigValue;
                            break;
                        }
                        case "resources":
                            foreach (var u in el.Elements().Where(e => e.Name.LocalName == "url"))
                            {
                                var url = u.Value.Trim();
                                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                                if (uri.Scheme is not ("http" or "https" or "ftp" or "ftps")) continue;
                                var pri = int.TryParse((string?)u.Attribute("priority"), out var pv2) ? pv2 : 999999;
                                var maxc = int.TryParse((string?)u.Attribute("maxconnections"), out var mv) ? mv : (int?)null;
                                mirrors.Add(new MirrorUrl { Url = url, Priority = pri, MaxConnections = maxc, Location = (string?)u.Attribute("location") });
                            }
                            break;
                    }
                }

                if (name.Length > 0)
                    files.Add(new MetalinkFile { FileName = name, Size = size, Hashes = hashes, Pieces = pieces, Mirrors = mirrors, SignaturePgp = signature });
            }
        }

        // pick the first file (Nova downloads one file per task; multi-file metalinks add one task per file upstream)
        var file = files.FirstOrDefault();
        if (file is null || file.Mirrors.Count == 0) return null;
        return file;
    }

    // ---- Metalink/HTTP (RFC 6249) --------------------------------------------

    /// <summary>
    /// Extracts mirrors (Link: …; rel=duplicate) and the Digest header (RFC 3230).
    /// Returns null when the server offers no Metalink/HTTP metadata.
    /// </summary>
    public static (List<MirrorUrl> Mirrors, Dictionary<string, string> Hashes)? ParseMetalinkHttp(
        IEnumerable<string> linkHeaders, string? digestHeader)
    {
        var mirrors = new List<MirrorUrl>();
        foreach (var link in linkHeaders)
        {
            foreach (var part in SplitLinkHeader(link))
            {
                if (!part.Contains("rel=duplicate", StringComparison.OrdinalIgnoreCase) &&
                    !part.Contains("rel=\"duplicate\"", StringComparison.OrdinalIgnoreCase)) continue;
                var url = ExtractLinkUri(part);
                if (url is null) continue;
                var pri = ExtractParam(part, "pri") is { } p && int.TryParse(p, out var pv) ? pv : 999999;
                var maxc = ExtractParam(part, "maxconn") is { } m && int.TryParse(m, out var mv) ? mv : (int?)null;
                mirrors.Add(new MirrorUrl { Url = url, Priority = pri, MaxConnections = maxc, Location = ExtractParam(part, "geo") });
            }
        }

        var hashes = new Dictionary<string, string>();
        foreach (var digest in digestHeader is null ? Enumerable.Empty<string>() : new[] { digestHeader })
        {
            foreach (var item in digest.Split(','))
            {
                var kv = item.Split('=', 2);
                if (kv.Length != 2) continue;
                var algo = kv[0].Trim().ToLowerInvariant().Replace("-", "");   // "SHA-256" → "sha256"
                try
                {
                    var hex = Convert.ToHexString(Convert.FromBase64String(kv[1].Trim())).ToLowerInvariant();
                    if (Hashing.IsSupported(algo) && hex.Length > 0 && !hashes.ContainsKey(algo))
                        hashes[algo] = hex;
                }
                catch { /* malformed digest item */ }
            }
        }

        return mirrors.Count > 0 || hashes.Count > 0 ? (mirrors, hashes) : null;
    }

    internal static IEnumerable<string> SplitLinkHeader(string header)
    {
        // commas can appear inside <…>; split only outside angle brackets
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < header.Length; i++)
        {
            switch (header[i])
            {
                case '<': depth++; break;
                case '>': depth = Math.Max(0, depth - 1); break;
                case ',' when depth == 0:
                    parts.Add(header[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        if (start < header.Length) parts.Add(header[start..].Trim());
        return parts.Where(p => p.Length > 0);
    }

    internal static string? ExtractLinkUri(string part)
    {
        var open = part.IndexOf('<');
        var close = part.IndexOf('>');
        if (open < 0 || close <= open) return null;
        var raw = part[(open + 1)..close].Trim();
        return Uri.TryCreate(raw, UriKind.Absolute, out var u) ? u.ToString() : null;
    }

    internal static string? ExtractParam(string part, string name)
    {
        foreach (var seg in part.Split(';'))
        {
            var kv = seg.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim().Trim('"');
        }
        return null;
    }

    // ---- sidecars -------------------------------------------------------------

    /// <summary>
    /// Tries to discover and fetch a checksum sidecar for the URL:
    /// url + ".sha256" / ".sha512" / ".md5" / ".sfv" (in that order).
    /// Returns the first matching, correctly-formed hash for the file.
    /// </summary>
    public static async Task<(string Algo, string Hex)?> FetchSidecarAsync(HttpClient http, string fileUrl,
        string fileName, CancellationToken ct)
    {
        foreach (var ext in new[] { ".sha256", ".sha512", ".md5", ".sfv" })
        {
            var sidecarUrl = fileUrl.Split('#')[0] + ext;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, sidecarUrl);
                HttpProber.AddDefaultHeaders(req, null);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (body.Length > 256 * 1024) continue; // sanity cap
                var found = ParseSidecar(body, ext, fileName);
                if (found is not null) return found;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* sidecar missing/unreachable — try next */ }
        }
        return null;
    }

    /// <summary>Parses sha256sum/md5sum-style text or SFV; matches the file name when present.</summary>
    public static (string Algo, string Hex)? ParseSidecar(string body, string ext, string fileName)
    {
        var algo = ext switch
        {
            ".sha256" => "sha256",
            ".sha512" => "sha512",   // normalized to sha256? No — sha512 unsupported → filtered below
            ".md5" => "md5",
            ".sfv" => "crc32",
            _ => "",
        };
        if (algo == "sha512") return null; // not in SupportedAlgos; honest skip
        if (algo.Length == 0) return null;

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            string hex, name;
            if (algo == "crc32")
            {
                // SFV: "filename CRC32" (CRC last token, 8 hex)
                var sp = line.LastIndexOf(' ');
                if (sp <= 0) continue;
                name = line[..sp].Trim().TrimStart('*');
                hex = line[(sp + 1)..].Trim();
            }
            else
            {
                // coreutils: "HEX [ *]filename" (or BSD tag style "SHA256 (name) = HEX")
                if (line.Contains('(', StringComparison.OrdinalIgnoreCase) && line.Contains(") =", StringComparison.OrdinalIgnoreCase))
                {
                    var open = line.IndexOf('(');
                    var close = line.IndexOf(')', open);
                    var eq = line.IndexOf("= ", close, StringComparison.Ordinal);
                    if (close < 0 || eq < 0) continue;
                    name = line[(open + 1)..close].Trim();
                    hex = line[(eq + 2)..].Trim();
                }
                else
                {
                    var sp = line.IndexOf(' ');
                    if (sp != 32 && sp != 64 && sp != 40) { /* fallthrough generic */ }
                    if (sp <= 0) continue;
                    hex = line[..sp].Trim();
                    name = line[(sp + 1)..].Trim().TrimStart('*');
                }
            }

            hex = hex.ToLowerInvariant();
            var hexOk = hex.Length == (algo switch { "sha256" => 64, "md5" => 32, "crc32" => 8, _ => -1 })
                        && hex.All(c => Uri.IsHexDigit(c));
            if (!hexOk) continue;
            if (name.Length > 0 && !name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
            return (algo, hex);
        }
        return null;
    }
}

/// <summary>
/// Filename token templates (aria2-style parameterization, v1.3.0):
/// %date% %time% %index% %domain% %category% %title% %ext% — used by batch add + categories.
/// </summary>
public static class FilenameTokens
{
    public sealed class Context
    {
        public int Index { get; init; } = 1;
        public string Url { get; init; } = "";
        public string Category { get; init; } = "other";
        public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
        public string? FileName { get; init; }
    }

    public static bool HasTokens(string template) =>
        !string.IsNullOrEmpty(template) && template.Contains('%') &&
        new[] { "%date%", "%time%", "%index%", "%domain%", "%category%", "%title%", "%ext%" }
            .Any(t => template.Contains(t, StringComparison.OrdinalIgnoreCase));

    public static string Expand(string template, Context ctx)
    {
        if (!HasTokens(template)) return template;
        var host = Uri.TryCreate(ctx.Url, UriKind.Absolute, out var u) ? u.Host : "";
        var baseName = ctx.FileName ?? FilenamePolicy.FromUrl(ctx.Url) ?? "download";
        var ext = Path.GetExtension(baseName);
        var title = Path.GetFileNameWithoutExtension(baseName);

        var sb = new System.Text.StringBuilder(ReplaceTokensNoCase(template, new Dictionary<string, string>
        {
            ["%ext%"] = ext.StartsWith('.') ? ext[1..] : ext,
            ["%title%"] = title,
            ["%category%"] = ctx.Category,
            ["%domain%"] = host,
            ["%index%"] = ctx.Index.ToString("D3"),
            ["%date%"] = ctx.Now.ToString("yyyy-MM-dd"),
            ["%time%"] = ctx.Now.ToString("HHmmss"),
        }));
        return sb.ToString();
    }

    private static string ReplaceTokensNoCase(string input, Dictionary<string, string> tokens)
    {
        foreach (var (token, value) in tokens)
        {
            int idx;
            while ((idx = input.IndexOf(token, StringComparison.OrdinalIgnoreCase)) >= 0)
                input = input[..idx] + value + input[(idx + token.Length)..];
        }
        return input;
    }
}

/// <summary>
/// Netscape cookies.txt parsing (v1.3.0, aria2 parity): imports browser cookie files
/// into per-host "Cookie:" request headers. Uses only #HttpOnly_ prefixed + classic
/// 7-field lines; never persists values anywhere but the user's task rows.
/// </summary>
public static class CookieFile
{
    /// <summary>Returns host → cookie header ("k=v; k2=v2") for every domain in the file.</summary>
    public static Dictionary<string, string> ParseNetscape(string content)
    {
        var jar = new Dictionary<string, List<(string Name, string Value)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || !line.Contains('\t')) continue;
            if (line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                line = line["#HttpOnly_".Length..];
            else if (line.StartsWith('#')) continue;

            var f = line.Split('\t');
            if (f.Length < 7) continue;
            var domain = f[0].Trim();
            var name = f[5].Trim();
            var value = f[6].Trim();
            if (domain.Length == 0 || name.Length == 0) continue;
            var host = domain.StartsWith('.') ? domain[1..] : domain;
            if (!jar.TryGetValue(host, out var list))
                jar[host] = list = new List<(string, string)>();
            list.Add((name, value));
        }
        return jar.ToDictionary(kv => kv.Key, kv => string.Join("; ", kv.Value.Select(c => $"{c.Name}={c.Value}")),
            StringComparer.OrdinalIgnoreCase);
    }
}
