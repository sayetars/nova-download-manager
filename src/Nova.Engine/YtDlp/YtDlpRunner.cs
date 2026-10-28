using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Nova.Domain;

namespace Nova.Engine.YtDlp;

/// <summary>
/// v1.5.1: selection payload persisted on a task for NATIVE yt-dlp mode. The engine task
/// exists like any other (queued, pausable, resumable), but the bytes come from yt-dlp
/// itself — which is the only way to obtain formats that need merging (separate video+audio)
/// or per-site fragment handling. Direct-downloadable formats keep using the normal engine
/// pipeline (multi-segment, mirrors, hashing) exactly as in v1.5.0.
/// </summary>
internal static class ProcessArgExtensions
{
    /// <summary>ArgumentList is Collection&lt;string&gt; (no AddRange) — append items one by one.</summary>
    public static void AddArgs(this ProcessStartInfo psi, params string[] args)
    {
        foreach (var a in args) psi.ArgumentList.Add(a);
    }
}

public sealed class YtDlpNativeSelection
{
    public string Source { get; set; } = "ytdlp";
    public string Mode { get; set; } = "native";
    public string Page { get; set; } = "";
    /// <summary>yt-dlp format spec: "137+251" (merge) or a single id.</summary>
    public string FormatId { get; set; } = "";
    public string? Title { get; set; }
    public string? Ext { get; set; }
    /// <summary>Sum of the chosen formats' sizes when known at analyze time (0 = unknown).</summary>
    public long TotalBytes { get; set; }

    public static YtDlpNativeSelection? TryParse(string? mediaSelection)
    {
        if (string.IsNullOrWhiteSpace(mediaSelection)) return null;
        try
        {
            using var doc = JsonDocument.Parse(mediaSelection);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("source", out var s) || s.GetString() != "ytdlp") return null;
            if (!root.TryGetProperty("mode", out var m) || m.GetString() != "native") return null;
            if (!root.TryGetProperty("format_id", out var f) || string.IsNullOrEmpty(f.GetString())) return null;
            return new YtDlpNativeSelection
            {
                Page = root.TryGetProperty("page", out var p) ? p.GetString() ?? "" : "",
                FormatId = f.GetString()!,
                Title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                Ext = root.TryGetProperty("ext", out var e) ? e.GetString() : null,
                TotalBytes = root.TryGetProperty("total_bytes", out var tb) && tb.ValueKind == JsonValueKind.Number
                    ? tb.GetInt64() : 0,
            };
        }
        catch (JsonException) { return null; }
    }

    public string Encode() => JsonSerializer.Serialize(this,
        new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,   // "format_id", "total_bytes", …
        });

    /// <summary>Number of sequential download phases (one per format in a "a+b" spec).</summary>
    public int Phases => FormatId.Split('+', StringSplitOptions.TrimEntries).Length;
}

/// <summary>One parsed <c>[download]</c> progress line.</summary>
public sealed record YtDlpProgressLine
{
    public double Fraction { get; init; }             // 0..1 (within the current phase)
    public bool HasFraction { get; init; }
    public long PhaseTotalBytes { get; init; }        // "of 10.00MiB" (0 when unknown)
    public long PhaseReceivedBytes { get; init; }    // fraction*total, or raw bytes when no fraction
    public long SpeedBps { get; init; }
    public int EtaSeconds { get; init; }

    // [download]  42.3% of ~ 10.00MiB at 1.23MiB/s ETA 00:12
    // [download]   4.50MiB at 1.20MiB/s ETA 00:04          (unknown total)
    // [download] 100% of 10.00MiB in 00:05                 (phase finished)
    private static readonly Regex Rx = new(
        @"^\[download\]\s+" +
        @"(?:(?<pct>[\d.]+)%\s+of\s+(?<approx>~)?\s*(?<total>[\d.]+)(?<tunit>B|KiB|MiB|GiB|TiB)" +
        @"|\[?\s*(?<raw>[\d.]+)(?<runit>B|KiB|MiB|GiB|TiB))" +
        @"(?:\s+at\s+(?<speed>[\d.]+)(?<sunit>B|KiB|MiB|GiB)/s)?" +
        @"(?:\s+(?:ETA\s+(?<eta>[\d:]+)|in\s+(?<took>[\d:]+)))?\s*$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    public static YtDlpProgressLine? TryParse(string line)
    {
        if (!line.StartsWith("[download]", StringComparison.Ordinal)) return null;
        var m = Rx.Match(line);
        if (!m.Success) return null;

        long speed = 0;
        if (m.Groups["speed"].Success)
            speed = (long)(double.Parse(m.Groups["speed"].Value, CultureInfo.InvariantCulture)
                           * Unit(m.Groups["sunit"].Value));

        var result = new YtDlpProgressLine { SpeedBps = speed };
        if (m.Groups["eta"].Success)
            result = result with { EtaSeconds = ParseHms(m.Groups["eta"].Value) };

        if (m.Groups["pct"].Success)
        {
            var total = (long)(double.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture)
                               * Unit(m.Groups["tunit"].Value));
            var frac = double.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture) / 100.0;
            return result with
            {
                HasFraction = true,
                PhaseTotalBytes = total,
                PhaseReceivedBytes = (long)(total * frac),
                Fraction = Math.Clamp(frac, 0, 1),
            };
        }
        var raw = (long)(double.Parse(m.Groups["raw"].Value, CultureInfo.InvariantCulture)
                         * Unit(m.Groups["runit"].Value));
        return result with { PhaseReceivedBytes = raw };
    }

    public static bool IsDestination(string line) =>
        line.StartsWith("[download] Destination: ", StringComparison.Ordinal);

    public static bool IsAlreadyDownloaded(string line) =>
        line.StartsWith("[download] ", StringComparison.Ordinal) &&
        line.Contains("has already been downloaded", StringComparison.Ordinal);

    private static double Unit(string u) => u switch
    {
        "B" => 1, "KiB" => 1024, "MiB" => 1024 * 1024, "GiB" => 1024L * 1024 * 1024,
        "TiB" => 1024L * 1024 * 1024 * 1024, _ => 1,
    };

    private static int ParseHms(string s)
    {
        var parts = s.Split(':');
        var sec = 0;
        foreach (var p in parts) sec = sec * 60 + int.Parse(p, CultureInfo.InvariantCulture);
        return sec;
    }
}

/// <summary>
/// v1.5.1: runs yt-dlp natively for ONE task — spawning it, streaming <c>--newline</c>
/// progress into engine progress events, mapping exit codes to structured errors, and
/// resolving the final (possibly merged) file. Pause keeps yt-dlp's <c>.part</c> files so
/// resume continues via yt-dlp's own <c>--continue</c>. Never times out on its own — the
/// task's cancellation tokens drive process lifetime.
/// </summary>
public static class YtDlpRunner
{
    public sealed class Hooks
    {
        /// <summary>(receivedDelta, speedBps) — same shape as the torrent/ftp runners.</summary>
        public required Action<long, long> OnProgress { get; init; }
    }

    public static async Task<string> RunAsync(DownloadTask task, YtDlpNativeSelection sel,
        YtDlpBridge? bridge, Hooks hooks, CancellationToken ct,
        CancellationToken userCancel, CancellationToken hardStop)
    {
        var bin = bridge?.BinaryPath()
            ?? throw new YtDlpException("ytdlp_missing", ErrorCategory.Media,
                "yt-dlp was not found. Install it and set its path in Settings → yt-dlp.");

        var destDir = task.DestinationDir;
        Directory.CreateDirectory(destDir);
        var baseName = BuildBaseName(task, sel);

        var psi = new ProcessStartInfo
        {
            FileName = bin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(sel.FormatId);
        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--progress");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--no-mtime");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(Path.Combine(destDir, baseName + ".%(ext)s"));
        if (sel.FormatId.Contains('+'))
            psi.AddArgs("--merge-output-format", "mp4/mkv");
        if (!string.IsNullOrWhiteSpace(task.Cookies))
            psi.AddArgs("--add-headers", "Cookie: " + task.Cookies);
        if (!string.IsNullOrWhiteSpace(task.Referer))
            psi.AddArgs("--referer", task.Referer);

        using var p = Process.Start(psi)
            ?? throw new YtDlpException("ytdlp_start_failed", ErrorCategory.Media,
                "yt-dlp could not be started.");

        var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var phases = new List<(long Received, long Total)>();
        var mergerPath = (string?)null;
        var lastDestination = (string?)null;

        try
        {
            while (true)
            {
                var line = await p.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;
                line = line.TrimEnd('\r');
                if (line.Length == 0) continue;

                if (YtDlpProgressLine.IsDestination(line))
                {
                    lastDestination = line["[download] Destination: ".Length..].Trim().Trim('"');
                    if (phases.Count < sel.Phases) phases.Add((0, 0));
                    continue;
                }
                if (line.StartsWith("[Merger] Merging formats into ", StringComparison.Ordinal))
                {
                    mergerPath = line["[Merger] Merging formats into ".Length..].Trim().Trim('"');
                    continue;
                }
                if (YtDlpProgressLine.IsAlreadyDownloaded(line))
                {
                    // final file exists already — yt-dlp skips; treat as phase complete
                    if (phases.Count < sel.Phases) phases.Add((0, 0));
                    continue;
                }
                if (line.StartsWith("[download] ", StringComparison.Ordinal) &&
                    YtDlpProgressLine.TryParse(line) is { } pl)
                {
                    if (phases.Count == 0) phases.Add((0, 0));   // progress before Destination (defensive)
                    var idx = phases.Count - 1;
                    var cur = phases[idx];
                    var recv = Math.Max(cur.Received, pl.PhaseReceivedBytes);
                    var tot = pl.PhaseTotalBytes > 0 ? pl.PhaseTotalBytes : cur.Total;
                    phases[idx] = (recv, tot);
                    PublishProgress(task, phases, pl.SpeedBps, hooks);
                }
            }

            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
            {
                var stderr = await stderrTask;
                throw new YtDlpException("ytdlp_failed", ErrorCategory.Media,
                    YtDlpBridge.MapStderrForUi(stderr,
                        "yt-dlp could not download this media."));
            }

            var finalPath = ResolveFinalPath(destDir, baseName, mergerPath, lastDestination);
            var size = new FileInfo(finalPath).Length;
            task.TotalBytes = size;
            task.ReceivedBytes = size;
            return finalPath;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* process refused to die in time */ }
            if (userCancel.IsCancellationRequested)
                DeletePartFiles(destDir, baseName);   // cancelled: clean the partial artifacts
            throw;                                     // paused/hard-stop keep .part for resume
        }
    }

    private static void PublishProgress(DownloadTask task, List<(long Received, long Total)> phases,
        long speedBps, Hooks hooks)
    {
        var received = phases.Sum(x => x.Received);
        var known = phases.Sum(x => x.Total);
        if (known > 0 && task.TotalBytes < known) task.TotalBytes = known;
        var delta = received - task.ReceivedBytes;
        if (delta > 0)
        {
            task.ReceivedBytes = received;
            hooks.OnProgress(delta, speedBps);
        }
    }

    private static string BuildBaseName(DownloadTask task, YtDlpNativeSelection sel)
    {
        var name = task.FileName;
        if (string.IsNullOrWhiteSpace(name) || name == "download")
            name = !string.IsNullOrWhiteSpace(sel.Title)
                ? FilenamePolicy.SanitizeFileName(sel.Title)
                : !string.IsNullOrEmpty(sel.Page) ? FilenamePolicy.FromUrl(sel.Page) ?? "media" : "media";
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Final file: merger output &gt; last destination (sans .part) &gt; newest base.* on disk.</summary>
    private static string ResolveFinalPath(string destDir, string baseName,
        string? mergerPath, string? lastDestination)
    {
        if (!string.IsNullOrEmpty(mergerPath) && File.Exists(mergerPath))
            return mergerPath;
        if (!string.IsNullOrEmpty(lastDestination))
        {
            var dest = lastDestination.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                ? lastDestination[..^".part".Length] : lastDestination;
            if (File.Exists(dest)) return dest;
        }
        var match = new DirectoryInfo(destDir)
            .EnumerateFiles(baseName + ".*")
            .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                        && !f.Name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (match is null)
            throw new YtDlpException("ytdlp_no_output", ErrorCategory.Media,
                "yt-dlp finished but Nova could not find the output file.");
        return match.FullName;
    }

    private static void DeletePartFiles(string destDir, string baseName)
    {
        try
        {
            foreach (var f in new DirectoryInfo(destDir)
                         .EnumerateFiles(baseName + ".*")
                         .Where(f => f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                                     || f.Name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)))
                f.Delete();
        }
        catch (Exception ex) { EngineLog.Warn($"ytdlp part cleanup failed: {ex.Message}"); }
    }
}
