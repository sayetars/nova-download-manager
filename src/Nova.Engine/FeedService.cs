using System.Globalization;
using System.Xml.Linq;
using Nova.Domain;
using Nova.Persistence;

namespace Nova.Engine;

/// <summary>
/// v1.3.0: RSS/Atom feed subscriptions (JDownloader/FDM parity) — polls feeds,
/// turns new enclosures/links matching the filters into downloads. Seen-item
/// state persists in SQLite so restarts never re-download old entries.
/// </summary>
public sealed class FeedService : IDisposable
{
    private readonly DownloadEngine _engine;
    private readonly IFeedRuleRepository _repo;
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly System.Timers.Timer _timer;
    private int _busy;

    public FeedService(DownloadEngine engine, IFeedRuleRepository repo, HttpClient http, IClock? clock = null)
    {
        _engine = engine;
        _repo = repo;
        _http = http;
        _clock = clock ?? SystemClock.Instance;
        _timer = new System.Timers.Timer(30_000) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = CheckDueAsync();
        _timer.Start();
    }

    public async Task CheckDueAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            foreach (var rule in _repo.GetAll())
            {
                if (!rule.Enabled || rule.IntervalMinutes <= 0) continue;
                if (_clock.UnixMs < rule.LastCheckMs + (long)rule.IntervalMinutes * 60_000) continue;
                try { await RunRuleAsync(rule.Id); }
                catch (Exception ex) { EngineLog.Warn($"feed rule {rule.Id} failed: {ex.Message}"); }
            }
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Fetches one feed and adds every new matching enclosure/link as a download.</summary>
    public async Task<FeedRunResult> RunRuleAsync(long ruleId)
    {
        var rule = _repo.Get(ruleId) ?? throw new InvalidOperationException($"feed rule {ruleId} not found");
        using var resp = await _http.GetAsync(rule.Url, HttpCompletionOption.ResponseContentRead);
        resp.EnsureSuccessStatusCode();
        var xml = await resp.Content.ReadAsStringAsync();
        var items = ParseFeed(xml);

        var seen = _repo.GetSeen(ruleId);
        var filters = rule.Filters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var added = 0;
        var seenCount = 0;

        foreach (var item in items)
        {
            _repo.MarkSeen(ruleId, item.ItemId, _clock.UnixMs);
            if (seen.Contains(item.ItemId)) { seenCount++; continue; }
            if (item.Url.Length == 0) { seenCount++; continue; }
            if (filters.Length > 0 && !filters.Any(f =>
                    item.Title.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                    item.Url.Contains(f, StringComparison.OrdinalIgnoreCase)))
            { seenCount++; continue; }
            if (rule.MaxItems > 0 && added >= rule.MaxItems) break;

            var fileName = FilenamePolicy.SanitizeFileName(
                FilenameTokens.Expand("%title%.%ext%",
                    new FilenameTokens.Context { Url = item.Url, FileName = MakeName(item) }));
            var result = _engine.Add(new NewDownload
            {
                Url = item.Url,
                DestinationDir = rule.DestinationDir.Length > 0 ? rule.DestinationDir : null,
                FileName = fileName,
                Category = rule.Category.Length > 0 ? rule.Category : "other",
                Referer = rule.Url,           // feed origin as referer (helps some hosts)
                AutoStart = rule.AutoStart,
            });
            if (result.Ok) added++;
        }

        _repo.SetLastCheck(ruleId, _clock.UnixMs);
        EngineLog.Info($"feed '{rule.Name}': {items.Count} items, {added} added, {seen.Count} previously seen");
        return new FeedRunResult(ruleId, items.Count, added, seenCount, 0);
    }

    private static string MakeName(FeedItem item)
    {
        // enclosure URLs usually end with a real filename; derive %ext% from it, title for %title%
        var fromUrl = FilenamePolicy.FromUrl(item.Url);
        return string.IsNullOrWhiteSpace(fromUrl) ? item.Title : fromUrl;
    }

    /// <summary>Parses RSS 2.0 and Atom documents into normalized items (enclosure/link preferred).</summary>
    public static IReadOnlyList<FeedItem> ParseFeed(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return Array.Empty<FeedItem>(); }
        var root = doc.Root;
        if (root is null) return Array.Empty<FeedItem>();

        var list = new List<FeedItem>();
        var isRss = root.Name.LocalName == "rss";
        var items = isRss
            ? root.Descendants().Where(e => e.Name.LocalName == "item")
            : root.Descendants().Where(e => e.Name.LocalName == "entry");

        foreach (var el in items)
        {
            var title = Child(el, "title")?.Value.Trim() ?? "";
            var guid = Child(el, "guid")?.Value.Trim()
                       ?? Child(el, "id")?.Value.Trim() ?? "";
            var pub = ParseDate(Child(el, "pubDate")?.Value ?? Child(el, "updated")?.Value ?? Child(el, "published")?.Value);

            string url = "";
            var enclosure = el.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure");
            if (enclosure is not null)
            {
                var encUrl = (string?)enclosure.Attribute("url");
                if (Uri.TryCreate(encUrl, UriKind.Absolute, out var eu) &&
                    eu.Scheme is "http" or "https" or "ftp" or "ftps")
                    url = eu.ToString();
            }
            if (url.Length == 0)
            {
                foreach (var link in el.Elements().Where(e => e.Name.LocalName == "link"))
                {
                    var href = (string?)link.Attribute("href");
                    var text = href is not null ? href : link.Value.Trim();
                    if (Uri.TryCreate(text, UriKind.Absolute, out var lu) &&
                        lu.Scheme is "http" or "https" or "ftp" or "ftps")
                    {
                        url = lu.ToString();
                        break;
                    }
                }
            }

            var itemId = guid.Length > 0 ? guid : (url.Length > 0 ? url : title);
            if (itemId.Length == 0) continue;
            list.Add(new FeedItem(itemId, title, url, pub));
        }
        return list;
    }

    /// <summary>Namespace-safe child lookup (RSS 2.0 has no xmlns; Atom lives in its own).</summary>
    private static XElement? Child(XElement el, string localName) =>
        el.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static long ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        // RFC 822 ("Mon, 06 Sep 2021 16:00:00 GMT") and ISO-8601 both occur in the wild
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToUnixTimeMilliseconds();
        return 0;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
