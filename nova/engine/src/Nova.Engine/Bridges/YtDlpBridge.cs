using System.Diagnostics;
using System.Text.Json;

namespace Nova.Engine.Bridges;

/// <summary>
/// yt-dlp bridge: gives Nova coverage of 1000+ sites (YouTube, Twitter,
/// Instagram, hundreds of extractors) using an external yt-dlp binary when
/// present. Engine talks JSON lines (--print-json) and imports the resolved
/// direct URL into its own segmented downloader, so Nova's speed engine —
/// not yt-dlp's — moves the bytes.
/// </summary>
public sealed class YtDlpBridge
{
    private string? _cachedPath;
    private readonly SemaphoreSlim _probe = new(1, 1);

    public event Action<string>? Log;

    public async Task<string?> LocateBinaryAsync(CancellationToken ct = default)
    {
        if (_cachedPath != null && File.Exists(_cachedPath)) return _cachedPath;
        await _probe.WaitAsync(ct);
        try
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("NOVA_YTDLP_PATH"),
                Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp"),
                Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe"),
                "/usr/local/bin/yt-dlp", "/usr/bin/yt-dlp",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova", "tools", "yt-dlp.exe")
            };
            foreach (var c in candidates)
            {
                if (!string.IsNullOrEmpty(c) && File.Exists(c))
                {
                    _cachedPath = c;
                    return c;
                }
            }
            // PATH lookup
            var which = await RunAsync("--version", ".", ct);
            if (which.exit == 0)
            {
                var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathVar.Split(Path.PathSeparator))
                {
                    try
                    {
                        var p = Path.Combine(dir, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
                        if (File.Exists(p)) { _cachedPath = p; return p; }
                    }
                    catch { /* skip malformed PATH entry */ }
                }
            }
            return null;
        }
        finally { _probe.Release(); }
    }

    public sealed record YtDlpInfo
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? WebpageUrl { get; set; }
        public string? Url { get; set; }          // best direct URL
        public string? Ext { get; set; }
        public long? Filesize { get; set; }
        public double? Duration { get; set; }
        public bool IsLive { get; set; }
        public List<Format> Formats { get; set; } = new();
        public string? Thumbnail { get; set; }
        public string? Uploader { get; set; }
        public Dictionary<string, string> HttpHeaders { get; set; } = new();
    }

    public sealed record Format
    {
        public string FormatId { get; set; } = "";
        public string? Ext { get; set; }
        public long? Filesize { get; set; }
        public string? FormatNote { get; set; }
        public int? Height { get; set; }
        public double? Fps { get; set; }
        public string? Vcodec { get; set; }
        public string? Acodec { get; set; }
        public long? Tbr { get; set; }
        public string Label =>
            (Height is { } h ? $"{h}p{(Fps is { } f && f > 30 ? ((int)Math.Round(f)).ToString() : "")}" : FormatNote ?? "audio") +
            $" · {Vcodec ?? "?"}/{Acodec ?? "?"}";
    }

    public async Task<YtDlpInfo?> ExtractAsync(string pageUrl, string? formatSelector = null,
        Dictionary<string, string>? extraArgs = null, CancellationToken ct = default)
    {
        var bin = await LocateBinaryAsync(ct);
        if (bin is null)
        {
            Log?.Invoke("yt-dlp binary not found — bridge disabled");
            return null;
        }

        var args = new List<string>
        {
            "-J", "--no-warnings", "--no-playlist",
            "--no-check-certificates",
        };
        if (!string.IsNullOrEmpty(formatSelector))
            args.AddRange(new[] { "-f", formatSelector });
        if (extraArgs != null)
            foreach (var (k, v) in extraArgs) args.Add($"--{k}={v}");
        args.Add(pageUrl);

        var (exit, stdout, stderr) = await RunCapturedAsync(bin, args, ct);
        if (exit != 0)
        {
            Log?.Invoke($"yt-dlp failed: {stderr.Trim()}");
            return null;
        }
        var info = JsonSerializer.Deserialize<YtDlpInfo>(stdout, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        });
        return info;
    }

    public async Task<(int exit, string stdout, string stderr)> RunCapturedAsync(string bin, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(bin)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    private async Task<(int exit, string stdout, string stderr)> RunAsync(string args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "where" : "which",
            Arguments = OperatingSystem.IsWindows() ? "yt-dlp" : "yt-dlp",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, stdout, "");
        }
        catch { return (127, "", ""); }
    }
}
