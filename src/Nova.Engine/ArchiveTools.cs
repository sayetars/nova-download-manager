using System.Diagnostics;
using System.IO.Compression;
using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// v1.3.0 post-download toolbox (JDownloader/FDM parity):
///  - archive auto-extract: ZIP in-process, 7z/RAR via external tools when present;
///  - media conversion presets ("mp3" audio extraction, "remux" container copy) via ffmpeg.
/// All tools are optional: missing binaries degrade to honest errors, never silent failures.
/// </summary>
public static class ArchiveTools
{
    public static readonly string[] ConvertPresets = { "", "mp3", "remux" };

    public static bool IsValidPreset(string p) => ConvertPresets.Contains(p ?? "");

    // ---- extraction ---------------------------------------------------------

    public sealed record ExtractResult(bool Ok, string Tool, string Message, int Entries);

    public static bool IsArchive(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".zip" or ".7z" or ".rar";
    }

    /// <summary>Extracts an archive next to itself (or into destDir). ZIP in-process; 7z/RAR external.</summary>
    public static ExtractResult Extract(string archivePath, string destDir, CancellationToken ct)
    {
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        Directory.CreateDirectory(destDir);
        try
        {
            if (ext == ".zip") return ExtractZip(archivePath, destDir);
            var tool = FindExternalExtractor(ext);
            if (tool is null)
                return new ExtractResult(false, "", $"No extractor for {ext} found (install 7-Zip or bsdtar).", 0);
            return RunExternal(tool.Value.exe, tool.Value.args.Replace("{ARCHIVE}", archivePath).Replace("{DEST}", destDir), destDir);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ExtractResult(false, "zip", ex.Message, 0);
        }
    }

    private static ExtractResult ExtractZip(string archivePath, string destDir)
    {
        var count = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        var destFull = Path.GetFullPath(destDir);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(destFull, entry.FullName));
            if (!target.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.Ordinal) && target != destFull)
                continue; // zip-slip guard
            if (target.EndsWith(Path.DirectorySeparatorChar) || string.IsNullOrEmpty(entry.Name)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var inStream = entry.Open();
            using var outStream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, FileOptions.Asynchronous);
            inStream.CopyTo(outStream);
            count++;
        }
        return new ExtractResult(true, "zip", $"Extracted {count} entr{(count == 1 ? "y" : "ies")}.", count);
    }

    private static (string exe, string args)? FindExternalExtractor(string ext)
    {
        var candidates = ext switch
        {
            ".7z" => new[] { ("7z", "x -y -o{DEST} {ARCHIVE}"), ("7za", "x -y -o{DEST} {ARCHIVE}"), ("bsdtar", "-xf {ARCHIVE} -C {DEST}") },
            ".rar" => new[] { ("7z", "x -y -o{DEST} {ARCHIVE}"), ("unrar", "x -y {ARCHIVE} {DEST}") },
            _ => Array.Empty<(string, string)>(),
        };
        foreach (var (exe, args) in candidates)
        {
            var resolved = ResolveOnPath(exe);
            if (resolved is not null) return (resolved, args);
        }
        return null;
    }

    internal static string? ResolveOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" };
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                foreach (var e in exts)
                {
                    var p = Path.Combine(dir.Trim(), exe + e);
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* bad PATH entry */ }
        }
        // common Windows 7-Zip install locations
        if (OperatingSystem.IsWindows())
        {
            foreach (var p in new[] { @"C:\Program Files\7-Zip\7z.exe", @"C:\Program Files (x86)\7-Zip\7z.exe" })
                if (File.Exists(p)) return p;
        }
        return null;
    }

    private static ExtractResult RunExternal(string exe, string args, string destDir)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return new ExtractResult(false, exe, "extractor failed to start", 0);
            if (!p.WaitForExit(10 * 60 * 1000)) { p.Kill(); return new ExtractResult(false, exe, "extraction timed out", 0); }
            var files = Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories).Count();
            return p.ExitCode == 0
                ? new ExtractResult(true, exe, $"Extracted {files} files.", files)
                : new ExtractResult(false, exe, $"extractor exited with {p.ExitCode}", 0);
        }
        catch (Exception ex)
        {
            return new ExtractResult(false, exe, ex.Message, 0);
        }
    }

    // ---- conversion ----------------------------------------------------------

    public sealed record ConvertResult(bool Ok, string Message, string? OutputPath);

    /// <summary>Runs an ffmpeg post-conversion preset. Honest failure when ffmpeg is missing.</summary>
    public static ConvertResult Convert(string finalPath, string preset, string? ffmpegPathSetting, CancellationToken ct)
    {
        if (preset is not ("mp3" or "remux"))
            return new ConvertResult(false, $"Unknown convert preset '{preset}'.", null);
        var ffmpeg = ResolveOnPath("ffmpeg") ?? (File.Exists(ffmpegPathSetting ?? "") ? ffmpegPathSetting : null);
        if (ffmpeg is null)
            return new ConvertResult(false, "ffmpeg was not found on PATH; conversion skipped.", null);

        var args = preset switch
        {
            "mp3" => $"-y -i \"{finalPath}\" -vn -acodec libmp3lame -q:a 2 \"{NoExt(finalPath)}.mp3\"",
            _ => $"-y -i \"{finalPath}\" -c copy \"{NoExt(finalPath)}.remux.mp4\"",
        };
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
            if (p is null) return new ConvertResult(false, "ffmpeg failed to start", null);
            var err = p.StandardError.ReadToEndAsync().Result;
            if (!p.WaitForExit(30 * 60 * 1000)) { p.Kill(); return new ConvertResult(false, "conversion timed out", null); }
            var output = preset == "mp3" ? $"{NoExt(finalPath)}.mp3" : $"{NoExt(finalPath)}.remux.mp4";
            return p.ExitCode == 0 && File.Exists(output)
                ? new ConvertResult(true, $"Converted → {Path.GetFileName(output)}", output)
                : new ConvertResult(false, $"ffmpeg exited with {p.ExitCode}: {LastLine(err)}", null);
        }
        catch (Exception ex)
        {
            return new ConvertResult(false, ex.Message, null);
        }
    }

    private static string NoExt(string p) => p[..^Path.GetExtension(p).Length];
    private static string LastLine(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1][..Math.Min(200, lines[^1].Length)] : "";
    }
}
