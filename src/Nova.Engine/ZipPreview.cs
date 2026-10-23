using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Nova.Engine;

/// <summary>ZIP preview + selective extraction (IDM parity). Works on completed
/// local files AND on remote files by fetching only the central directory via
/// Range requests — so users can inspect archives before committing to the
/// full download.</summary>
public static class ZipPreview
{
    public sealed record ZipEntryInfo(string Name, long CompressedSize, long UncompressedSize, DateTimeOffset Modified);

    // ------------------------------------------------------------- local file
    public static List<ZipEntryInfo> ListLocal(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => new ZipEntryInfo(e.FullName, e.CompressedLength, e.Length, e.LastWriteTime))
            .ToList();
    }

    public static void ExtractEntries(string archivePath, IEnumerable<string> entryNames, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var zip = ZipFile.OpenRead(archivePath);
        var wanted = entryNames.ToHashSet(StringComparer.Ordinal);
        foreach (var e in zip.Entries.Where(e => wanted.Contains(e.FullName)))
        {
            var dest = Path.GetFullPath(Path.Combine(destDir, e.FullName));
            var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
            if (!dest.StartsWith(root, StringComparison.Ordinal))
                throw new UnauthorizedAccessException($"Zip entry escapes destination: {e.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
        }
    }

    // ------------------------------------------------------------- remote (Range-based central directory)
    /// <summary>
    /// Reads the ZIP central directory over HTTP using Range requests
    /// (EOCD + CD only — typically the last ~64 KiB of the archive).
    /// Falls back to null when the server does not support ranges or the
    /// archive is not a standard ZIP.
    /// </summary>
    public static async Task<List<ZipEntryInfo>?> ListRemoteAsync(HttpClient http, string url, string? referer = null, CancellationToken ct = default)
    {
        // probe: need the total size
        var probeReq = new HttpRequestMessage(HttpMethod.Get, url);
        probeReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        HttpProber.AddDefaultHeaders(probeReq, referer);
        using var probe = await http.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!probe.IsSuccessStatusCode && probe.StatusCode != System.Net.HttpStatusCode.PartialContent) return null;
        long total = -1;
        if (probe.Content.Headers.ContentRange?.HasLength == true) total = probe.Content.Headers.ContentRange.Length!.Value;
        else if (probe.Content.Headers.ContentLength is > 0) total = probe.Content.Headers.ContentLength.Value;
        if (total <= 0) return null;

        // fetch the tail
        var tailLen = (int)Math.Min(total, 1024 * 1024);   // up to 1MB tail for huge central directories
        var tail = await FetchRangeAsync(http, url, total - tailLen, null, referer, ct);
        if (tail == null) return null;

        // find EOCD (signature 0x06054b50)
        var eocd = FindEocd(tail);
        if (eocd < 0) return null;
        var cdSize = BitConverter.ToUInt32(tail, eocd + 12);
        var cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
        var entryCount = BitConverter.ToUInt16(tail, eocd + 10);

        // ZIP64? (cdOffset == 0xFFFFFFFF) → not supported remotely
        if (cdOffset == 0xFFFFFFFF) return null;

        byte[] cd;
        var cdStartInTail = tail.Length - (int)(total - (cdOffset + cdSize)) - (int)cdSize;
        if (cdStartInTail >= 0 && cdStartInTail + (int)cdSize <= tail.Length)
            cd = tail[cdStartInTail..(cdStartInTail + (int)cdSize)];
        else
        {
            cd = await FetchRangeAsync(http, url, cdOffset, cdOffset + cdSize - 1, referer, ct);
            if (cd == null) return null;
        }
        return ParseCentralDirectory(cd, entryCount);
    }

    public static int FindEocd(byte[] tail)
    {
        for (var i = tail.Length - 22; i >= 0; i--)
            if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                return i;
        return -1;
    }

    public static List<ZipEntryInfo> ParseCentralDirectory(byte[] cd, int count)
    {
        var list = new List<ZipEntryInfo>();
        var off = 0;
        for (var i = 0; i < count && off + 46 <= cd.Length; i++)
        {
            if (cd[off] != 0x50 || cd[off + 1] != 0x4b || cd[off + 2] != 0x01 || cd[off + 3] != 0x02) break;
            var method = BitConverter.ToUInt16(cd, off + 10);
            var mtime = BitConverter.ToUInt32(cd, off + 12);
            var compSize = BitConverter.ToUInt32(cd, off + 20);
            var uncompSize = BitConverter.ToUInt32(cd, off + 24);
            var nameLen = BitConverter.ToUInt16(cd, off + 28);
            var extraLen = BitConverter.ToUInt16(cd, off + 30);
            var commentLen = BitConverter.ToUInt16(cd, off + 32);
            var name = System.Text.Encoding.UTF8.GetString(cd, off + 46, nameLen);
            if (name.Length > 0 && !name.EndsWith('/'))
                list.Add(new ZipEntryInfo(name, method == 0 ? uncompSize : compSize, uncompSize, DosDateTime(msDosDate: (ushort)(mtime >> 16), msDosTime: (ushort)(mtime & 0xFFFF))));
            off += 46 + nameLen + extraLen + commentLen;
        }
        return list;
    }

    internal static DateTimeOffset DosDateTime(ushort msDosDate, ushort msDosTime)
    {
        try
        {
            var year = 1980 + ((msDosDate >> 9) & 0x7F);
            var month = (msDosDate >> 5) & 0xF;
            var day = msDosDate & 0x1F;
            var hour = (msDosTime >> 11) & 0x1F;
            var minute = (msDosTime >> 5) & 0x3F;
            var second = (msDosTime & 0x1F) * 2;
            if (month is < 1 or > 12 || day is < 1 or > 31) return DateTimeOffset.UnixEpoch;
            return new DateTimeOffset(year, month, day, Math.Min(hour, 23), Math.Min(minute, 59), Math.Min(second, 59), TimeSpan.Zero);
        }
        catch { return DateTimeOffset.UnixEpoch; }
    }

    private static async Task<byte[]?> FetchRangeAsync(HttpClient http, string url, long from, long? to, string? referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = to is { } t
            ? new System.Net.Http.Headers.RangeHeaderValue(from, t)
            : new System.Net.Http.Headers.RangeHeaderValue(from, null);
        HttpProber.AddDefaultHeaders(req, referer);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}

/// <summary>
/// Antivirus integration (IDM parity). Default hooks Windows Defender
/// (MpCmdRun.exe); a custom command with {file} / {dir} placeholders can be
/// configured via settings av.enabled / av.command.
/// </summary>
public sealed class Antivirus
{
    public bool Enabled { get; }
    public string? Command { get; }
    public int TimeoutSeconds { get; } = 300;

    public Antivirus(bool enabled, string? command, int timeoutSeconds = 300)
    {
        Enabled = enabled; Command = command; TimeoutSeconds = timeoutSeconds;
    }

    public sealed record ScanResult(bool Clean, string Details);

    private static string DefenderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Windows Defender", "MpCmdRun.exe");

    public ScanResult Scan(string file)
    {
        if (!Enabled) return new ScanResult(true, "disabled");
        string exe, args;
        if (!string.IsNullOrWhiteSpace(Command))
        {
            var expanded = Command
                .Replace("{file}", Quote(file))
                .Replace("{dir}", Quote(Path.GetDirectoryName(file) ?? "."))
                .Replace("{name}", Path.GetFileName(file));
            var parts = SplitCommandLine(expanded);
            exe = parts[0];
            args = string.Join(' ', parts.Skip(1));
        }
        else if (OperatingSystem.IsWindows() && File.Exists(DefenderPath))
        {
            exe = DefenderPath;
            args = $"-Scan -ScanType 3 -File {Quote(file)} -DisableRemediation";
        }
        else if (!OperatingSystem.IsWindows())
        {
            var clam = Which("clamscan");
            if (clam is null) return new ScanResult(true, "no scanner available (skipped)");
            exe = clam; args = $"--no-summary {Quote(file)}";
        }
        else return new ScanResult(true, "no scanner available (skipped)");

        try
        {
            var psi = new ProcessStartInfo(exe) { Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p is null) return new ScanResult(true, "scanner not started (skipped)");
            if (!p.WaitForExit(TimeSpan.FromSeconds(TimeoutSeconds)))
            {
                p.Kill(entireProcessTree: true);
                return new ScanResult(false, "Antivirus scan timed out.");
            }
            var output = (p.StandardOutput.ReadToEnd() + " " + p.StandardError.ReadToEnd()).Trim();
            return p.ExitCode switch
            {
                0 => new ScanResult(true, "clean"),
                2 => new ScanResult(false, Trim(output)),
                1 when exe.Contains("clam") => new ScanResult(false, Trim(output)),
                _ => new ScanResult(false, $"scanner exit {p.ExitCode}: {Trim(output)}"),
            };
        }
        catch (Exception ex)
        {
            return new ScanResult(false, $"Antivirus error: {ex.Message}");
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
    private static string Quote(string s) => '"' + s + '"';

    internal static string[] SplitCommandLine(string cmd)
    {
        var list = new List<string>(); var sb = new StringBuilder(); var q = false;
        foreach (var ch in cmd)
        {
            if (ch == '"') q = !q;
            else if (ch == ' ' && !q) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } }
            else sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list.ToArray();
    }

    private static string? Which(string tool)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, tool);
            try { if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }
}
