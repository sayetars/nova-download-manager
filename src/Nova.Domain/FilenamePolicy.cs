using System.Globalization;

namespace Nova.Domain;

/// <summary>Filename &amp; path safety: sanitizes names, blocks traversal, deduplicates collisions.</summary>
public static class FilenamePolicy
{
    private static readonly char[] InvalidChars =
    {
        '<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0', '\x01', '\x02', '\x03', '\x04', '\x05',
        '\x06', '\x07', '\x08', '\x09', '\x0A', '\x0B', '\x0C', '\x0D', '\x0E', '\x0F',
        '\x10', '\x11', '\x12', '\x13', '\x14', '\x15', '\x16', '\x17', '\x18', '\x19',
        '\x1A', '\x1B', '\x1C', '\x1D', '\x1E', '\x1F'
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
    };

    /// <summary>
    /// Sanitizes a filename: strips any embedded path components (path traversal),
    /// removes invalid chars, reserves nothing, trims dots/spaces at the end (Windows rule).
    /// Unicode (incl. Persian) letters are preserved verbatim.
    /// </summary>
    public static string SanitizeFileName(string? name, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        // Take the last path component only → kills "..\..\evil.exe" traversal attempts.
        var leaf = name.Replace('\\', '/');
        leaf = leaf.Substring(leaf.LastIndexOf('/') + 1);

        var sb = new System.Text.StringBuilder(leaf.Length);
        foreach (var ch in leaf)
        {
            if (ch == '.' || char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '(' or ')' or '[' or ']'
                or '+' or ',' or '&' or '\'' or '!' or '#' or '$' or '%' or '=' or '@' or '~' or '^'
                || ch > 0x007F) // keep full Unicode incl. Persian/Arabic/CJK
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        var result = sb.ToString().Trim();
        // Windows: filename cannot end with dot or space.
        result = result.TrimEnd(' ', '.');
        // Leading dots are legal on POSIX but hidden-file semantics; strip for clarity.
        result = result.TrimStart(' ', '.');

        if (result.Length == 0) return fallback;

        if (ReservedNames.Contains(result.Split('.')[0]))
            result = "_" + result;

        if (result.Length > 180)
        {
            var ext = Path.GetExtension(result);
            var stem = result[..^ext.Length][..(180 - ext.Length)];
            result = stem + ext;
        }
        return result;
    }

    /// <summary>Guesses a filename from a Content-Disposition header (handles RFC5987 UTF-8 and quoted forms).</summary>
    public static string? FromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        // filename*=UTF-8''%D9%86%D8%A7%D9%85%D9%85%D9%86%D8%A7%D8%B3%D8%A8%20%282%29.bin
        var starIdx = header.IndexOf("filename*=", StringComparison.OrdinalIgnoreCase);
        if (starIdx >= 0)
        {
            var raw = header[(starIdx + "filename*=".Length)..].Trim('"', ' ');
            var tick = raw.IndexOf('\'');
            var tick2 = raw.IndexOf('\'', tick + 1);
            if (tick >= 0 && tick2 > tick) raw = raw[(tick2 + 1)..];
            var decoded = Uri.UnescapeDataString(raw);
            if (!string.IsNullOrWhiteSpace(decoded)) return SanitizeFileName(decoded);
        }
        var idx = header.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var raw = header[(idx + "filename=".Length)..].Trim().Trim('"').Trim();
            if (!string.IsNullOrWhiteSpace(raw)) return SanitizeFileName(raw);
        }
        return null;
    }

    /// <summary>Guesses a filename from a URL path (Unicode-safe, strips query).</summary>
    public static string? FromUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            var last = u.AbsolutePath.Split('/').LastOrDefault(s => s.Length > 0);
            if (string.IsNullOrEmpty(last)) return null;
            return SanitizeFileName(Uri.UnescapeDataString(last));
        }
        catch { return null; }
    }

    /// <summary>Returns a non-colliding path: "file (2).ext". Never overwrites existing files.</summary>
    public static string MakeUniquePath(string fullPath)
    {
        if (!File.Exists(fullPath)) return fullPath;
        var dir = Path.GetDirectoryName(fullPath)!;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}");
    }

    /// <summary>Validates that <paramref name="candidate"/> stays inside <paramref name="rootDir"/> (full paths).</summary>
    public static bool IsInsideDirectory(string rootDir, string candidate)
    {
        var root = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !full.EndsWith(Path.DirectorySeparatorChar + root.Split(Path.DirectorySeparatorChar)[^2] + Path.DirectorySeparatorChar);
    }

    /// <summary>Human-readable size. Uses the user's locale digits through the caller, not here.</summary>
    public static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v.ToString(u == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)} {units[u]}";
    }
}
