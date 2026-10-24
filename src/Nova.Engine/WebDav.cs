using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// WebDAV support (v1.3.0): dav:// → http://, davs:// → https:// scheme rewrite for
/// downloads (ranged GETs work unchanged through the normal HTTP path) plus a PROPFIND
/// directory listing for the UI browser (IPC dav_list).
/// </summary>
public static class WebDav
{
    public sealed record DavEntry(string Name, bool IsDirectory, long Size, DateTimeOffset Modified);

    public static string Rewrite(string url)
    {
        if (url.StartsWith("davs://", StringComparison.OrdinalIgnoreCase)) return "https://" + url[7..];
        if (url.StartsWith("dav://", StringComparison.OrdinalIgnoreCase)) return "http://" + url[6..];
        return url;
    }

    /// <summary>Depth-1 PROPFIND listing. Throws NovaHttpException on auth/protocol failures.</summary>
    public static async Task<List<DavEntry>> ListAsync(HttpClient http, string url,
        TaskCredentials? creds, CancellationToken ct)
    {
        var httpUrl = Rewrite(url);
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), httpUrl);
        req.Headers.Add("Depth", "1");
        req.Content = new StringContent(
            """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:displayname/><d:getcontentlength/><d:getlastmodified/><d:resourcetype/></d:prop></d:propfind>""",
            Encoding.UTF8, "application/xml");
        if (creds is not null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.UserName}:{creds.Password}")));

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new NovaHttpException("http_401", ErrorCategory.Auth, "WebDAV authentication failed.", url);
        if (!resp.IsSuccessStatusCode)
            throw NovaHttpException.FromStatus(resp.StatusCode, url);

        var xml = await resp.Content.ReadAsStringAsync(ct);
        return ParsePropfind(xml, httpUrl);
    }

    public static List<DavEntry> ParsePropfind(string xml, string requestUrl)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return new List<DavEntry>(); }
        var self = new Uri(requestUrl).AbsolutePath.TrimEnd('/');

        var list = new List<DavEntry>();
        foreach (var respEl in doc.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            var href = respEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "href")?.Value.Trim() ?? "";
            if (href.Length == 0) continue;
            var path = Uri.UnescapeDataString(href);
            if (path.TrimEnd('/') == self) continue;   // the requested folder itself

            var prop = respEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "prop");
            var isDir = prop?.Descendants().Any(e => e.Name.LocalName == "collection") == true;
            var name = prop?.Descendants().FirstOrDefault(e => e.Name.LocalName == "displayname")?.Value?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                var decoded = Uri.UnescapeDataString(new Uri(href.StartsWith("http") ? href : "http://x" + href).AbsolutePath);
                name = decoded.TrimEnd('/').Split('/').LastOrDefault() ?? decoded;
            }
            long size = -1;
            var sizeText = prop?.Descendants().FirstOrDefault(e => e.Name.LocalName == "getcontentlength")?.Value;
            if (long.TryParse(sizeText, out var s)) size = s;
            var modified = DateTimeOffset.MinValue;
            var lm = prop?.Descendants().FirstOrDefault(e => e.Name.LocalName == "getlastmodified")?.Value;
            if (lm is not null && DateTimeOffset.TryParse(lm, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m))
                modified = m;

            list.Add(new DavEntry(name ?? "", isDir, isDir ? -1 : size, modified));
        }
        return list;
    }
}
