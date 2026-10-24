using FluentFTP;
using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// FTP/FTPS task support (IDM parity): passive transfers, resume via REST,
/// FTPS (explicit TLS), directory listing for the FTP browser UI.
/// Downloads land in the same crash-safe .novadl temp + unique final flow.
/// </summary>
public static class FtpTask
{
    public sealed record FtpEntry(string Name, bool IsDirectory, long Size, DateTimeOffset Modified);

    /// <summary>Directory listing for the UI (IPC ftp_list).</summary>
    public static async Task<List<FtpEntry>> ListAsync(string url, TaskCredentials? creds, int timeoutSeconds = 20)
    {
        var u = new Uri(url);
        await using var client = await ConnectAsync(u, creds, timeoutSeconds);
        var path = Uri.UnescapeDataString(string.IsNullOrWhiteSpace(u.AbsolutePath) || u.AbsolutePath == "/" ? "/" : u.AbsolutePath);
        var items = await client.GetListing(path);
        return items.Select(i => new FtpEntry(i.Name, i.Type == FtpObjectType.Directory, i.Size, i.Modified)).ToList();
    }

    private static async Task<AsyncFtpClient> ConnectAsync(Uri u, TaskCredentials? creds, int timeoutSeconds)
    {
        var user = !string.IsNullOrEmpty(creds?.UserName) ? creds.UserName : "anonymous";
        var pass = !string.IsNullOrEmpty(creds?.UserName) ? creds.Password : "nova@localhost";
        var cfg = new FtpConfig { ConnectTimeout = timeoutSeconds * 1000, ReadTimeout = timeoutSeconds * 3000 };
        var client = new AsyncFtpClient(u.Host, user, pass, u.Port > 0 ? u.Port : 21, cfg);
        if (u.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase))
            cfg.EncryptionMode = FtpEncryptionMode.Explicit;
        await client.Connect();
        return client;
    }

    /// <summary>Download one FTP file with resume. Returns final path. Reports (deltaBytes, rateBps).</summary>
    public static async Task<string> RunAsync(DownloadTask task, TaskCredentials? creds,
        Action<long, long> onProgress, CancellationToken ct)
    {
        var u = new Uri(task.Url);
        await using var client = await ConnectAsync(u, creds, 20);
        var remotePath = Uri.UnescapeDataString(string.IsNullOrWhiteSpace(u.AbsolutePath) || u.AbsolutePath == "/" ? "/" : u.AbsolutePath);

        var size = await client.GetFileSize(remotePath, -1, ct);
        task.TotalBytes = size;

        Directory.CreateDirectory(task.DestinationDir);
        var tempPath = task.FinalPath + DownloadDataFile.TempSuffix;
        var offset = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        if (size > 0 && offset >= size) offset = 0;
        task.ReceivedBytes = offset;

        await using (var fs = new FileStream(tempPath, offset > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true))
        {
            // FluentFTP reports cumulative TransferredBytes — convert to per-tick deltas so
            // the engine's speed meter AND quota tracker see real byte deltas.
            long lastOffset = offset;
            var progress = new Progress<FtpProgress>(p =>
            {
                var abs = offset + p.TransferredBytes;
                var delta = abs - lastOffset;
                if (delta > 0) { lastOffset = abs; onProgress(delta, (long)p.TransferSpeed); }
                else onProgress(0, (long)p.TransferSpeed);
            });
            var ok = await client.DownloadStream(fs, remotePath, restartPosition: offset,
                progress: progress, token: ct, stopPosition: 0);
            if (!ok) throw new IOException("FTP transfer failed.");
        }

        var final = FilenamePolicy.MakeUniquePath(task.FinalPath);
        File.Move(tempPath, final, overwrite: false);
        task.ReceivedBytes = size > 0 ? size : new FileInfo(final).Length;
        task.TotalBytes = task.ReceivedBytes;
        return final;
    }
}
