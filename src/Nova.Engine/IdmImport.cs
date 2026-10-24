using System.Text.RegularExpressions;

namespace Nova.Engine;

/// <summary>
/// IDM parity — best-effort import of an IDM export (.ief/.ef2 INI or a plain URL list).
/// IDM's "Export" produces INI-ish files with URLs scattered across lines; we extract
/// every absolute http(s)://ftp(s):// URL, dedupe order-preserving, and keep the rest.
/// </summary>
public static partial class IdmImport
{
    [GeneratedRegex(@"https?://[^\s""'<>\\]+|ftps?://[^\s""'<>\\]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    /// <summary>Extracts a deduplicated, order-preserving URL list from IDM export content.</summary>
    public static IReadOnlyList<string> ExtractUrls(string content)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<string>();
        foreach (var line in (content ?? "").Split('\n'))
        {
            var trimmed = line.Trim().TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            foreach (var m in UrlRegex().Matches(trimmed).Select(x => x.Value))
            {
                var url = m.TrimEnd('.', ',', ';', ')');
                if (seen.Add(url)) outList.Add(url);
            }
        }
        return outList;
    }

    /// <summary>True when the content looks like an IDM .ief/.ef2 INI export.</summary>
    public static bool LooksLikeIef(string content) =>
        (content ?? "").Contains("[", StringComparison.Ordinal)
        && ((content ?? "").Contains(".ief", StringComparison.OrdinalIgnoreCase)
            || (content ?? "").Contains("Internet Download Manager", StringComparison.OrdinalIgnoreCase)
            || (content ?? "").Contains("DownloadList", StringComparison.OrdinalIgnoreCase));
}
