using Nova.Domain;
using Renci.SshNet;

namespace Nova.Engine;

/// <summary>
/// SFTP task support (v1.3.0, aria2/FDM parity): segmented download over parallel
/// SFTP connections, resume via per-segment seeks, directory listing for the UI.
/// Runs on the same crash-safe .novadl temp + unique-final flow as HTTP/FTP.
/// </summary>
public static class SftpTask
{
    public sealed record SftpEntry(string Name, bool IsDirectory, long Size, DateTimeOffset Modified);

    private static ConnectionInfo BuildInfo(Uri u, TaskCredentials? creds)
    {
        var user = string.IsNullOrEmpty(creds?.UserName) ? "anonymous" : creds.UserName;
        var pass = creds?.Password ?? "";
        // optional ssh://user:pass@host:port/path — port 22 default
        return new ConnectionInfo(u.Host, u.Port > 0 ? u.Port : 22, user,
            new PasswordAuthenticationMethod(user, pass));
    }

    /// <summary>Directory listing for the UI (IPC ftp_list also accepts sftp:// URLs).</summary>
    public static async Task<List<SftpEntry>> ListAsync(string url, TaskCredentials? creds, int timeoutSeconds = 20)
    {
        var u = new Uri(url);
        return await Task.Run(() =>
        {
            using var client = new SftpClient(BuildInfo(u, creds))
            {
                OperationTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            };
            client.Connect();
            if (!client.IsConnected) throw new NovaHttpException("sftp_connect", ErrorCategory.Network,
                "Could not connect to the SFTP server.", url);
            var path = Uri.UnescapeDataString(string.IsNullOrWhiteSpace(u.AbsolutePath) ? "/" : u.AbsolutePath);
            var items = client.ListDirectory(path)
                .Where(e => e.Name is not "." and not "..")
                .Select(e => new SftpEntry(e.Name, e.IsDirectory, e.Attributes.Size, e.LastWriteTime))
                .ToList();
            client.Disconnect();
            return items;
        });
    }

    /// <summary>Download one SFTP file with parallel segment connections. Returns final path.</summary>
    public static async Task<string> RunAsync(DownloadTask task, TaskCredentials? creds,
        Action<long, long> onProgress, CancellationToken ct)
    {
        var u = new Uri(task.Url);
        var remotePath = Uri.UnescapeDataString(u.AbsolutePath);
        if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
            throw new NovaHttpException("sftp_bad_path", ErrorCategory.Protocol,
                "The SFTP URL does not point to a file.", task.Url);

        Directory.CreateDirectory(task.DestinationDir);
        var tempPath = task.FinalPath + DownloadDataFile.TempSuffix;
        var connections = Math.Clamp(task.MaxConnections, 1, 8);

        long size;
        List<DownloadSegment> plan;
        bool resumable;
        var existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        using (var probe = new SftpClient(BuildInfo(u, creds)))
        {
            probe.Connect();
            if (!probe.IsConnected)
                throw new NovaHttpException("sftp_connect", ErrorCategory.Network,
                    "Could not connect to the SFTP server.", task.Url);
            var attrs = probe.GetAttributes(remotePath);
            if (attrs.IsDirectory)
                throw new NovaHttpException("sftp_is_directory", ErrorCategory.Protocol,
                    "The SFTP URL points to a directory.", task.Url);
            size = attrs.Size;
            task.TotalBytes = size;
            probe.Disconnect();
        }

        if (size > 0 && connections > 1 && size >= 2 * 1024 * 1024 && existing == 0)
        {
            var n = (int)Math.Min(connections, Math.Max(1, size / (512 * 1024)));
            var baseLen = size / n;
            long off = 0;
            plan = Enumerable.Range(0, n).Select(i =>
            {
                var len = i == n - 1 ? size - off : baseLen;
                var seg = new DownloadSegment { TaskId = task.Id, Index = i, Start = off, End = off + len - 1, Received = 0 };
                off += len;
                return seg;
            }).ToList();
            resumable = true;
        }
        else if (existing > 0 && size > 0 && existing < size)
        {
            // single-connection resume (REST parity: continue from byte offset)
            plan = new List<DownloadSegment> { new() { TaskId = task.Id, Index = 0, Start = 0, End = size - 1, Received = existing } };
            resumable = true;
        }
        else
        {
            plan = new List<DownloadSegment> { new() { TaskId = task.Id, Index = 0, Start = 0, End = size - 1, Received = 0 } };
            resumable = false;
        }

        long done = 0;
        _ = done;
        var speedMeter = new SpeedMeter();

        async Task RunSegment(DownloadSegment seg)
        {
            using var client = new SftpClient(BuildInfo(u, creds));
            client.Connect();
            if (!client.IsConnected)
                throw new NovaHttpException("sftp_connect", ErrorCategory.Network,
                    "Could not connect to the SFTP server.", task.Url);
            await using var stream = client.OpenRead(remotePath);
            stream.Seek(seg.Start + seg.Received, SeekOrigin.Begin);

            var buffer = new byte[64 * 1024];
            await using (var fs = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.Write, 64 * 1024, useAsync: true))
            {
                while (!ct.IsCancellationRequested && seg.Received < seg.Length)
                {
                    var want = (int)Math.Min(buffer.Length, seg.Length - seg.Received);
                    var n = await stream.ReadAsync(buffer.AsMemory(0, want), ct);
                    if (n <= 0) break;
                    fs.Seek(seg.Start + seg.Received, SeekOrigin.Begin);
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    seg.Received += n;
                    speedMeter.Add(n);
                    onProgress(n, speedMeter.SpeedBps);
                }
            }
            client.Disconnect();
        }

        if (resumable && plan.Count > 1)
            await Task.WhenAll(plan.Select(RunSegment));
        else
            await RunSegment(plan[0]);

        ct.ThrowIfCancellationRequested();

        var final = FilenamePolicy.MakeUniquePath(task.FinalPath);
        File.Move(tempPath, final, overwrite: false);
        task.ReceivedBytes = size;
        task.TotalBytes = size;
        return final;
    }
}
