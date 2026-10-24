using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Nova.Domain;
using Nova.Engine;
using Nova.Persistence;
using System.Collections.Concurrent;

namespace Nova.Grabber;

/// <summary>
/// Recursive website mirror (IDM Site Grabber): crawls HTML pages up to a
/// depth limit, downloads linked resources by type, rewrites links for
/// offline browsing, and persists projects for re-runs.
/// </summary>
public sealed class GrabberService
{
    public sealed record GrabberOptions
    {
        public int MaxDepth { get; init; } = 3;
        public bool FollowLinks { get; init; } = true;
        public bool Images { get; init; } = true;
        public bool Css { get; init; } = true;
        public bool Scripts { get; init; }
        public bool Video { get; init; }
        public bool RewriteForOffline { get; init; } = true;
        public string IncludeFilter { get; init; } = "";    // wildcard, ';'-separated
        public string ExcludeFilter { get; init; } = "";
        public string SaveDirectory { get; init; } = "";
        public string? BoundaryHost { get; init; }
    }

    public sealed record GrabberProgress(int PagesDone, int PagesQueued, int FilesDone, long BytesDone, string Current);

    public event Action<GrabberProgress>? Progress;

    private readonly NovaDatabase _db;

    public GrabberService(NovaDatabase db) { _db = db; }

    // ---------------------------------------------------------------- public API
    public async Task<GrabberResult> RunAsync(HttpClient http, string rootUrl, GrabberOptions opts,
        CancellationToken ct = default)
    {
        var root = new Uri(rootUrl);
        var boundary = opts.BoundaryHost ?? root.Host;

        var pageQueue = new ConcurrentQueue<(Uri url, int depth)>();
        var visited = new ConcurrentDictionary<Uri, byte>(UriComparer.Instance);
        var resources = new ConcurrentDictionary<Uri, byte>(UriComparer.Instance);

        pageQueue.Enqueue((root, 0));
        visited.TryAdd(root, 0);

        int pagesDone = 0, filesDone = 0;
        long bytesDone = 0;
        var pagesQueued = 1;
        var result = new GrabberResult();

        while (pageQueue.TryDequeue(out var item) && !ct.IsCancellationRequested)
        {
            var (pageUrl, depth) = item;
            Interlocked.Decrement(ref pagesQueued);
            Emit(pageUrl.ToString());

            string html;
            try { html = await http.GetStringAsync(pageUrl, ct); }
            catch (Exception ex)
            {
                result.FailedPages.Add(pageUrl.ToString());
                EngineLog.Warn($"[grabber] page failed {pageUrl}: {ex.Message}");
                pagesDone++;
                continue;
            }

            var doc = new HtmlParser().ParseDocument(html);

            // ---- resources
            var found = new List<Uri>();
            Collect(doc, "img", "src", opts.Images, found, pageUrl);
            Collect(doc, "script", "src", opts.Scripts, found, pageUrl);
            CollectStylesheets(doc, opts.Css, found, pageUrl);
            Collect(doc, "video", "src", opts.Video, found, pageUrl);
            Collect(doc, "audio", "src", opts.Video, found, pageUrl);
            Collect(doc, "source", "src", opts.Video, found, pageUrl);

            foreach (var res in found.Where(r => resources.TryAdd(r, 0)))
            {
                var (ok, size) = await DownloadResourceAsync(http, res, opts, ct);
                if (ok) { filesDone++; Interlocked.Add(ref bytesDone, size); }
            }

            // ---- follow page links
            if (opts.FollowLinks && depth < opts.MaxDepth)
            {
                foreach (var a in doc.QuerySelectorAll("a[href]"))
                {
                    var href = a.GetAttribute("href");
                    if (string.IsNullOrWhiteSpace(href) ||
                        href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                        href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Uri.TryCreate(pageUrl, href, out var link) || link.Scheme is not ("http" or "https")) continue;
                    if (!PassesFilters(link, opts)) continue;

                    var clean = Clean(link);
                    if (!clean.Host.Equals(boundary, StringComparison.OrdinalIgnoreCase)) continue;
                    if (visited.TryAdd(clean, 0))
                    {
                        pageQueue.Enqueue((clean, depth + 1));
                        Interlocked.Increment(ref pagesQueued);
                    }
                }
            }

            // ---- save page (with optional offline rewrite)
            var localPagePath = MapToLocalPath(root, pageUrl, opts, isPage: true);
            Directory.CreateDirectory(Path.GetDirectoryName(localPagePath)!);
            if (opts.RewriteForOffline)
            {
                RewriteLinks(doc, root, pageUrl, localPagePath, opts);
                await File.WriteAllTextAsync(localPagePath, doc.DocumentElement.OuterHtml, ct);
            }
            else
            {
                await File.WriteAllTextAsync(localPagePath, html, ct);
            }
            result.SavedPages.Add(pageUrl.ToString());
            pagesDone++;
            Emit(pageUrl.ToString());
        }

        SaveProject(rootUrl, opts, pagesDone, filesDone, bytesDone);
        return result;

        void Emit(string current = "") => Progress?.Invoke(new GrabberProgress(pagesDone, pagesQueued, filesDone, bytesDone, current));
    }

    public sealed class GrabberResult
    {
        public List<string> SavedPages { get; } = new();
        public List<string> FailedPages { get; } = new();
        public int FilesDone;
        public long BytesDone;
    }

    /// <summary>Stored projects (UI list + scheduled re-sync).</summary>
    public List<object> ListProjects()
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, root_url, max_depth, include_filter, exclude_filter, options, save_dir, last_run_at FROM grabber_project ORDER BY id";
            using var r = cmd.ExecuteReader();
            var list = new List<object>();
            while (r.Read())
                list.Add(new
                {
                    id = r.GetInt64(0), root_url = r.GetString(1), max_depth = r.GetInt32(2),
                    include = r.GetString(3), exclude = r.GetString(4), options = r.GetString(5),
                    save_dir = r.GetString(6), last_run_at = r.IsDBNull(7) ? null : r.GetString(7),
                });
            return list;
        }
    }

    public void DeleteProject(long id)
    {
        _db.InTransaction<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM grabber_project WHERE id=@id";
            cmd.Parameters.AddValue("@id", id);
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    private void SaveProject(string rootUrl, GrabberOptions opts, int pages, int files, long bytes)
    {
        _db.InTransaction<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO grabber_project(root_url, max_depth, include_filter, exclude_filter, options, save_dir, last_run_at)
                VALUES(@u,@d,@i,@e,@o,@s,@t)
                ON CONFLICT(root_url) DO UPDATE SET
                    max_depth=@d, include_filter=@i, exclude_filter=@e, options=@o, save_dir=@s, last_run_at=@t
                """;
            cmd.Parameters.AddValue("@u", rootUrl);
            cmd.Parameters.AddValue("@d", opts.MaxDepth);
            cmd.Parameters.AddValue("@i", opts.IncludeFilter);
            cmd.Parameters.AddValue("@e", opts.ExcludeFilter);
            cmd.Parameters.AddValue("@o", System.Text.Json.JsonSerializer.Serialize(new
            {
                opts.FollowLinks, opts.Images, opts.Css, opts.Scripts, opts.Video, opts.RewriteForOffline,
            }));
            cmd.Parameters.AddValue("@s", opts.SaveDirectory);
            cmd.Parameters.AddValue("@t", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    // ---------------------------------------------------------------- internals
    private static void Collect(IDocument doc, string selector, string attr, bool enabled, List<Uri> found, Uri pageUrl)
    {
        if (!enabled) return;
        foreach (var el in doc.QuerySelectorAll(selector))
        {
            var v = el.GetAttribute(attr);
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (Uri.TryCreate(pageUrl, v, out var u) && u.Scheme is "http" or "https")
                found.Add(u);
        }
    }

    private static void CollectStylesheets(IDocument doc, bool grabCss, List<Uri> found, Uri pageUrl)
    {
        if (!grabCss) return;
        foreach (var el in doc.QuerySelectorAll("link[rel='stylesheet'], link[rel~='stylesheet']"))
        {
            var v = el.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (Uri.TryCreate(pageUrl, v, out var u) && u.Scheme is "http" or "https")
                found.Add(u);
        }
    }

    private async Task<(bool ok, long bytes)> DownloadResourceAsync(HttpClient http, Uri res, GrabberOptions opts, CancellationToken ct)
    {
        try
        {
            var local = MapToLocalPath(res, res, opts, isPage: false);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            using var resp = await http.GetAsync(res, ct);
            if (!resp.IsSuccessStatusCode) return (false, 0);
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await src.CopyToAsync(dst, ct);
            return (true, dst.Length);
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"[grabber] resource failed {res}: {ex.Message}");
            return (false, 0);
        }
    }

    /// <summary>Map URL → local path preserving directory structure; path traversal blocked.</summary>
    internal static string MapToLocalPath(Uri root, Uri url, GrabberOptions opts, bool isPage)
    {
        string rel = Uri.UnescapeDataString(url.AbsolutePath.TrimStart('/'));
        var query = url.Query;
        if (string.IsNullOrEmpty(rel) || rel.EndsWith("/")) rel += isPage ? "index.html" : "file";
        if (isPage && !Path.HasExtension(rel)) rel += "/index.html";
        rel = string.Join("_", rel.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (!string.IsNullOrEmpty(query))
        {
            // resource with query string: fold it into the name to avoid collisions
            var safeQ = string.Join("_", query.TrimStart('?').Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            rel = Path.Combine(Path.GetDirectoryName(rel) ?? "", Path.GetFileNameWithoutExtension(rel) + "_" + safeQ + Path.GetExtension(rel));
        }
        var dir = string.IsNullOrEmpty(opts.SaveDirectory) ? Environment.CurrentDirectory : opts.SaveDirectory;
        var baseDir = Path.GetFullPath(Path.Combine(dir, SafeDir(root.Host)));
        var full = Path.GetFullPath(Path.Combine(baseDir, rel));
        if (!full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Grabber path escapes destination: {url}");
        return full;
    }

    private static string SafeDir(string s) => string.Join("_", s.Split(Path.GetInvalidPathChars())).Replace("..", "__");

    private static void RewriteLinks(IDocument doc, Uri root, Uri pageUrl, string localPagePath, GrabberOptions opts)
    {
        foreach (var el in doc.QuerySelectorAll("[src],[href]"))
        {
            foreach (var attr in new[] { "src", "href" })
            {
                var v = el.GetAttribute(attr);
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (!Uri.TryCreate(pageUrl, v, out var abs) || abs.Scheme is not ("http" or "https")) continue;
                var localRes = MapToLocalPath(root, abs, opts, isPage: false);
                var rel = Path.GetRelativePath(Path.GetDirectoryName(localPagePath)!, localRes).Replace('\\', '/');
                el.SetAttribute(attr, rel);
            }
        }
    }

    internal static bool PassesFilters(Uri u, GrabberOptions opts)
    {
        var s = u.ToString();
        if (!string.IsNullOrEmpty(opts.IncludeFilter) && !WildcardListMatch(s, opts.IncludeFilter)) return false;
        if (!string.IsNullOrEmpty(opts.ExcludeFilter) && WildcardListMatch(s, opts.ExcludeFilter)) return false;
        return true;
    }

    internal static bool WildcardListMatch(string s, string patterns)
    {
        foreach (var p in patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Nova.Engine.UrlPatterns.WildcardMatch(s, p)) return true;
        return false;
    }

    internal static Uri Clean(Uri u) => new(u.GetLeftPart(UriPartial.Path));

    private sealed class UriComparer : IEqualityComparer<Uri>
    {
        public static readonly UriComparer Instance = new();
        public bool Equals(Uri? x, Uri? y) => string.Equals(x?.ToString(), y?.ToString(), StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(Uri obj) => obj.ToString().ToLowerInvariant().GetHashCode();
    }
}
