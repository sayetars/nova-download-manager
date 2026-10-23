using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Nova.Media;

public sealed record DashRepresentation
{
    public required string Id { get; init; }
    public string Type { get; init; } = "video"; // video | audio | text
    public long Bandwidth { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Codecs { get; init; }
    public string? AudioSamplingRate { get; init; }
    public required List<string> SegmentUrls { get; init; }
    public string? InitUrl { get; init; }
    public double? DurationSec { get; init; }
}

public sealed class DashManifest
{
    public List<DashRepresentation> Representations { get; set; } = new();
    public bool HasEncryption { get; set; }
}

/// <summary>
/// Minimal-but-real MPD parser: SegmentTemplate (duration + media), SegmentList and
/// SegmentBase/SegmentURL are supported; ContentProtection flags DRM (we refuse to touch it).
/// </summary>
public static class DashParser
{
    public static DashManifest Parse(string xml, string manifestUrl)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException ex) { throw new InvalidOperationException("MPD is not valid XML: " + ex.Message); }

        XNamespace ns = doc.Root?.Name.NamespaceName ?? "";
        var result = new DashManifest();
        if (doc.Descendants(ns + "ContentProtection").Any())
            result.HasEncryption = true;

        var segTemplate = doc.Descendants(ns + "SegmentTemplate").FirstOrDefault();
        var timescale = GetLong(segTemplate?.Attribute("timescale")) ?? 1;
        var duration = GetLong(segTemplate?.Attribute("duration"));
        var mediaTpl = segTemplate?.Attribute("media")?.Value ?? "$RepresentationID$/segment_$Number$.m4s";
        var initTpl = segTemplate?.Attribute("initialization")?.Value;

        var durationSec = ParseIsoDuration(doc.Root?.Attribute("mediaPresentationDuration")?.Value);

        foreach (var rep in doc.Descendants(ns + "Representation"))
        {
            var id = (string?)rep.Attribute("id") ?? Guid.NewGuid().ToString();
            var bandwidth = GetLong(rep.Attribute("bandwidth")) ?? 0;
            var set = rep.Parent;
            var type = "video";
            var setContentType = (string?)set?.Attribute("contentType");
            var setMimeType = (string?)set?.Attribute("mimeType");
            if (setContentType?.StartsWith("audio") == true || setMimeType?.StartsWith("audio") == true
                || rep.Attribute("audioSamplingRate") is not null)
                type = "audio";
            else if (setContentType?.StartsWith("text") == true || setMimeType?.StartsWith("text") == true)
                type = "text";

            var width = GetInt(rep.Attribute("width"));
            var height = GetInt(rep.Attribute("height"));

            var segUrls = new List<string>();
            string? initUrl = null;

            // SegmentList / SegmentURL
            var list = rep.Element(ns + "SegmentList") ?? set?.Element(ns + "SegmentList");
            if (list is not null)
            {
                var lInit = list.Element(ns + "Initialization")?.Attribute("sourceURL")?.Value;
                if (lInit is not null) initUrl = HlsParser.ResolveUrl(manifestUrl, lInit);
                foreach (var su in list.Elements(ns + "SegmentURL"))
                {
                    var m = su.Attribute("media")?.Value;
                    if (m is not null) segUrls.Add(HlsParser.ResolveUrl(manifestUrl, m));
                }
            }
            else
            {
                // SegmentTemplate (period-level, adaptation-set-level or representation-level)
                var repTpl = rep.Element(ns + "SegmentTemplate");
                var setTpl = set?.Element(ns + "SegmentTemplate");
                var tpl = repTpl ?? setTpl ?? segTemplate;
                if (tpl is not null)
                {
                    var rscale = GetLong(tpl.Attribute("timescale")) ?? timescale;
                    var rdur = GetLong(tpl.Attribute("duration")) ?? duration ?? 0;
                    var rmedia = tpl.Attribute("media")?.Value ?? mediaTpl;
                    var rinit = tpl.Attribute("initialization")?.Value ?? initTpl;
                    if (rinit is not null)
                        initUrl = HlsParser.ResolveUrl(manifestUrl, rinit
                            .Replace("$RepresentationID$", id)
                            .Replace("$Bandwidth$", bandwidth.ToString()));
                    if (rdur > 0 && durationSec is > 0)
                    {
                        var segDurSec = (double)rdur / rscale;
                        var count = (int)Math.Ceiling((double)durationSec! / segDurSec);
                        for (var i = 1; i <= count; i++)
                        {
                            var url = rmedia
                                .Replace("$RepresentationID$", id)
                                .Replace("$Bandwidth$", bandwidth.ToString())
                                .Replace("$Number$", i.ToString())
                                .Replace("$Time$()", "")
                                .Replace("$Number%06d$", i.ToString());
                            segUrls.Add(HlsParser.ResolveUrl(manifestUrl, url));
                        }
                    }
                    // SegmentTimeline (explicit times) — most common for VOD
                    var st = rep.Element(ns + "SegmentTemplate")?.Element(ns + "SegmentTimeline")
                                ?? set?.Element(ns + "SegmentTemplate")?.Element(ns + "SegmentTimeline")
                                ?? segTemplate?.Element(ns + "SegmentTimeline");
                    if (st is not null)
                    {
                        segUrls.Clear();
                        long t = 0;
                        foreach (var s in st.Elements(ns + "S"))
                        {
                            var stTime = GetLong(s.Attribute("t"));
                            if (stTime is not null) t = stTime.Value;
                            var sd = GetLong(s.Attribute("d")) ?? rdur;
                            var repeat = GetLong(s.Attribute("r")) ?? 0;
                            for (var k = 0; k <= repeat; k++)
                            {
                                var url = (rmedia ?? mediaTpl)
                                    .Replace("$RepresentationID$", id)
                                    .Replace("$Bandwidth$", bandwidth.ToString())
                                    .Replace("$Time$", t.ToString())
                                    .Replace("$Number$", (segUrls.Count + 1).ToString())
                                    .Replace("$Number%06d$", (segUrls.Count + 1).ToString());
                                segUrls.Add(HlsParser.ResolveUrl(manifestUrl, url));
                                t += sd;
                            }
                        }
                    }
                }
            }

            if (segUrls.Count > 0 || initUrl is not null)
                result.Representations.Add(new DashRepresentation
                {
                    Id = id,
                    Type = type,
                    Bandwidth = bandwidth,
                    Width = width,
                    Height = height,
                    Codecs = (string?)rep.Attribute("codecs"),
                    AudioSamplingRate = (string?)rep.Attribute("audioSamplingRate"),
                    SegmentUrls = segUrls,
                    InitUrl = initUrl,
                    DurationSec = durationSec,
                });
        }
        return result;
    }

    public static double? ParseIsoDuration(string s)
    {
        // PT#H#M#.#S  (also possible: P0DT...)
        if (string.IsNullOrEmpty(s)) return null;
        var total = 0.0;
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsDigit(s[i]) || s[i] == '.')
            {
                var start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                var num = double.Parse(s[start..i], System.Globalization.CultureInfo.InvariantCulture);
                if (i < s.Length)
                {
                    total += s[i] switch
                    {
                        'H' => num * 3600,
                        'M' => num * 60,
                        'S' => num,
                        'D' => num * 86400,
                        _ => 0,
                    };
                    i++;
                }
            }
            else i++;
        }
        return total;
    }

    private static long? GetLong(XAttribute? a) =>
        long.TryParse(a?.Value, out var v) ? v : null;

    private static int? GetInt(XAttribute? a) =>
        int.TryParse(a?.Value, out var v) ? v : null;
}
