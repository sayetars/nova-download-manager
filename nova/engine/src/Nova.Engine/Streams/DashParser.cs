using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nova.Engine.Streams;

/// <summary>
/// MPEG-DASH (.mpd) parser: adaptations -> representations -> segments.
/// Supports SegmentTemplate (media/timeline + $Number$/$Time$) and
/// SegmentList. Returns video and audio tracks for the quality panel;
/// Nova downloads selected tracks in parallel and remuxes with ffmpeg.
/// </summary>
public sealed class DashParser
{
    public sealed record DashManifest
    {
        public List<AdaptationSetInfo> Sets { get; init; } = new();
        public TimeSpan? TotalDuration { get; init; }
        public bool IsLive { get; init; }
    }

    public sealed record AdaptationSetInfo
    {
        public string ContentType { get; init; } = "video";
        public string? Lang { get; init; }
        public List<RepresentationInfo> Representations { get; init; } = new();
    }

    public sealed record RepresentationInfo
    {
        public string Id { get; init; } = "";
        public long Bandwidth { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public string? Codecs { get; init; }
        public string? Container { get; init; }
        public double? FrameRate { get; init; }
        public string? AudioSampleRate { get; init; }
        public List<SegmentInfo> Segments { get; } = new();
        public long EstimatedSizeBytes { get; init; }
    }

    public sealed record SegmentInfo(string Url, double DurationSec, long? RangeStart, long? RangeEnd);

    public DashManifest Parse(string xml, Uri mpdUrl)
    {
        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        var doc = XDocument.Parse(xml);
        var mpd = doc.Root ?? throw new InvalidOperationException("empty mpd");
        var isLive = (string?)mpd.Attribute("type") == "dynamic";

        TimeSpan? total = null;
        if ((string?)mpd.Attribute("mediaPresentationDuration") is { } dur)
            total = XmlDuration(dur);

        var sets = new List<AdaptationSetInfo>();
        foreach (var period in mpd.Elements(ns + "Period"))
            foreach (var aset in period.Elements(ns + "AdaptationSet"))
                sets.Add(new SetBuilder(this, aset, ns, mpdUrl, period, mpd).Build());
        return new DashManifest { Sets = sets, TotalDuration = total, IsLive = isLive };
    }

    private sealed class SetBuilder(DashParser parent, XElement aset, XNamespace ns, Uri mpdUrl, XElement period, XElement mpd)
    {
        public AdaptationSetInfo Build()
        {
            var mime = (string?)aset.Attribute("mimeType") ?? "";
            var contentType = (string?)aset.Attribute("contentType")
                ?? (mime.Contains("video") ? "video" : mime.Contains("audio") ? "audio" : mime.Contains("text") ? "text" : "video");

            var info = new AdaptationSetInfo
            {
                ContentType = contentType,
                Lang = (string?)aset.Attribute("lang")
            };

            foreach (var rep in aset.Elements(ns + "Representation"))
            {
                var mimeRep = (string?)rep.Attribute("mimeType") ?? mime;
                var r = new RepresentationInfo
                {
                    Id = (string?)rep.Attribute("id") ?? "",
                    Bandwidth = Long((string?)rep.Attribute("bandwidth")) ?? 0,
                    Width = Int((string?)rep.Attribute("width")) ?? Int((string?)aset.Attribute("width")),
                    Height = Int((string?)rep.Attribute("height")) ?? Int((string?)aset.Attribute("height")),
                    Codecs = (string?)rep.Attribute("codecs") ?? (string?)aset.Attribute("codecs"),
                    Container = mimeRep.Contains("webm") ? "webm" : "mp4",
                    FrameRate = ParseFrameRate((string?)rep.Attribute("frameRate") ?? (string?)aset.Attribute("frameRate")),
                    AudioSampleRate = (string?)rep.Attribute("audioSamplingRate")
                };
                BuildSegments(rep, r);
                info.Representations.Add(r);
            }
            return info;
        }

        private void BuildSegments(XElement rep, RepresentationInfo info)
        {
            // 1) SegmentList
            if (rep.Element(ns + "SegmentList") is { } segList)
            {
                foreach (var s in segList.Elements(ns + "SegmentURL"))
                {
                    var media = (string?)s.Attribute("media");
                    if (media != null)
                        info.Segments.Add(new SegmentInfo(new Uri(mpdUrl, media).ToString(), 0, null, null));
                }
                var init = segList.Element(ns + "Initialization");
                var initMedia = (string?)init?.Attribute("sourceURL");
                if (initMedia != null)
                    info.Segments.Insert(0, new SegmentInfo(new Uri(mpdUrl, initMedia).ToString(), 0, null, null));
                return;
            }

            // 2) SegmentTemplate
            var template = rep.Element(ns + "SegmentTemplate") ?? aset.Element(ns + "SegmentTemplate");
            if (template is { } st)
            {
                var media = (string?)st.Attribute("media") ?? "";
                var initAttr = (string?)st.Attribute("initialization");
                var timescale = Long((string?)st.Attribute("timescale")) ?? 1;
                var duration = Long((string?)st.Attribute("duration")) ?? 0;
                var startNumber = Long((string?)st.Attribute("startNumber")) ?? 1;

                var repId = (string?)rep.Attribute("id") ?? "";
                var bandwidth = Long((string?)rep.Attribute("bandwidth")) ?? 0;

                if (initAttr is { } ia)
                    info.Segments.Add(new SegmentInfo(new Uri(mpdUrl, Expand(ia, repId, bandwidth, 0, 0)).ToString(), 0, null, null));

                if (st.Element(ns + "SegmentTimeline") is { } timeline)
                {
                    var time = 0L;
                    var number = startNumber;
                    foreach (var sElem in timeline.Elements(ns + "S"))
                    {
                        var d = Long((string?)sElem.Attribute("d")) ?? 0;
                        var r = Long((string?)sElem.Attribute("r")) ?? 0;
                        for (var i = 0; i <= r; i++)
                        {
                            info.Segments.Add(new SegmentInfo(
                                new Uri(mpdUrl, Expand(media, repId, bandwidth, number, time)).ToString(),
                                (double)d / timescale, null, null));
                            time += d;
                            number++;
                        }
                    }
                }
                else if (duration > 0)
                {
                    var totalDur = XmlDuration((string?)mpd.Attribute("mediaPresentationDuration") ?? "PT0S");
                    var segCount = (long)Math.Ceiling((totalDur?.TotalSeconds ?? 0) / ((double)duration / timescale));
                    for (long n = startNumber; n < startNumber + segCount; n++)
                        info.Segments.Add(new SegmentInfo(
                            new Uri(mpdUrl, Expand(media, repId, bandwidth, n, (n - startNumber) * duration)).ToString(),
                            (double)duration / timescale, null, null));
                }
                return;
            }

            // 3) plain BaseURL single file
            if (rep.Element(ns + "BaseURL") is { } b)
                info.Segments.Add(new SegmentInfo(new Uri(mpdUrl, b.Value.Trim()).ToString(), 0, null, null));
        }

        private static string Expand(string template, string repId, long bandwidth, long number, long time)
            => template.Replace("$RepresentationID$", repId)
                       .Replace("$Bandwidth$", bandwidth.ToString())
                       .Replace("$Number$", number.ToString())
                       .Replace("$Time$", time.ToString())
                       .Replace("$$", "$");

        private static long? Long(string? s) => long.TryParse(s, out var v) ? v : null;
        private static int? Int(string? s) => int.TryParse(s, out var v) ? v : null;
    }

    internal static TimeSpan? XmlDuration(string value)
    {
        var m = Regex.Match(value, @"^PT(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?$");
        if (!m.Success) return null;
        var h = double.TryParse(m.Groups[1].Value, out var hh) ? hh : 0;
        var mi = double.TryParse(m.Groups[2].Value, out var mm) ? mm : 0;
        var s = double.TryParse(m.Groups[3].Value, out var ss) ? ss : 0;
        return TimeSpan.FromSeconds(h * 3600 + mi * 60 + s);
    }

    internal static double? ParseFrameRate(string? fr)
    {
        if (string.IsNullOrEmpty(fr)) return null;
        if (fr.Contains('/'))
        {
            var parts = fr.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out var n) && int.TryParse(parts[1], out var d) && d > 0)
                return (double)n / d;
            return null;
        }
        return double.TryParse(fr, out var v) ? v : null;
    }
}
