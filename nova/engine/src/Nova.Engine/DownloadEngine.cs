using System.Collections.Concurrent;
using System.Net;
using Nova.Core;
using Nova.Core.Events;
using Nova.Core.Models;
using Nova.Core.Protocol;
using Nova.Engine.Bridges;
using Nova.Engine.Ftp;
using Nova.Engine.Http;
using Nova.Engine.Streams;
using Nova.Storage;

namespace Nova.Engine;

/// <summary>
/// Orchestrator: owns the task registry, spawns protocol-specific runners,
/// enforces concurrency limits, and maps runner events to state transitions,
/// persistence and IPC broadcast. One engine per process — the daemon the
/// UI and extensions talk to.
/// </summary>
public sealed class DownloadEngine : IAsyncDisposable
{
    private readonly TaskRepository _repo;
    private readonly SettingsRepository _settingsRepo;
    private readonly SettingsDocument _settings;
    private readonly SpeedController _speed = new();
    private readonly ConcurrentDictionary<string, Task> _runners = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancels = new();
    private readonly ConcurrentDictionary<string, HttpProbe.ProbeResult> _probes = new();
    private readonly HttpClient _http;
    private readonly HttpProbe _probe;
    private readonly HlsDownloader _hls;
    private readonly DashParser _dash;
    public readonly YtDlpBridge YtDlp;
    public readonly ServerProfiler Profiler;
    public readonly DebridService Debrid;
    public readonly CloudHostResolver CloudHosts;
    private readonly FtpDownloader _ftp;
    private readonly SftpDownloader _sftp;
    private readonly ConcurrentDictionary<string, DownloadTask> _tasks = new();
    private readonly object _shutdownGate = new();
    private bool _shuttingDown;

    public string DatabasePath { get; }
    public event Action<TaskProgress>? ProgressEvent;
    public event Action<TaskStateChanged>? StateEvent;

    public DownloadEngine(string databasePath)
    {
        DatabasePath = databasePath;
        var db = new Database(databasePath);
        _repo = new TaskRepository(db);
        _settingsRepo = new SettingsRepository(db);
        _settings = _settingsRepo.Load();
        _speed.SetGlobalLimit(_settings.SpeedLimitBytesPerSec);
        _speed.Start();

        _http = BuildHttpClient(_settings);
        _probe = new HttpProbe(_http, new SettingsBridge());
        _hls = new HlsDownloader(_http);
        _dash = new DashParser();
        YtDlp = new YtDlpBridge();
        Profiler = new ServerProfiler(_http);
        Debrid = new DebridService(_http);
        CloudHosts = new CloudHostResolver(_http);
        _ftp = new FtpDownloader(_repo);
        _sftp = new SftpDownloader(_repo);

        RecoverFromDatabase();
    }

    public static HttpClient BuildHttpClient(SettingsDocument s)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = true,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            CheckCertificateRevocationList = true
        };
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(30, s.IdleTimeoutSec))
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NovaDownloadManager/1.0");
        return http;
    }

    private void RecoverFromDatabase()
    {
        foreach (var t in _repo.LoadAll())
        {
            // crash recovery: anything mid-flight becomes Paused (resumable)
            if (t.State is DownloadState.Analyzing or DownloadState.Downloading or DownloadState.Pausing
                or DownloadState.Retrying or DownloadState.Queued or DownloadState.Verifying)
                t.State = DownloadState.Paused;
            _tasks[t.Id] = t;
        }
    }

    // ------------------------------------------------------------------
    // Task API (called by IPC dispatcher)
    // ------------------------------------------------------------------

    public IReadOnlyCollection<DownloadTask> List() => _tasks.Values.ToList();

    public DownloadTask? Get(string id) => _tasks.TryGetValue(id, out var t) ? t : null;

    public async Task<DownloadTask> AddUrlAsync(AddUrlRequest req)
    {
        var task = new DownloadTask
        {
            Url = req.Url,
            FileName = req.FileName ?? "",
            SaveDirectory = req.SaveDirectory ?? _settings.DefaultSaveDirectory,
            Category = req.Category,
            QueueName = req.Queue,
            Username = req.Username,
            Secret = req.Password,
            Headers = req.Headers ?? new Dictionary<string, string>(),
            IsSequentialPreview = req.SequentialPreview,
            Priority = req.Priority,
            Proxy = req.ProxyJson is { } pj ? Json.Deserialize<ProxyConfig>(pj) : null
        };
        if (DateTime.TryParse(req.ScheduledStart, out var sched)) task.ScheduledStart = sched;

        // normalize protocol
        if (Uri.TryCreate(req.Url, UriKind.Absolute, out var uri))
        {
            task.Protocol = uri.Scheme.ToLowerInvariant() switch
            {
                "ftp" => TransferProtocol.Ftp,
                "ftps" => TransferProtocol.Ftps,
                "sftp" => TransferProtocol.Sftp,
                "magnet" => TransferProtocol.Magnet,
                "https" => TransferProtocol.Https,
                _ => TransferProtocol.Http
            };
            if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) task.Protocol = TransferProtocol.Hls;
            if (uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)) task.Protocol = TransferProtocol.Dash;
        }

        ApplyDuplicatePolicy(task);

        _repo.Insert(task);
        _tasks[task.Id] = task;
        EventBus.Instance.Publish(new TaskAdded(task));

        if (_settings.AutoStartOnAdd && task.ScheduledStart is null)
            StartTask(task);
        return await Task.FromResult(task);
    }

    public async Task<DownloadTask> AddVideoAsync(AddVideoRequest req)
    {
        var proto = req.Protocol.ToLowerInvariant() switch
        {
            "hls" => TransferProtocol.Hls,
            "dash" => TransferProtocol.Dash,
            _ => TransferProtocol.Http
        };
        var task = new DownloadTask
        {
            Url = req.MediaUrl,
            ReferringUrl = req.PageUrl,
            FileName = SanitizeFileName(req.Title),
            SaveDirectory = req.SaveDirectory ?? _settings.DefaultSaveDirectory,
            Category = "video",
            Protocol = proto,
            Headers = req.Headers ?? new Dictionary<string, string>(),
            Video = new VideoCandidate
            {
                PageUrl = req.PageUrl, MediaUrl = req.MediaUrl, Title = req.Title,
                Protocol = proto, IsLive = req.IsLive, DetectMethod = req.DetectMethod
            },
            SelectedVideoTrackId = req.VideoTrackId,
            SelectedAudioTrackId = req.AudioTrackId
        };
        _repo.Insert(task);
        _tasks[task.Id] = task;
        EventBus.Instance.Publish(new TaskAdded(task));
        if (_settings.AutoStartOnAdd) StartTask(task);
        return await Task.FromResult(task);
    }

    public bool Pause(string id)
    {
        if (!_tasks.TryGetValue(id, out var task)) return false;
        if (task.State is DownloadState.Downloading or DownloadState.Queued or DownloadState.Analyzing)
        {
            if (_cancels.TryGetValue(id, out var cts)) cts.Cancel();
            SetState(task, DownloadState.Pausing);
            return true;
        }
        return false;
    }

    public bool Resume(string id)
    {
        if (!_tasks.TryGetValue(id, out var task)) return false;
        if (task.State is DownloadState.Paused or DownloadState.Interrupted or DownloadState.Failed)
        {
            task.RetryCount = 0;
            StartTask(task);
            return true;
        }
        return false;
    }

    public bool Cancel(string id, bool deleteFiles)
    {
        if (!_tasks.TryGetValue(id, out var task)) return false;
        if (_cancels.TryGetValue(id, out var cts)) cts.Cancel();
        SetState(task, DownloadState.Cancelled);
        if (deleteFiles)
        {
            TryDelete(task.TempFilePath);
            TryDelete(task.FullPath);
        }
        return true;
    }

    public bool Remove(string id, bool deleteFiles)
    {
        Cancel(id, deleteFiles);
        _repo.Delete(id, out var existed);
        _tasks.TryRemove(id, out _);
        EventBus.Instance.Publish(new TaskRemoved(id, deleteFiles));
        return existed;
    }

    public void SetGlobalSpeedLimit(long bytesPerSec) => _speed.SetGlobalLimit(bytesPerSec);

    public void SetTaskSpeedLimit(string id, long bytesPerSec) => _speed.SetTaskLimit(id, bytesPerSec);

    public async Task ShutdownAsync()
    {
        lock (_shutdownGate)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
        }
        foreach (var (_, cts) in _cancels) cts.Cancel();
        await Task.WhenAll(_runners.Values.ToArray());
    }

    // ------------------------------------------------------------------
    // Runner spawning
    // ------------------------------------------------------------------

    private void StartTask(DownloadTask task)
    {
        SetState(task, DownloadState.Queued);
        _runners[task.Id] = Task.Run(() => RunLifecycleAsync(task));
    }

    private async Task RunLifecycleAsync(DownloadTask task)
    {
        try
        {
            // Concurrency gate: max N active downloads, highest priority first
            while (ActiveCount() >= _settings.MaxConcurrentTasks && task.State == DownloadState.Queued)
                await Task.Delay(500);

            var cts = _cancels[task.Id] = new CancellationTokenSource();
            task.StartedAt ??= DateTime.UtcNow;
            SetState(task, DownloadState.Downloading);

            switch (task.Protocol)
            {
                case TransferProtocol.Ftp or TransferProtocol.Ftps:
                    await RunFtpAsync(task, cts.Token);
                    break;
                case TransferProtocol.Sftp:
                    await RunSftpAsync(task, cts.Token);
                    break;
                case TransferProtocol.Hls:
                    await RunHlsAsync(task, cts.Token);
                    break;
                case TransferProtocol.Dash:
                    await RunDashAsync(task, cts.Token);
                    break;
                case TransferProtocol.YtDlp:
                    await RunYtDlpAsync(task, cts.Token);
                    break;
                default:
                    await RunHttpAsync(task, cts.Token);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SetState(task, DownloadState.Paused, null, null);
        }
        catch (Exception ex)
        {
            task.RetryCount++;
            if (task.RetryCount < task.MaxRetries)
            {
                SetState(task, DownloadState.Retrying, null, ex.Message);
                var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(_settings.RetryBackoffBaseSec, task.RetryCount), 300));
                await Task.Delay(backoff);
                StartTask(task);
            }
            else
            {
                SetState(task, DownloadState.Failed, FailureReason.Unknown, ex.Message);
            }
        }
        finally
        {
            _cancels.TryRemove(task.Id, out var cts);
            cts?.Dispose();
            _runners.TryRemove(task.Id, out _);
        }
    }

    private int ActiveCount() => _tasks.Values.Count(t => t.State == DownloadState.Downloading);

    private async Task RunHttpAsync(DownloadTask task, CancellationToken ct)
    {
        // 1) Probe (reuse cached probe on resume)
        if (!_probes.TryGetValue(task.Id, out var probe))
        {
            SetState(task, DownloadState.Analyzing);
            probe = await _probe.ProbeAsync(task, ct);
            _probes[task.Id] = probe;
            task.FinalUrl = probe.FinalUrl;
            task.AcceptRanges = probe.AcceptRanges;
            task.TotalBytes = probe.TotalBytes ?? -1;
            if (!string.IsNullOrEmpty(task.FileName)) { /* keep user-provided */ }
            else task.FileName = probe.FileName;
            if (probe.ETag != null) task.ETag = probe.ETag;
            if (probe.LastModified != null) task.LastModified = probe.LastModified;
            SetState(task, DownloadState.Downloading);
        }

        if (task.TotalBytes == 0)
        {
            task.ReceivedBytes = 0;
            File.WriteAllText(task.FullPath, "");
            SetState(task, DownloadState.Completed);
            return;
        }

        // 2) Resume validation: server must still have the same file
        if (task.ReceivedBytes > 0 && !await ValidateResumeAsync(task, probe, ct))
        {
            task.ReceivedBytes = 0;
            task.Segments.Clear();
        }

        // 3) Segmented transfer
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloader = SegmentedHttpDownloader.Create(task, _http, _repo, _speed);
        downloader.ProgressTick += t => EmitProgress(t);
        downloader.Failed += (t, reason, err) =>
        {
            task.RetryCount++;
            if (task.RetryCount < task.MaxRetries)
            {
                SetState(t, DownloadState.Retrying, reason, err);
                tcs.TrySetException(new RetryRequestedException(err));
            }
            else tcs.TrySetException(new DownloadFailedException(reason, err));
        };
        downloader.Completed += t =>
        {
            // 4) Verify checksum when provided
            if (!string.IsNullOrEmpty(task.ExpectedHash))
            {
                SetState(task, DownloadState.Verifying);
                var ok = ChecksumVerifier.VerifyAsync(task, task.FullPath, ct).GetAwaiter().GetResult();
                if (!ok)
                {
                    tcs.TrySetException(new DownloadFailedException(FailureReason.ChecksumMismatch, "checksum mismatch"));
                    return;
                }
            }
            task.CompletedAt = DateTime.UtcNow;
            SetState(task, DownloadState.Completed);
            tcs.TrySetResult();
        };

        await downloader.RunAsync(ct);
        await tcs.Task;
    }

    private async Task<bool> ValidateResumeAsync(DownloadTask task, HttpProbe.ProbeResult probe, CancellationToken ct)
    {
        if (task.ETag is null && task.LastModified is null) return true;
        using var req = new HttpRequestMessage(HttpMethod.Head, task.FinalUrl ?? task.Url);
        using var resp = await _http.SendAsync(req, ct);
        var etag = resp.Headers.ETag?.Tag;
        var lm = resp.Content.Headers.LastModified?.ToString();
        if (task.ETag is not null && etag is not null && etag != task.ETag) return false;
        if (task.LastModified is not null && lm is not null && lm != task.LastModified) return false;
        return true;
    }

    private async Task RunFtpAsync(DownloadTask task, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ftp.ProgressTick += EmitProgress;
        _ftp.Failed += (t, r, e) => tcs.TrySetException(new DownloadFailedException(r, e));
        _ftp.Completed += t => { task.CompletedAt = DateTime.UtcNow; SetState(t, DownloadState.Completed); tcs.TrySetResult(); };
        var run = _ftp.RunAsync(task, ct);
        await Task.WhenAll(run, tcs.Task);
    }

    private async Task RunSftpAsync(DownloadTask task, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sftp.ProgressTick += EmitProgress;
        _sftp.Failed += (t, r, e) => tcs.TrySetException(new DownloadFailedException(r, e));
        _sftp.Completed += t => { task.CompletedAt = DateTime.UtcNow; SetState(t, DownloadState.Completed); tcs.TrySetResult(); };
        var run = _sftp.RunAsync(task, ct);
        await Task.WhenAll(run, tcs.Task);
    }

    private async Task RunHlsAsync(DownloadTask task, CancellationToken ct)
    {
        var baseUrl = new Uri(task.Url);
        var playlist = await _hls.ParseAsync(baseUrl, task.Headers, ct);
        var outTs = Path.Combine(task.SaveDirectory, Path.ChangeExtension(SanitizeFileName(task.FileName), ".ts"));

        var keyBytes = (byte[]?)null;
        _hls.Progress += (done, total) =>
        {
            task.ReceivedBytes = done;
            task.TotalBytes = total;
            EmitProgress(task);
        };

        Directory.CreateDirectory(task.SaveDirectory);
        await _hls.DownloadAsync(playlist, outTs, keyBytes, task.Headers, null, ct);

        // remux to mp4 with ffmpeg if available
        var final = await TryRemuxAsync(outTs, task, ct) ?? outTs;
        task.FileName = Path.GetFileName(final);
        task.TotalBytes = new FileInfo(final).Length;
        task.ReceivedBytes = task.TotalBytes;
        task.CompletedAt = DateTime.UtcNow;
        SetState(task, DownloadState.Completed);
    }

    private async Task RunDashAsync(DownloadTask task, CancellationToken ct)
    {
        var xml = await _http.GetStringAsync(task.Url, ct);
        var manifest = _dash.Parse(xml, new Uri(task.Url));

        var video = manifest.Sets.FirstOrDefault(s => s.ContentType == "video")?.Representations
            .OrderByDescending(r => r.Height).FirstOrDefault();
        var audio = manifest.Sets.FirstOrDefault(s => s.ContentType == "audio")?.Representations
            .OrderByDescending(r => r.Bandwidth).FirstOrDefault();

        Directory.CreateDirectory(task.SaveDirectory);
        var videoFile = Path.Combine(task.SaveDirectory, Path.GetFileNameWithoutExtension(SanitizeFileName(task.FileName)) + ".video.m4s");
        var audioFile = audio != null ? Path.Combine(task.SaveDirectory, Path.GetFileNameWithoutExtension(SanitizeFileName(task.FileName)) + ".audio.m4s") : null;

        var videoTask = DownloadSegmentListAsync(video!, videoFile, task, ct);
        var audioTask = audio != null ? DownloadSegmentListAsync(audio, audioFile!, task, ct) : Task.CompletedTask;
        await Task.WhenAll(videoTask, audioTask);

        // remux
        var final = await TryRemuxDashAsync(videoFile, audioFile, task, ct) ?? videoFile;
        task.FileName = Path.GetFileName(final);
        task.TotalBytes = new FileInfo(final).Length;
        task.ReceivedBytes = task.TotalBytes;
        task.CompletedAt = DateTime.UtcNow;
        SetState(task, DownloadState.Completed);
    }

    private async Task DownloadSegmentListAsync(DashParser.RepresentationInfo rep, string outFile, DownloadTask task, CancellationToken ct)
    {
        await using var output = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.Read,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long done = 0;
        foreach (var seg in rep.Segments)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(HttpMethod.Get, seg.Url);
            foreach (var (k, v) in task.Headers) req.Headers.TryAddWithoutValidation(k, v);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            await output.WriteAsync(bytes, ct);
            Interlocked.Add(ref done, bytes.Length);
        }
    }

    private async Task RunYtDlpAsync(DownloadTask task, CancellationToken ct)
    {
        var info = await YtDlp.ExtractAsync(task.Url, task.YtDlpFormat, null, ct)
            ?? throw new InvalidOperationException("yt-dlp extract failed");
        task.Url = info.Url ?? task.Url;
        task.FileName = SanitizeFileName(info.Title) + "." + (info.Ext ?? "mp4");
        task.TotalBytes = info.Filesize ?? -1;
        _repo.Update(task);

        if (info.Url is null)
        {
            throw new DownloadFailedException(FailureReason.ProtocolNotSupported, "yt-dlp returned no direct URL");
        }
        task.Protocol = TransferProtocol.Http;
        await RunHttpAsync(task, ct);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<string?> TryRemuxAsync(string tsFile, DownloadTask task, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return null;
        var target = Path.ChangeExtension(tsFile, ".mp4");
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            Arguments = $"-y -i \"{tsFile}\" -c copy -bsf:a aac_adtstoasc \"{target}\"",
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode == 0 && File.Exists(target))
        {
            try { File.Delete(tsFile); } catch { }
            return target;
        }
        return null;
    }

    private async Task<string?> TryRemuxDashAsync(string videoFile, string? audioFile, DownloadTask task, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return null;
        var target = Path.ChangeExtension(videoFile, ".mp4");
        var args = audioFile is null
            ? $"-y -i \"{videoFile}\" -c copy \"{target}\""
            : $"-y -i \"{videoFile}\" -i \"{audioFile}\" -c copy \"{target}\"";
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            Arguments = args,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode == 0 && File.Exists(target))
        {
            try { File.Delete(videoFile); if (audioFile != null) File.Delete(audioFile); } catch { }
            return target;
        }
        return null;
    }

    private static string? FindFfmpeg()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("NOVA_FFMPEG_PATH"),
            Path.Combine(AppContext.BaseDirectory, "tools", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"),
            "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/snap/bin/ffmpeg"
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }

    private void ApplyDuplicatePolicy(DownloadTask task)
    {
        if (string.IsNullOrEmpty(task.FileName)) return;
        var target = task.FullPath;
        if (!File.Exists(target)) return;
        switch (_settings.DuplicatePolicy)
        {
            case "overwrite":
                break;
            case "skip":
                task.FileName = Path.GetFileNameWithoutExtension(task.FileName) + " (1)" + Path.GetExtension(task.FileName);
                break;
            default: // rename
                var n = 1;
                while (File.Exists(Path.Combine(task.SaveDirectory,
                           Path.GetFileNameWithoutExtension(task.FileName) + $" ({n})" + Path.GetExtension(task.FileName))))
                    n++;
                task.FileName = Path.GetFileNameWithoutExtension(task.FileName) + $" ({n})" + Path.GetExtension(task.FileName);
                break;
        }
    }

    internal static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "video";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void SetState(DownloadTask task, DownloadState to, FailureReason? reason = null, string? error = null)
    {
        if (task.State == to) return;
        if (!DownloadStateMachine.CanTransition(task.State, to))
        {
            // Allow healing transitions for scheduler-driven flows
            if (to is DownloadState.Paused && task.State is DownloadState.Cancelled or DownloadState.Failed) { }
            else if (to is DownloadState.Downloading && task.State is DownloadState.Retrying or DownloadState.Interrupted) { }
            else return;
        }
        var from = task.State;
        task.State = to;
        task.UpdatedAt = DateTime.UtcNow;
        if (reason != null) task.Failure = reason.Value;
        if (error != null) task.ErrorMessage = error;
        _repo.UpdateState(task);
        var ev = new TaskStateChanged(task.Id, from, to, task.Failure, task.ErrorMessage,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        StateEvent?.Invoke(ev);
        EventBus.Instance.Publish(ev);
        EmitProgress(task);
    }

    private void EmitProgress(DownloadTask task)
    {
        var p = new TaskProgress
        {
            TaskId = task.Id,
            State = task.State,
            ReceivedBytes = task.ReceivedBytes,
            TotalBytes = task.TotalBytes,
            SpeedBytesPerSec = task.SpeedBytesPerSec,
            ActiveSegments = task.ActiveSegments
        };
        ProgressEvent?.Invoke(p);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _speed.Dispose();
        _http.Dispose();
    }
}

/// <summary>Engine settings access for probe (kept minimal for now).</summary>
public sealed class SettingsBridge
{
}

public sealed class DownloadFailedException(FailureReason reason, string? message)
    : Exception(message ?? reason.ToString())
{
    public FailureReason Reason { get; } = reason;
}

public sealed class RetryRequestedException(string? message) : Exception(message);
