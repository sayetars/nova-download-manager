using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// Crash-safe random-access writer for the in-progress file (".novadl" temp).
/// Writes at absolute offsets under a lock — multiple segment workers share ONE handle,
/// which eliminates any write-write race on Windows and POSIX alike.
/// Invariant: persisted "received" counters never exceed what is actually on disk,
/// because counters advance only AFTER Write() returns.
/// </summary>
public sealed class DownloadDataFile : IAsyncDisposable
{
    public const string TempSuffix = ".novadl";
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public string TempPath { get; }
    public string FinalPath { get; }

    private DownloadDataFile(FileStream stream, string tempPath, string finalPath)
    {
        _stream = stream;
        TempPath = tempPath;
        FinalPath = finalPath;
    }

    public static DownloadDataFile OpenOrCreate(string finalPath, long preallocateLength, string? tempRoot = null)
    {
        var temp = string.IsNullOrWhiteSpace(tempRoot)
            ? finalPath + TempSuffix
            : Path.Combine(Path.GetFullPath(tempRoot), Path.GetFileName(finalPath) + TempSuffix);
        var dir = Path.GetDirectoryName(temp);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var exists = File.Exists(temp);
        var stream = new FileStream(temp, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!exists && preallocateLength > 0)
        {
            try { stream.SetLength(preallocateLength); } catch (IOException) { /* sparse FS */ }
        }
        return new DownloadDataFile(stream, temp, finalPath);
    }

    public long Length => _stream.Length;

    public async Task WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            _stream.Position = offset;
            await _stream.WriteAsync(buffer, ct);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>fsync + move to final (unique) path. Returns the final path actually used.</summary>
    public async Task<string> FinalizeAsync()
    {
        await _ioLock.WaitAsync();
        try
        {
            await _stream.FlushAsync(CancellationToken.None);
            _stream.Flush(flushToDisk: true); // fsync — completed means durable
        }
        finally { _ioLock.Release(); }

        var target = FilenamePolicy.MakeUniquePath(FinalPath);
        File.Move(TempPath, target, overwrite: false);
        return target;
    }

    public void AbandonTemp()
    {
        try
        {
            _stream.Dispose();
            if (File.Exists(TempPath)) File.Delete(TempPath);
        }
        catch (IOException) { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _ioLock.Dispose();
    }
}

/// <summary>Static disk utilities shared by the runner.</summary>
public static class Disk
{
    public static bool HasSpaceFor(string directory, long neededBytes, out long freeBytes)
    {
        freeBytes = 0;
        try
        {
            var root = Path.GetFullPath(directory);
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            freeBytes = drive.AvailableFreeSpace;
            return freeBytes > neededBytes + 50 * 1024 * 1024; // 50MB headroom
        }
        catch { return true; } // exotic FS — don't block
    }

    public static string GuessCategory(string fileName, string? mime)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (mime?.StartsWith("video/") == true || ext is ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".ts" or ".m4v") return "video";
        if (mime?.StartsWith("audio/") == true || ext is ".mp3" or ".m4a" or ".aac" or ".flac" or ".ogg" or ".opus" or ".wav") return "audio";
        if (ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso") return "archive";
        if (ext is ".exe" or ".msi" or ".apk" or ".dmg" or ".appimage" or ".deb" or ".rpm") return "program";
        if (ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".odt" or ".txt" or ".epub") return "document";
        if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg") return "image";
        return "other";
    }
}
