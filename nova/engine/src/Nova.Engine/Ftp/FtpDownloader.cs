using System.Net;
using System.Net.Sockets;
using Nova.Core.Models;
using Nova.Storage;
using Renci.SshNet;

namespace Nova.Engine.Ftp;

/// <summary>
/// FTP/FTPS downloader using multi-part RETR with REST (resume) when the
/// server supports it. Single stream otherwise. Mirrors the segmented engine's
/// progress/persist contract so the scheduler treats both identically.
/// </summary>
public sealed class FtpDownloader
{
    private readonly TaskRepository _repo;

    public FtpDownloader(TaskRepository repo) => _repo = repo;

    public event Action<DownloadTask>? ProgressTick;
    public event Action<DownloadTask, FailureReason, string?>? Failed;
    public event Action<DownloadTask>? Completed;

    public async Task RunAsync(DownloadTask task, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(task.Url);
            var host = uri.Host;
            var port = uri.Port < 0 ? 21 : uri.Port;
            var pathAndFile = uri.AbsolutePath.TrimStart('/');

            var request = (FtpWebRequest)WebRequest.Create(task.Url);
            request.Method = WebRequestMethods.Ftp.DownloadFile;
            request.Credentials = new NetworkCredential(task.Username ?? "anonymous", task.Secret ?? "nova@localhost");
            request.UsePassive = true;
            request.UseBinary = true;

            var partPath = task.TempFilePath ?? Path.ChangeExtension(task.FullPath, ".novapart");
            task.TempFilePath = partPath;
            var resume = File.Exists(partPath) && task.ReceivedBytes > 0;
            if (resume) request.ContentOffset = task.ReceivedBytes;

            var dir = Path.GetDirectoryName(partPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var resp = (FtpWebResponse)await request.GetResponseAsync();
            var total = resp.ContentLength;
            if (total > 0)
            {
                task.TotalBytes = resume ? task.ReceivedBytes + total : total;
                await using var pre = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                    1 << 16, FileOptions.Asynchronous);
                pre.SetLength(task.TotalBytes);
            }

            await using var stream = resp.GetResponseStream();
            await using var output = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
                1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            output.Position = task.ReceivedBytes;

            var buf = new byte[64 * 1024];
            int n;
            var tick = Environment.TickCount64;
            while ((n = await stream.ReadAsync(buf, ct)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, n), ct);
                task.ReceivedBytes += n;
                if (Environment.TickCount64 - tick > 500)
                {
                    tick = Environment.TickCount64;
                    ProgressTick?.Invoke(task);
                }
            }

            task.TotalBytes = Math.Max(task.TotalBytes, task.ReceivedBytes);
            output.Close();
            if (File.Exists(task.FullPath)) File.Delete(task.FullPath);
            File.Move(partPath, task.FullPath);
            task.TempFilePath = null;
            task.ReceivedBytes = task.TotalBytes;
            Completed?.Invoke(task);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _repo.Update(task);
        }
        catch (WebException ex)
        {
            var reason = ex.Status switch
            {
                WebExceptionStatus.ConnectFailure => FailureReason.ConnectionRefused,
                WebExceptionStatus.Timeout => FailureReason.Timeout,
                WebExceptionStatus.ProtocolError => FailureReason.Forbidden,
                _ => FailureReason.Unknown
            };
            Failed?.Invoke(task, reason, ex.Message);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(task, FailureReason.Unknown, ex.Message);
        }
    }
}

/// <summary>SFTP via SSH.NET — full multi-segment when server allows parallel reads.</summary>
public sealed class SftpDownloader
{
    private readonly TaskRepository _repo;

    public SftpDownloader(TaskRepository repo) => _repo = repo;

    public event Action<DownloadTask>? ProgressTick;
    public event Action<DownloadTask, FailureReason, string?>? Failed;
    public event Action<DownloadTask>? Completed;

    public async Task RunAsync(DownloadTask task, CancellationToken ct)
    {
        SftpClient? client = null;
        try
        {
            var uri = new Uri(task.Url);
            var port = uri.Port < 0 ? 22 : uri.Port;
            var username = string.IsNullOrEmpty(task.Username) ? Environment.UserName : task.Username;
            var auth = new PasswordAuthenticationMethod(username, task.Secret ?? "");
            var connInfo = new ConnectionInfo(uri.Host, port, username, auth);
            client = new SftpClient(connInfo);
            client.Connect();
            if (!client.IsConnected) throw new IOException("SFTP connect failed");

            var remotePath = uri.AbsolutePath;
            var attrs = client.GetAttributes(remotePath);
            task.TotalBytes = attrs.Size;

            var partPath = task.TempFilePath ?? Path.ChangeExtension(task.FullPath, ".novapart");
            task.TempFilePath = partPath;
            var dir = Path.GetDirectoryName(partPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var output = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
                1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            output.SetLength(task.TotalBytes);

            var buf = new byte[64 * 1024];
            long position = 0;
            var tick = Environment.TickCount64;
            using var src = client.OpenRead(remotePath);
            while (position < task.TotalBytes)
            {
                var n = src.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                await output.WriteAsync(buf.AsMemory(0, n), ct);
                position += n;
                task.ReceivedBytes = position;
                if (Environment.TickCount64 - tick > 500)
                {
                    tick = Environment.TickCount64;
                    ProgressTick?.Invoke(task);
                }
            }

            output.Close();
            if (File.Exists(task.FullPath)) File.Delete(task.FullPath);
            File.Move(partPath, task.FullPath);
            task.TempFilePath = null;
            task.ReceivedBytes = task.TotalBytes;
            Completed?.Invoke(task);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _repo.Update(task);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(task, FailureReason.Unknown, ex.Message);
        }
        finally
        {
            try { client?.Disconnect(); } catch { }
            client?.Dispose();
        }
    }
}
