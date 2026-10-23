using System.Text.RegularExpressions;

namespace Nova.Engine;

/// <summary>Batch URL pattern expansion (IDM parity): https://site/file[01-99].jpg, [1-100:5] steps.</summary>
public static class UrlPatterns
{
    /// <summary>Expand [start-end] and [start-end:step] numeric ranges (padding preserved). No pattern → single item.</summary>
    public static List<string> Expand(string pattern, int maxItems = 10_000)
    {
        var m = Regex.Match(pattern, @"\[(\d+)-(\d+)(?::(\d+))?\]");
        if (!m.Success) return [pattern];

        var start = long.Parse(m.Groups[1].Value);
        var end = long.Parse(m.Groups[2].Value);
        var pad = m.Groups[1].Value.Length;
        var step = m.Groups[3].Success ? Math.Max(1, long.Parse(m.Groups[3].Value)) : 1;

        var prefix = pattern[..m.Index];
        var suffix = pattern[(m.Index + m.Length)..];
        var list = new List<string>();
        if (start <= end)
            for (var v = start; v <= end && list.Count < maxItems; v += step)
                list.Add(prefix + v.ToString("D" + pad) + suffix);
        else
            for (var v = start; v >= end && list.Count < maxItems; v -= step)
                list.Add(prefix + v.ToString("D" + pad) + suffix);
        return list;
    }

    /// <summary>Split free text (paste of many URLs) into candidate URLs.</summary>
    public static List<string> ExtractUrls(string text) =>
        text.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Case-insensitive wildcard match with * and ? (dp, linear space).</summary>
    public static bool WildcardMatch(string s, string pattern)
    {
        var dp = new bool[s.Length + 1, pattern.Length + 1];
        dp[0, 0] = true;
        for (var j = 1; j <= pattern.Length; j++) if (pattern[j - 1] == '*') dp[0, j] = dp[0, j - 1];
        for (var i = 1; i <= s.Length; i++)
            for (var j = 1; j <= pattern.Length; j++)
            {
                var pc = char.ToLowerInvariant(pattern[j - 1]);
                var sc = char.ToLowerInvariant(s[i - 1]);
                if (pc == '*') dp[i, j] = dp[i - 1, j] || dp[i, j - 1];
                else if (pc == '?' || pc == sc) dp[i, j] = dp[i - 1, j - 1];
            }
        return dp[s.Length, pattern.Length];
    }
}
