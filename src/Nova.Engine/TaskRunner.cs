using System.Net;
using System.Security.Cryptography;
using Nova.Domain;
using Nova.Persistence;

namespace Nova.Engine;

/// <summary>
/// Runs ONE file task through its lifecycle:
/// analyze → (re)plan segments → parallel segment workers (dynamic re-split) → verify → finalize.
/// Also owns: FTP/FTPS dispatch, mirror failover, vault-auth retry, antivirus scan.
/// All errors are structured NovaError; UI-safe strings only.
/// </summary>
public sealed class TaskRunner
{
    private const int ChunkSize = 64 * 1024;
    private const long DynamicSplitMinBytes = 4 * 1024 * 1024;   // IDM-style: re-split only meaningful chunks

    private readonly HttpClient _http;
    private readonly HttpProber _prober;
    private readonly IDownloadTaskRepository _tasks;
    private readonly ISegmentRepository _segs;
    private readonly EngineEventBus _events;
    private readonly IMediaPipeline? _media;
    private readonly Func<RateLimiter?> _globalLimiter;
    private readonly Func<RateLimiter?> _taskLimiter;
    private readonly Action<DownloadTask, IReadOnlyList<DownloadSegment>> _persistPlan;
    private readonly Action<DownloadTask> _persistTask;
    private readonly Action<DownloadTask> _persistProgress;
    private readonly LoginVault? _vault;
    private readonly Antivirus? _antivirus;
    private readonly string? _tempRoot;

    public TaskRunner(HttpClient http, HttpProber prober, IDownloadTaskRepository tasks,
        ISegmentRepository segs, EngineEventBus events, IMediaPipeline? media,
        Func<RateLimiter?> globalLimiter, Func<RateLimiter?> taskLimiter,
        Action<DownloadTask, IReadOnlyList<DownloadSegment>> persistPlan,
        Action<DownloadTask> persistTask, Action<DownloadTask> persistProgress,
        LoginVault? vault = null, Antivirus? antivirus = null, string? tempRoot = null)
    {
        _http = http;
        _prober = prober;
        _tasks = tasks;
        _segs = segs;
        _events = events;
        _media = media;
        _globalLimiter = globalLimiter;
        _taskLimiter = taskLimiter;
        _persistPlan = persistPlan;
        _persistTask = persistTask;
        _persistProgress = persistProgress;
        _vault = vault;
        _antivirus = antivirus;
        _tempRoot = tempRoot;
    }

    public SpeedMeter Speed { get; } = new();

    /// <summary>User pause (keep data). Engine hard-stop and user cancel arrive via RunAsync tokens.</summary>
    public CancellationTokenSource Pause { get; } = new();

    // Per-run atomic counters (segment workers are concurrent).
    private long _runReceived;

    public async Task RunAsync(DownloadTask task, CancellationToken cancelToken, CancellationToken hardStop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Pause.Token, cancelToken, hardStop);
        var ct = linked.Token;
        _runReceived = 0;
        Speed.Reset();
        try
        {
            task.StartedAt ??= DateTimeOffset.UtcNow;
            SetState(task, DownloadState.Analyzing, null);
            _persistTask(task);

            // ---- analyze / revalidate -------------------------------------
            var probe = await _prober.ProbeAsync(task.EffectiveUrl ?? task.Url, task.Referer, ct);

            bool validatorChanged = false;
            if (task.ReceivedBytes > 0)
            {
                if (!string.IsNullOrEmpty(probe.Etag) && !string.IsNullOrEmpty(task.Etag))
                    validatorChanged = !string.Equals(task.Etag, probe.Etag, StringComparison.OrdinalIgnoreCase);
                else if (!string.IsNullOrEmpty(probe.LastModified) && !string.IsNullOrEmpty(task.LastModified))
                    validatorChanged = !string.Equals(task.LastModified, probe.LastModified, StringComparison.OrdinalIgnoreCase);
            }

            var oldReceived = task.ReceivedBytes;
            task.EffectiveUrl = probe.FinalUrl;
            task.Etag = probe.Etag;
            task.LastModified = probe.LastModified;
            task.MimeType = probe.ContentType;
            task.TotalBytes = probe.ContentLength;
            if (string.IsNullOrEmpty(task.FileName) || task.FileName == "download")
                task.FileName = probe.SuggestedFileName ?? FilenamePolicy.FromUrl(probe.FinalUrl) ?? "download";
            if (string.IsNullOrEmpty(task.Category) || task.Category == "other")
                task.Category = Disk.GuessCategory(task.FileName, probe.ContentType);

            // ---- dispatch ---------------------------------------------------
            // FTP / FTPS (IDM parity)
            var scheme = (task.EffectiveUrl ?? task.Url).Split(':')[0].ToLowerInvariant();
            if (scheme is "ftp" or "ftps")
            {
                await RunFtpAsync(task, ct);
                return;
            }

            if (task.Kind != DownloadKind.File)
            {
                if (_media is null)
                {
                    Fail(task, new NovaError("media_unavailable", ErrorCategory.Media,
                        "Media support is not available in this build."));
                    return;
                }
                await RunMediaAsync(task, ct);
                return;
            }

            // ---- plan (build fresh or resume persisted) --------------------
            var segments = _segs.GetForTask(task.Id);
            var planMatches = segments.Count > 0 && probe.ContentLength >= 0
                              && segments.Sum(s => s.End + 1) == probe.ContentLength;

            if (oldReceived > 0 && (validatorChanged || !planMatches))
            {
                task.ReceivedBytes = 0;
                _segs.DeleteForTask(task.Id);
                segments = new List<DownloadSegment>();
                _events.Emit(task, DownloadState.Interrupted,
                    validatorChanged ? "The server file changed; restarting the download." : "Download restarted.");
            }
            else if (segments.Count == 0)
            {
                task.ReceivedBytes = 0;
            }

            if (segments.Count == 0)
            {
                // No range support (or tiny file) → single connection regardless of configured max.
                if (!probe.AcceptRanges) task.MaxConnections = 1;
                segments = BuildPlan(task);
                _persistPlan(task, segments);
            }
            else
            {
                // resuming: progress counters must include previously-persisted bytes
                _runReceived = task.ReceivedBytes;
            }

            // disk space gate
            if (probe.ContentLength > 0 && !Disk.HasSpaceFor(task.DestinationDir, probe.ContentLength, out _))
            {
                Fail(task, new NovaError("disk_full", ErrorCategory.DiskFull,
                    "Not enough free space on the destination drive."), retryable: true);
                return;
            }

            // ---- download ---------------------------------------------------
            SetState(task, DownloadState.Downloading, null);
            _persistTask(task);

            await using var dataFile = DownloadDataFile.OpenOrCreate(task.FinalPath,
                probe.ContentLength > 0 ? probe.ContentLength : 0, _tempRoot);

            var concurrency = Math.Clamp(task.MaxConnections, 1, 32);
            // Dynamic IDM-style worker pool: N workers pull the largest remaining slice,
            // and on completion split the largest remaining slice when useful.
            var workLock = new object();
            var pool = segments.ToList();
            var claimed = new HashSet<long>();   // shared: one owner per slice at a time
            var workers = Enumerable.Range(0, Math.Max(1, Math.Min(concurrency, Math.Max(1, pool.Count))))
                .Select(_ => Task.Run(() => SegmentWorkerAsync(task, pool, dataFile, workLock, claimed, ct), ct))
                .ToArray();

            var lastPersist = Environment.TickCount64;
            using var progressTimer = new System.Timers.Timer(750);
            progressTimer.Elapsed += (_, _) =>
            {
                var now = Environment.TickCount64;
                if (now - lastPersist < 1000) return;
                lastPersist = now;
                task.ReceivedBytes = Interlocked.Read(ref _runReceived);
                _persistProgress(task);
                _events.Progress(task, Speed.SpeedBps);
            };
            progressTimer.Start();
            try
            {
                await Task.WhenAll(workers);
            }
            finally
            {
                progressTimer.Stop();
                progressTimer.Dispose();
                task.ReceivedBytes = Interlocked.Read(ref _runReceived);
                _persistProgress(task);
            }

            if (ct.IsCancellationRequested)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    SetState(task, DownloadState.Cancelled, null);
                    _persistTask(task);
                    TryAbandonTemp(task);
                }
                else if (Pause.IsCancellationRequested)
                {
                    SetState(task, DownloadState.Paused, null);
                    _persistTask(task);
                }
                else
                {
                    // engine shutting down — recovery pass will pick this up
                    SetState(task, DownloadState.Interrupted, null);
                }
                return;
            }

            // unknown-length: size = what actually arrived
            if (task.TotalBytes < 0)
            {
                task.TotalBytes = Interlocked.Read(ref _runReceived);
                task.ReceivedBytes = task.TotalBytes;
            }

            // ---- verify ----------------------------------------------------
            if (!string.IsNullOrEmpty(task.ExpectedSha256))
            {
                SetState(task, DownloadState.Verifying, null);
                _persistTask(task);
                var actual = await Sha256FileAsync(dataFile.TempPath, ct);
                if (!string.Equals(actual, task.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    _tasks.UpdateValidation(task.Id, ValidationStatus.Failed, actual);
                    task.Validation = ValidationStatus.Failed;
                    Fail(task, new NovaError("hash_mismatch", ErrorCategory.HashMismatch,
                        "The downloaded file does not match the expected SHA-256."));
                    dataFile.AbandonTemp();
                    return;
                }
                _tasks.UpdateValidation(task.Id, ValidationStatus.Passed, actual);
                task.Validation = ValidationStatus.Passed;
            }

            // ---- finalize --------------------------------------------------
            var finalPath = await dataFile.FinalizeAsync();
            task.FileName = Path.GetFileName(finalPath);   // reflect unique "(2)" name if deduped

            // ---- antivirus (IDM parity: scan on completion when enabled) -----
            if (_antivirus is { Enabled: true })
            {
                SetState(task, DownloadState.Verifying, null);
                _persistTask(task);
                var scan = _antivirus.Scan(finalPath);
                if (!scan.Clean)
                {
                    _tasks.UpdateValidation(task.Id, ValidationStatus.Failed, null);
                    Fail(task, new NovaError("antivirus", ErrorCategory.FileIo,
                        $"The antivirus scanner flagged this file: {scan.Details}"), retryable: false);
                    return;
                }
            }

            task.CompletedAt = DateTimeOffset.UtcNow;
            SetState(task, DownloadState.Completed, null);
            _persistTask(task);
            _events.Completed(task, finalPath);
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
        {
            SetState(task, DownloadState.Cancelled, null);
            _persistTask(task);
            TryAbandonTemp(task);
        }
        catch (OperationCanceledException) when (Pause.IsCancellationRequested)
        {
            task.ReceivedBytes = Interlocked.Read(ref _runReceived);
            SetState(task, DownloadState.Paused, null);
            _persistTask(task);
        }
        catch (OperationCanceledException) when (hardStop.IsCancellationRequested)
        {
            task.ReceivedBytes = Interlocked.Read(ref _runReceived);
            SetState(task, DownloadState.Interrupted, null);
        }
        catch (MediaProcessException mpe)
        {
            Fail(task, new NovaError(mpe.Code, mpe.Category, mpe.Message), retryable: mpe.Retryable);
        }
        catch (NovaHttpException hex)
        {
            Fail(task, new NovaError(hex.Code, hex.Category, hex.Message, hex.Details), retryable: hex.Retryable);
        }
        catch (IOException io) when (IsDiskFull(io))
        {
            Fail(task, new NovaError("disk_full", ErrorCategory.DiskFull,
                "The disk ran out of space while writing."), retryable: true);
        }
        catch (IOException io) when (!cancelToken.IsCancellationRequested &&
                                      !Pause.IsCancellationRequested && !hardStop.IsCancellationRequested)
        {
            // transient transport break (connection reset etc.)
            EngineLog.Warn($"io[{task.TaskKey}]: {io.Message}");
            Fail(task, new NovaError("io_error", ErrorCategory.FileIo,
                "The connection was interrupted while writing."), retryable: true);
        }
        catch (Exception ex) when (cancelToken.IsCancellationRequested ||
                                    Pause.IsCancellationRequested || hardStop.IsCancellationRequested)
        {
            // exceptions WRAPPED around cancellation (server aborts socket first)
            if (cancelToken.IsCancellationRequested)
            {
                SetState(task, DownloadState.Cancelled, null);
                _persistTask(task);
                TryAbandonTemp(task);
            }
            else if (Pause.IsCancellationRequested)
            {
                task.ReceivedBytes = Interlocked.Read(ref _runReceived);
                SetState(task, DownloadState.Paused, null);
                _persistTask(task);
            }
            else
            {
                task.ReceivedBytes = Interlocked.Read(ref _runReceived);
                SetState(task, DownloadState.Interrupted, null);
            }
        }
        catch (IOException io) when (IsDiskFull(io))
        {
            Fail(task, new NovaError("disk_full", ErrorCategory.DiskFull,
                "The disk ran out of space while writing."), retryable: true);
        }
        catch (UnauthorizedAccessException)
        {
            Fail(task, new NovaError("access_denied", ErrorCategory.AccessDenied,
                "Nova does not have permission to write to the destination folder."));
        }
        catch (Exception ex)
        {
            EngineLog.Error($"runner[{task.TaskKey}] unexpected: {ex.GetType().Name}: {ex.Message}");
            Fail(task, new NovaError("io_error", ErrorCategory.FileIo,
                "The download could not be written to disk."), retryable: true);
        }
    }

    private async Task RunMediaAsync(DownloadTask task, CancellationToken ct)
    {
        // sensible output names: master.m3u8 → master.ts, manifest.mpd → manifest.mp4
        if (task.Kind == DownloadKind.Hls && task.FileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            task.FileName = task.FileName[..^5] + ".ts";
        else if (task.Kind == DownloadKind.Dash && task.FileName.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            task.FileName = task.FileName[..^4] + ".mp4";

        SetState(task, DownloadState.Downloading, null);
        _persistTask(task);
        var tempPath = task.FinalPath + DownloadDataFile.TempSuffix;
        var ctx = new DownloadTaskContext
        {
            Task = task,
            Http = _http,
            GlobalLimiter = _globalLimiter,
            TaskLimiter = _taskLimiter(),
            OnBytes = n =>
            {
                Interlocked.Add(ref _runReceived, n);
                Speed.Add(n);
            },
            OnSegments = (done, total) =>
            {
                task.SegmentsDone = done;
                task.SegmentsTotal = total;
                task.ReceivedBytes = Interlocked.Read(ref _runReceived);
                _persistProgress(task);
                _events.MaybeProgress(task, Speed.SpeedBps, 500);
            },
            TempOutputPath = tempPath,
            Finalize = () =>
            {
                var target = FilenamePolicy.MakeUniquePath(task.FinalPath);
                File.Move(tempPath, target, overwrite: false);
                return target;
            },
        };

        await _media!.RunMediaTaskAsync(ctx, ct);

        if (ct.IsCancellationRequested)
        {
            if (cancelIsRequested(task)) { /* handled by caller path */ }
            return;
        }

        // verify (if user asked for a hash) + finalize handled inside pipeline Finalize()
        task.CompletedAt = DateTimeOffset.UtcNow;
        SetState(task, DownloadState.Completed, null);
        _persistTask(task);
    }

    private static bool cancelIsRequested(DownloadTask t) => t.State == DownloadState.Cancelled;

    private static void TryAbandonTemp(DownloadTask task)
    {
        try
        {
            var temp = task.FinalPath + DownloadDataFile.TempSuffix;
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Worker-pool loop (IDM dynamic segmentation): claims the slice with the
    /// most remaining bytes, downloads it, then tries to re-split the largest
    /// remaining slice so fast workers keep all connections saturated.
    /// A slice is owned by exactly one worker at a time (claim set).
    /// </summary>
    private async Task SegmentWorkerAsync(DownloadTask task, List<DownloadSegment> pool,
        DownloadDataFile file, object workLock, HashSet<long> claimed, CancellationToken ct)
    {
        var limiters = new[] { _globalLimiter(), _taskLimiter() }.Where(l => l is not null).ToArray()!;

        while (!ct.IsCancellationRequested)
        {
            DownloadSegment? seg;
            lock (workLock)
            {
                seg = pool.Where(s => !IsSliceDone(task, s) && !claimed.Contains(s.Id))
                    .OrderByDescending(Remaining)
                    .FirstOrDefault();
                if (seg is null) return;   // all slices complete or owned
                claimed.Add(seg.Id);
            }
            try
            {
                await DownloadSliceAsync(task, seg, file, limiters, ct);
            }
            finally
            {
                lock (workLock) claimed.Remove(seg.Id);
                TryDynamicSplit(task, pool, workLock);
            }
        }
    }

    private static long Remaining(DownloadSegment s)
        => s.End >= 0 ? Math.Max(0, s.Length - s.Received) : long.MaxValue / 4;

    private static bool IsSliceDone(DownloadTask task, DownloadSegment s)
        => task.TotalBytes >= 0 && s.End >= 0 ? s.Received >= s.Length : false;

    /// <summary>Split the largest remaining slice when more connections are allowed and the piece is big enough.
    /// The in-flight owner of the split slice stops at its new (shrunk) end via the DrainSliceAsync room clamp.</summary>
    private void TryDynamicSplit(DownloadTask task, List<DownloadSegment> pool, object workLock)
    {
        lock (workLock)
        {
            if (pool.Count >= task.MaxConnections) return;
            var biggest = pool.Where(s => !IsSliceDone(task, s)).OrderByDescending(Remaining).FirstOrDefault();
            if (biggest is null) return;
            var rem = Remaining(biggest);
            if (rem < 2 * DynamicSplitMinBytes) return;

            var mid = biggest.Start + biggest.Received + rem / 2;
            var fresh = new DownloadSegment
            {
                TaskId = task.Id,
                Index = pool.Count,
                Start = mid,
                End = biggest.End,
                Received = 0,
            };
            biggest.End = mid - 1;
            pool.Add(fresh);
            task.SegmentCount = pool.Count;
            try { _persistPlan(task, pool); } catch { /* persistence is retried on progress flush */ }
            EngineLog.Info($"dynamic split: task {task.TaskKey} now {pool.Count} slices");
        }
    }

    private async Task DownloadSliceAsync(DownloadTask task, DownloadSegment seg,
        DownloadDataFile file, RateLimiter[] limiters, CancellationToken ct)
    {
        var vaultTried = false;
        while (!ct.IsCancellationRequested)
        {
            var knownLength = task.TotalBytes >= 0 && seg.End >= 0 && seg.Length > 0;
            if (knownLength && seg.IsComplete) break;

            var sliceStart = seg.Start + seg.Received;
            var want = knownLength
                ? Math.Min(seg.Length - seg.Received, 8L * 1024 * 1024)
                : -1L; // unknown total: stream until EOF

            using var req = new HttpRequestMessage(HttpMethod.Get, task.EffectiveUrl ?? task.Url);
            if (knownLength)
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(sliceStart, sliceStart + want - 1);
            else if (seg.Received > 0)
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(sliceStart, null);
            HttpProber.AddDefaultHeaders(req, task.Referer);

            // mirror rotation: on hard failures the outer retry path switches task.EffectiveUrl (see MirrorFailover below)
            using var resp = await _prober.SendGuardedAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && !vaultTried && _vault is not null)
            {
                vaultTried = true;
                var cred = _vault.Find(task.Url);
                if (cred is not null)
                {
                    // retry once with vault credentials attached (Basic scheme)
                    using var retryReq = new HttpRequestMessage(HttpMethod.Get, task.EffectiveUrl ?? task.Url);
                    if (knownLength)
                        retryReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(sliceStart, sliceStart + want - 1);
                    else if (seg.Received > 0)
                        retryReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(sliceStart, null);
                    HttpProber.AddDefaultHeaders(retryReq, task.Referer);
                    retryReq.Headers.TryAddWithoutValidation("Authorization", new TaskCredentials(cred.UserName, cred.Password).BasicHeader());
                    using var retryResp = await _prober.SendGuardedAsync(retryReq, ct);
                    await DrainSliceAsync(task, seg, retryResp, file, knownLength, want, limiters, ct);
                    if (knownLength && seg.IsComplete) break;
                    continue;
                }
            }

            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Might mean the file is actually already complete for this segment → verify size.
                if (task.TotalBytes >= 0 && sliceStart == task.TotalBytes) { seg.Received = seg.Length; break; }
                throw new NovaHttpException("http_416", ErrorCategory.Http,
                    "The server refused the requested byte range.", task.Url);
            }

            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
                throw NovaHttpException.FromStatus(resp.StatusCode, task.Url);

            await DrainSliceAsync(task, seg, resp, file, knownLength, want, limiters, ct);

            if (!knownLength && seg.End >= 0) break; // reached EOF
            if (knownLength && seg.IsComplete) break;
        }
    }

    private async Task DrainSliceAsync(DownloadTask task, DownloadSegment seg, HttpResponseMessage resp,
        DownloadDataFile file, bool knownLength, long want, RateLimiter[] limiters, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[ChunkSize];
        while (!ct.IsCancellationRequested)
        {
            foreach (var l in limiters) await l!.WaitAsync(ChunkSize, ct);

            var n = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), ct);
            if (n <= 0)
            {
                if (want < 0 && !knownLength)
                {
                    // EOF on unknown-length stream → segment is done
                    seg.End = seg.Start + seg.Received - 1;
                }
                break;
            }

            // room clamp: a dynamic split may have shrunk this slice mid-flight —
            // discard stale-range bytes instead of writing past the new end.
            var writeAt = seg.Start + seg.Received;
            int take;
            if (knownLength)
            {
                var room = seg.End - writeAt + 1;
                if (room <= 0) break;
                take = (int)Math.Min(n, room);
            }
            else take = n;

            await file.WriteAsync(writeAt, buffer.AsMemory(0, take), ct);
            seg.Received += take;
            Interlocked.Add(ref _runReceived, take);
            Speed.Add(take);
            _events.MaybeProgress(task, Speed.SpeedBps, 500);
            if (knownLength && seg.IsComplete) break;
        }
    }

    /// <summary>FTP/FTPS path: shares the crash-safe temp/final flow + progress events.</summary>
    private async Task RunFtpAsync(DownloadTask task, CancellationToken ct)
    {
        SetState(task, DownloadState.Downloading, null);
        _persistTask(task);
        var creds = _vault?.Find(task.Url) is { } v ? new TaskCredentials(v.UserName, v.Password) : null;
        var finalPath = await FtpTask.RunAsync(task, creds, (delta, rate) =>
        {
            if (delta > 0) { Interlocked.Add(ref _runReceived, delta); Speed.Add(delta); }
            _events.MaybeProgress(task, rate > 0 ? rate : Speed.SpeedBps, 500);
        }, ct);

        task.FileName = Path.GetFileName(finalPath);
        task.CompletedAt = DateTimeOffset.UtcNow;
        SetState(task, DownloadState.Completed, null);
        _persistTask(task);
        _events.Completed(task, finalPath);
    }

    private List<DownloadSegment> BuildPlan(DownloadTask task)
    {
        var plan = new List<DownloadSegment>();
        var size = task.TotalBytes;
        if (size > 0 && task.MaxConnections > 1 && size >= 2 * 1024 * 1024)
        {
            var n = (int)Math.Min(task.MaxConnections, Math.Max(1, size / (512 * 1024)));
            var baseLen = size / n;
            long off = 0;
            for (var i = 0; i < n; i++)
            {
                var len = (i == n - 1) ? size - off : baseLen;
                plan.Add(new DownloadSegment { TaskId = task.Id, Index = i, Start = off, End = off + len - 1, Received = 0 });
                off += len;
            }
            task.SegmentCount = n;
        }
        else
        {
            plan.Add(new DownloadSegment { TaskId = task.Id, Index = 0, Start = 0, End = size - 1, Received = 0 });
            task.SegmentCount = 1;
        }
        task.SegmentsDone = 0;
        return plan;
    }

    private void SetState(DownloadTask task, DownloadState s, string? note)
    {
        task.State = s;
        _events.Emit(task, s, note);
    }

    private void Fail(DownloadTask task, NovaError error, bool retryable = false)
    {
        task.ErrorCode = error.Code;
        task.ErrorMessage = error.Message;
        task.ErrorCategory = error.Category;
        SetState(task, DownloadState.Failed, error.Message);
        _persistTask(task);
        _events.Failed(task, error, retryable);
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x27 or 0x70;

    internal static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
