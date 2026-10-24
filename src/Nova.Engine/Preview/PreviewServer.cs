using System.Net;
using System.Net.Sockets;
using System.Text;
using Nova.Domain;

namespace Nova.Engine.Preview;

/// <summary>
/// v1.5.0 (G17): serves the in-progress ".novadl" file (and completed files) over a
/// loopback-only HTTP endpoint so a media player can preview a partial download.
///   - GET/HEAD + Range support (players seek by re-requesting ranges)
///   - Unranged GETs are clamped to the CONTIGUOUS downloaded prefix — the player sees
///     a shorter file that only contains real bytes; the length grows as data arrives.
///   - Ranged GETs may reach into sparse regions (zeros) — players tolerate the gaps,
///     same as any torrent-streaming client.
///   - Token-authenticated path + 127.0.0.1 binding only. No directory listing ever.
/// The segment map comes live from the engine (running plans) or the repository
/// (paused/interrupted tasks).
/// </summary>
public sealed class PreviewServer : IDisposable
{
    private readonly Func<string, DownloadTask?> _getTask;
    private readonly Func<string, SegmentView[]> _getLiveViews;
    private readonly Func<long, IReadOnlyList<DownloadSegment>> _getSegments;
    private readonly string _token;
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _stopping;
    private int _port;

    public int Port => _port;
    public string Token => _token;

    public PreviewServer(Func<string, DownloadTask?> getTask,
        Func<string, SegmentView[]> getLiveViews,
        Func<long, IReadOnlyList<DownloadSegment>> getSegments,
        string? token = null)
    {
        _getTask = getTask;
        _getLiveViews = getLiveViews;
        _getSegments = getSegments;
        _token = string.IsNullOrEmpty(token)
            ? Convert.ToHexString(Guid.NewGuid().ToByteArray())
            : token!;
    }

    /// <summary>Binds to 127.0.0.1 on an ephemeral port and starts accepting.</summary>
    public void Start()
    {
        if (_listener is not null) return;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 8);
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _stopping = false;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "nova-preview" };
        _acceptThread.Start();
        EngineLog.Info($"preview server listening on 127.0.0.1:{_port}");
    }

    /// <summary>Stable URL for one task (safe to hand to any local player).</summary>
    public string UrlFor(DownloadTask task) =>
        $"http://127.0.0.1:{_port}/{_token}/{Uri.EscapeDataString(task.TaskKey)}/{Uri.EscapeDataString(task.FileName)}";

    private async void AcceptLoop()
    {
        var listener = _listener!;
        while (!_stopping)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) when (_stopping) { return; }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    private sealed class Request
    {
        public string Method = "";
        public string Path = "";
        public long RangeFrom = -1;
        public long RangeTo = -1;
        public bool HasRange;
    }

    private async Task HandleClient(TcpClient client)
    {
        using (client)
        {
            client.ReceiveTimeout = 15_000;
            client.SendTimeout = 15_000;
            try
            {
                var stream = client.GetStream();
                var req = await ReadRequest(stream);
                if (req is null) { await WriteSimple(stream, 400, "Bad Request"); return; }

                // parse: /{token}/{taskKey}/{filename}
                var parts = req.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !string.Equals(parts[0], _token, StringComparison.Ordinal))
                {
                    await WriteSimple(stream, 404, "Not Found");
                    return;
                }

                var task = _getTask(Uri.UnescapeDataString(parts[1]));
                if (task is null) { await WriteSimple(stream, 404, "Not Found"); return; }

                var (path, views) = ResolveFile(task);
                if (path is null) { await WriteSimple(stream, 409, "No Data Yet"); return; }

                await ServeFile(stream, req, task, path, views);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // client hung up mid-transfer — normal for streaming players that seek
            }
        }
    }

    private (string? Path, SegmentView[] Views) ResolveFile(DownloadTask task)
    {
        var temp = task.FinalPath + DownloadDataFile.TempSuffix;
        if (File.Exists(temp))
        {
            var views = _getLiveViews(task.TaskKey);
            if (views.Length == 0 && task.Id > 0)
                views = _getSegments(task.Id)
                    .Select(s => new SegmentView(s.Index, s.Start, s.End, s.Received))
                    .ToArray();
            return (temp, views);
        }
        if (File.Exists(task.FinalPath))
            return (task.FinalPath, Array.Empty<SegmentView>());
        return (null, Array.Empty<SegmentView>());
    }

    private static async Task<Request?> ReadRequest(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var total = 0;
        var headerEnd = -1;
        // read until "\r\n\r\n" or 4 KiB (headers only; body never read)
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (n <= 0) break;
            total += n;
            headerEnd = IndexOfHeaderEnd(buffer, total);
            if (headerEnd >= 0) break;
        }
        if (total == 0) return null;
        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd >= 0 ? headerEnd : total);
        var lines = head.Split("\r\n");
        if (lines.Length == 0) return null;
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var req = new Request { Method = first[0].ToUpperInvariant(), Path = first[1] };
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase) && value.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var spec = value["bytes=".Length..];
                var dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    if (long.TryParse(spec[..dash], out var from) && from >= 0)
                        req.RangeFrom = from;
                    if (long.TryParse(spec[(dash + 1)..], out var to) && to >= 0)
                        req.RangeTo = to;
                    req.HasRange = req.RangeFrom >= 0 || req.RangeTo >= 0;
                }
            }
        }
        return req;
    }

    private static int IndexOfHeaderEnd(byte[] buf, int len)
    {
        for (var i = 0; i + 3 < len; i++)
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                return i;
        return -1;
    }

    /// <summary>Largest L such that [0, L) is fully covered by downloaded segment parts.</summary>
    public static long ContiguousPrefix(SegmentView[] views)
    {
        if (views.Length == 0) return -1; // unknown → use whole file length
        long covered = 0;
        foreach (var v in views.Where(v => v.Received > 0).OrderBy(v => v.Start))
        {
            if (v.Start > covered) break;              // gap → stop
            covered = Math.Max(covered, v.Start + v.Received);
        }
        return covered;
    }

    private async Task ServeFile(NetworkStream stream, Request req, DownloadTask task, string path, SegmentView[] views)
    {
        long fileLength;
        try { fileLength = new FileInfo(path).Length; }
        catch (IOException) { await WriteSimple(stream, 500, "Gone"); return; }

        var contiguous = ContiguousPrefix(views);
        if (contiguous < 0) contiguous = fileLength;              // completed/unknown-map file
        var logicalLength = Math.Min(
            task.TotalBytes > 0 ? task.TotalBytes : fileLength,
            Math.Min(fileLength, contiguous > 0 ? contiguous : 0)); // never advertise beyond contiguous data

        if (req.Method == "HEAD")
        {
            await WriteHead(stream, 200, logicalLength, "application/octet-stream", null);
            return;
        }
        if (req.Method != "GET") { await WriteSimple(stream, 405, "Method Not Allowed"); return; }

        long from = 0, to = logicalLength - 1;
        var status = 200;
        if (req.HasRange)
        {
            // suffix range "bytes=-N"
            if (req.RangeFrom < 0 && req.RangeTo >= 0)
            {
                var n = Math.Min(req.RangeTo, logicalLength);
                from = Math.Max(0, logicalLength - n);
                to = logicalLength - 1;
            }
            else
            {
                from = req.RangeFrom;
                to = req.RangeTo >= 0 ? Math.Min(req.RangeTo, logicalLength - 1) : logicalLength - 1;
            }
            if (from >= logicalLength || to < from)
            {
                await WriteHead(stream, 416, 0, "application/octet-stream",
                    $"bytes */{logicalLength}");
                return;
            }
            status = 206;
        }

        const int chunk = 64 * 1024;
        var buffer = new byte[chunk];
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            chunk, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var contentType = GuessMime(task.FileName);
        await WriteHead(stream, status, to - from + 1, contentType, status == 206 ? $"bytes {from}-{to}/{logicalLength}" : null);

        fs.Position = from;
        var remaining = to - from + 1;
        while (remaining > 0)
        {
            var want = (int)Math.Min(chunk, remaining);
            var n = await fs.ReadAsync(buffer.AsMemory(0, want));
            if (n <= 0) break; // EOF earlier than expected — short read is fine for players
            await stream.WriteAsync(buffer.AsMemory(0, n));
            remaining -= n;
        }
    }

    public static string GuessMime(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".ts" or ".mts" or ".m2ts" => "video/mp2t",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".flv" => "video/x-flv",
            ".wmv" => "video/x-ms-wmv",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            ".opus" => "audio/ogg",
            _ => "application/octet-stream",
        };
    }

    private static async Task WriteHead(NetworkStream stream, int status, long contentLength, string contentType, string? contentRange)
    {
        var sb = new StringBuilder(256);
        sb.Append("HTTP/1.1 ").Append(status).Append(status switch
        {
            200 => " OK", 206 => " Partial Content", 404 => " Not Found", 405 => " Method Not Allowed",
            409 => " Conflict", 416 => " Range Not Satisfiable", _ => "",
        }).Append("\r\n");
        sb.Append("Server: NovaDM-Preview\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        sb.Append("Content-Length: ").Append(contentLength).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (contentRange is not null) sb.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes);
    }

    private static async Task WriteSimple(NetworkStream stream, int status, string reason)
    {
        var body = Encoding.ASCII.GetBytes(reason + "\n");
        var head = $"HTTP/1.1 {status} {reason}\r\nServer: NovaDM-Preview\r\nContent-Length: {body.Length}\r\n" +
                   "Content-Type: text/plain\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    public void Dispose()
    {
        _stopping = true;
        try { _listener?.Stop(); } catch { /* already stopped */ }
        _listener = null;
    }
}
