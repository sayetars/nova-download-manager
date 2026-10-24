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
    private readonly Action<long>? _quotaHook;
    private readonly Action<DownloadTask, IReadOnlyList<DownloadSegment>>? _publishLive;
    private readonly Func<string, string?>? _settings;
    private readonly ServerProfileService? _profiles;
    private readonly Security.PgpKeyringService? _pgpKeys;
    private long _lastLivePublishMs;

    public TaskRunner(HttpClient http, HttpProber prober, IDownloadTaskRepository tasks,
        ISegmentRepository segs, EngineEventBus events, IMediaPipeline? media,
        Func<RateLimiter?> globalLimiter, Func<RateLimiter?> taskLimiter,
        Action<DownloadTask, IReadOnlyList<DownloadSegment>> persistPlan,
        Action<DownloadTask> persistTask, Action<DownloadTask> persistProgress,
        LoginVault? vault = null, Antivirus? antivirus = null, string? tempRoot = null,
        Action<long>? quotaHook = null,
        Action<DownloadTask, IReadOnlyList<DownloadSegment>>? publishLive = null,
        Func<string, string?>? settings = null,
        ServerProfileService? profiles = null,
        Security.PgpKeyringService? pgpKeys = null)
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
        _quotaHook = quotaHook;
        _publishLive = publishLive;
        _settings = settings;
        _profiles = profiles;
        _pgpKeys = pgpKeys;
    }

    public SpeedMeter Speed { get; } = new();

    private string? Setting(string key) => _settings?.Invoke(key);

    /// <summary>
    /// v1.3.0 multi-source pool (aria2/FDM parity): the file is fetched from the primary
    /// URL AND the mirrors simultaneously — workers round-robin across healthy sources.
    /// </summary>
    private sealed class SourcePool
    {
        private readonly List<string> _items;
        private readonly HashSet<string> _dead = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _strikes = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private int _cursor;

        public SourcePool(IEnumerable<string> urls)
        {
            _items = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public int Count { get { lock (_lock) return _items.Count; } }

        public string Next()
        {
            lock (_lock)
            {
                var alive = _items.Where(u => !_dead.Contains(u)).ToList();
                if (alive.Count == 0) return _items[0];   // last resort: try primary again
                var url = alive[Math.Abs(_cursor) % alive.Count];
                _cursor++;
                return url;
            }
        }

        public void Report(string url, bool ok)
        {
            lock (_lock)
            {
                if (ok) { _strikes.Remove(url); return; }
                var n = _strikes.GetValueOrDefault(url) + 1;
                _strikes[url] = n;
                if (n >= 3 && _items.Count > 1 && _items.Count(u => !_dead.Contains(u)) > 1)
                    _dead.Add(url);
            }
        }
    }

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

            // ---- BitTorrent (v1.4.0): magnet: or local .torrent — full swarm pipeline -----
            if (Torrent.TorrentTask.IsTorrentSource(task.EffectiveUrl ?? task.Url))
            {
                await RunTorrentAsync(task, ct);
                return;
            }

            // ---- analyze / revalidate -------------------------------------
            var probeSw = System.Diagnostics.Stopwatch.StartNew();
            var probe = await _prober.ProbeAsync(task.EffectiveUrl ?? task.Url, task.Referer, ct, task);
            _profiles?.RecordTtfb(task.EffectiveUrl ?? task.Url, probeSw.ElapsedMilliseconds);
            _profiles?.RecordResult(task.EffectiveUrl ?? task.Url, true);

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

            // ---- v1.3.0: Metalink (XML + RFC 6249) + checksum sidecars --------
            if (task.Kind == DownloadKind.File)
            {
                var probeWithTask = task;
                var metalinkApplied = await TryApplyMetalinkAsync(task, probe, ct);
                if (metalinkApplied)
                {
                    // re-probe the real file behind the metalink
                    probe = await _prober.ProbeAsync(task.EffectiveUrl ?? task.Url, task.Referer, ct, probeWithTask);
                    task.EffectiveUrl = probe.FinalUrl;
                    task.Etag = probe.Etag;
                    task.LastModified = probe.LastModified;
                    task.MimeType = probe.ContentType;
                    if (probe.ContentLength > 0) task.TotalBytes = probe.ContentLength;
                }
                else
                {
                    ApplyMetalinkHttp(task, probe);
                    await TryApplySidecarAsync(task, ct);
                }
                _persistTask(task);
            }

            // ---- dispatch ---------------------------------------------------
            // FTP / FTPS (IDM parity), SFTP (v1.3.0), WebDAV scheme rewrite (v1.3.0)
            var scheme = (task.EffectiveUrl ?? task.Url).Split(':')[0].ToLowerInvariant();
            if (scheme is "dav" or "davs")
            {
                var davUrl = task.EffectiveUrl ?? task.Url;
                var rewritten = (scheme == "dav" ? "http" : "https") + davUrl[scheme.Length..];
                EngineLog.Info($"webdav: {davUrl} → {rewritten}");
                task.Url = rewritten;
                task.EffectiveUrl = null;
            }
            if (scheme is "ftp" or "ftps")
            {
                await RunFtpAsync(task, ct);
                return;
            }
            if (scheme is "sftp")
            {
                await RunSftpAsync(task, ct);
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
                _publishLive?.Invoke(task, segments);
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
            // v1.3.0: workers round-robin across the source pool (primary + mirrors) simultaneously.
            var workLock = new object();
            var pool = segments.ToList();
            var claimed = new HashSet<long>();   // shared: one owner per slice at a time
            var sources = new SourcePool(BuildSources(task));
            var workers = Enumerable.Range(0, Math.Max(1, Math.Min(concurrency, Math.Max(1, pool.Count))))
                .Select(_ => Task.Run(() => SegmentWorkerAsync(task, pool, sources, dataFile, workLock, claimed, ct), ct))
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

            // ---- verify (v1.3.0: piece repair → algo-qualified whole-file hash) ----
            var pieces = PieceHashInfo.Parse(task.PieceHashes);
            if (pieces is not null)
            {
                SetState(task, DownloadState.Verifying, null);
                _persistTask(task);
                await RepairPiecesAsync(task, sources, dataFile, pieces, ct);
            }

            var hashReq = task.ExpectedHash;
            if (string.IsNullOrWhiteSpace(hashReq) && !string.IsNullOrEmpty(task.ExpectedSha256))
                hashReq = "sha256:" + task.ExpectedSha256;

            if (!string.IsNullOrWhiteSpace(hashReq))
            {
                var parsed = Hashing.ParseQualified(hashReq);
                if (parsed is null)
                {
                    EngineLog.Warn($"verify[{task.TaskKey}]: unsupported hash format — skipped");
                }
                else
                {
                    SetState(task, DownloadState.Verifying, null);
                    _persistTask(task);
                    var (ok, actual) = await Hashing.VerifyFileAsync(dataFile.TempPath, parsed.Value.Algo, parsed.Value.Hex, ct);
                    if (!ok)
                    {
                        _tasks.UpdateValidation(task.Id, ValidationStatus.Failed, actual);
                        task.Validation = ValidationStatus.Failed;
                        Fail(task, new NovaError("hash_mismatch", ErrorCategory.HashMismatch,
                            $"The downloaded file does not match the expected {parsed.Value.Algo.ToUpperInvariant()} hash."));
                        dataFile.AbandonTemp();
                        return;
                    }
                    _tasks.UpdateValidation(task.Id, ValidationStatus.Passed, actual);
                    task.Validation = ValidationStatus.Passed;
                }
            }

            // ---- v1.5.0 (G26): PGP signature verification (metalink / sidecar) ----
            if (await VerifyPgpAsync(task, dataFile, ct)) return;

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

            // ---- v1.3.0: auto-extract + convert post-steps -------------------
            if (Setting("extract.auto") == "true" && ArchiveTools.IsArchive(finalPath))
            {
                SetState(task, DownloadState.Verifying, null);
                _persistTask(task);
                var dest = Path.Combine(task.DestinationDir, Path.GetFileNameWithoutExtension(finalPath) + "_files");
                var xr = ArchiveTools.Extract(finalPath, dest, ct);
                EngineLog.Info($"extract[{task.TaskKey}]: {xr.Tool}: {xr.Message}");
                if (xr.Ok && Setting("extract.delete_archive") == "true")
                {
                    try { File.Delete(finalPath); } catch { /* best effort */ }
                }
            }
            if (!string.IsNullOrEmpty(task.ConvertPreset) && ArchiveTools.IsValidPreset(task.ConvertPreset))
            {
                SetState(task, DownloadState.Verifying, null);
                _persistTask(task);
                var cr = ArchiveTools.Convert(finalPath, task.ConvertPreset, Setting("ffmpeg.path"), ct);
                EngineLog.Info($"convert[{task.TaskKey}]: {cr.Message}");
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
                _quotaHook?.Invoke(n);
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
    private async Task SegmentWorkerAsync(DownloadTask task, List<DownloadSegment> pool, SourcePool sources,
        DownloadDataFile file, object workLock, HashSet<long> claimed, CancellationToken ct)
    {
        var limiters = new[] { _globalLimiter(), _taskLimiter() }.Where(l => l is not null).ToArray()!;
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            DownloadSegment? seg;
            lock (workLock)
            {
                var pending = pool.Where(s => !IsSliceDone(task, s) && !claimed.Contains(s.Id));
                seg = task.Sequential
                    ? pending.OrderBy(s => s.Start).FirstOrDefault()      // inorder (aria2 stream-piece parity)
                    : pending.OrderByDescending(Remaining).FirstOrDefault();
                if (seg is null) return;   // all slices complete or owned
                claimed.Add(seg.Id);
            }
            try
            {
                if (await DownloadSliceAsync(task, pool, sources, seg, file, limiters, ct))
                    consecutiveFailures = 0;
            }
            catch (NovaHttpException hex) when (sources.Count > 1 && hex.Category != ErrorCategory.HashMismatch)
            {
                // multi-source resilience: let the remaining mirrors absorb this failure.
                consecutiveFailures++;
                EngineLog.Warn($"source failure ({hex.Code}) — {consecutiveFailures} consecutive");
                if (consecutiveFailures > sources.Count * 2) throw;
            }
            finally
            {
                lock (workLock) claimed.Remove(seg.Id);
                if (!task.Sequential) TryDynamicSplit(task, pool, workLock);
            }
        }
    }

    /// <summary>HTTP(S) sources for multi-source download (primary first, then mirrors).</summary>
    private static List<string> BuildSources(DownloadTask task)
    {
        var list = new List<string> { task.EffectiveUrl ?? task.Url };
        list.AddRange(task.Mirrors);
        return list.Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri)
                               && uri.Scheme is "http" or "https").ToList();
    }

    /// <summary>Cookies + per-task extra headers on every task HTTP request (v1.3.0).</summary>
    internal static void ApplyTaskHeaders(HttpRequestMessage req, DownloadTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.Cookies))
            req.Headers.TryAddWithoutValidation("Cookie", task.Cookies.Trim());
        if (!string.IsNullOrWhiteSpace(task.ExtraHeaders))
        {
            try
            {
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(task.ExtraHeaders,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (dict is not null)
                    foreach (var (k, v) in dict)
                        if (!string.IsNullOrWhiteSpace(k) && v is not null)
                            req.Headers.TryAddWithoutValidation(k, v);
            }
            catch { /* malformed header JSON is rejected at add-time */ }
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
            _publishLive?.Invoke(task, pool);
            EngineLog.Info($"dynamic split: task {task.TaskKey} now {pool.Count} slices");
        }
    }

    /// <summary>Download one slice from the source pool. Returns true when the slice finished.</summary>
    private async Task<bool> DownloadSliceAsync(DownloadTask task, List<DownloadSegment> pool, SourcePool sources, DownloadSegment seg,
        DownloadDataFile file, RateLimiter[] limiters, CancellationToken ct)
    {
        var vaultTried = false;
        while (!ct.IsCancellationRequested)
        {
            var source = sources.Next();
            var knownLength = task.TotalBytes >= 0 && seg.End >= 0 && seg.Length > 0;
            if (knownLength && seg.IsComplete) break;

            var sliceStart = seg.Start + seg.Received;
            var want = knownLength
                ? Math.Min(seg.Length - seg.Received, 8L * 1024 * 1024)
                : -1L; // unknown total: stream until EOF

            using var req = new HttpRequestMessage(HttpMethod.Get, source);
            if (knownLength)
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(sliceStart, sliceStart + want - 1);
            else if (seg.Received > 0)
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(sliceStart, null);
            HttpProber.AddDefaultHeaders(req, task.Referer);
            ApplyTaskHeaders(req, task);

            HttpResponseMessage resp;
            var sliceSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                resp = await _prober.SendGuardedAsync(req, ct);
                sources.Report(source, true);
                _profiles?.RecordTtfb(source, sliceSw.ElapsedMilliseconds);
                _profiles?.RecordResult(source, true);
            }
            catch (NovaHttpException)
            {
                sources.Report(source, false);
                _profiles?.RecordResult(source, false);
                throw;
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized && !vaultTried && _vault is not null)
                {
                    vaultTried = true;
                    var cred = _vault.Find(task.Url);
                    if (cred is not null)
                    {
                        // retry once with vault credentials attached (Basic scheme)
                        using var retryReq = new HttpRequestMessage(HttpMethod.Get, source);
                        if (knownLength)
                            retryReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(sliceStart, sliceStart + want - 1);
                        else if (seg.Received > 0)
                            retryReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(sliceStart, null);
                        HttpProber.AddDefaultHeaders(retryReq, task.Referer);
                        ApplyTaskHeaders(retryReq, task);
                        retryReq.Headers.TryAddWithoutValidation("Authorization", new TaskCredentials(cred.UserName, cred.Password).BasicHeader());
                        using var retryResp = await _prober.SendGuardedAsync(retryReq, ct);
                        await DrainSliceAsync(task, pool, seg, retryResp, file, knownLength, want, limiters, ct);
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
                {
                    _profiles?.RecordResult(source, false);
                    throw NovaHttpException.FromStatus(resp.StatusCode, source);
                }

                var speedStart = seg.Received;
                await DrainSliceAsync(task, pool, seg, resp, file, knownLength, want, limiters, ct);
                // v1.5.0 (G27): throughput sample for the per-server profile
                var gained = seg.Received - speedStart;
                if (gained > 0)
                    _profiles?.RecordSpeed(source, gained, sliceSw.ElapsedMilliseconds);
            }

            if (!knownLength && seg.End >= 0) break; // reached EOF
            if (knownLength && seg.IsComplete) break;
        }
        return knownSliceDone(task, seg);
    }

    private static bool knownSliceDone(DownloadTask task, DownloadSegment seg)
        => task.TotalBytes < 0 || seg.End < 0 || seg.Length <= 0 ? false : seg.IsComplete;

    private async Task DrainSliceAsync(DownloadTask task, List<DownloadSegment> pool, DownloadSegment seg, HttpResponseMessage resp,
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
            _quotaHook?.Invoke(take);
            if (_publishLive is not null)
            {
                var nowTicks = Environment.TickCount64;
                if (nowTicks - Interlocked.Read(ref _lastLivePublishMs) >= 500)
                {
                    Interlocked.Exchange(ref _lastLivePublishMs, nowTicks);
                    _publishLive(task, pool);
                }
            }
            _events.MaybeProgress(task, Speed.SpeedBps, 500);
            if (knownLength && seg.IsComplete) break;
        }
    }

    // ---- v1.3.0: Metalink + sidecar + piece repair + SFTP ---------------------

    /// <summary>Expands a Metalink task (XML doc / .meta4 / metalink content-type) into mirrors + hashes.</summary>
    private async Task<bool> TryApplyMetalinkAsync(DownloadTask task, HttpProbeResult probe, CancellationToken ct)
    {
        try
        {
            if (!Metalink.LooksLikeMetalink(task.EffectiveUrl ?? task.Url, probe.ContentType)) return false;
            if (task.ReceivedBytes > 0) return false;   // resuming the meta itself — leave alone

            using var req = new HttpRequestMessage(HttpMethod.Get, task.EffectiveUrl ?? task.Url);
            HttpProber.AddDefaultHeaders(req, task.Referer);
            ApplyTaskHeaders(req, task);
            using var resp = await _prober.SendGuardedAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (body.Length > 4 * 1024 * 1024) return false;
            var meta = Metalink.ParseXml(body);
            if (meta is null) return false;

            // v1.5.0 (G27): metalink priority is authoritative; mirrors in the SAME
            // priority bucket are ordered fastest-first using the per-server profiles.
            var ordered = new List<string>();
            foreach (var bucket in meta.Mirrors.GroupBy(m => m.Priority).OrderBy(g => g.Key))
                ordered.AddRange(_profiles?.OrderMirrors(bucket.Select(m => m.Url)) ?? bucket.Select(m => m.Url));
            if (ordered.Count == 0) return false;

            task.Url = ordered[0];
            task.MirrorUrls = string.Join('\n', ordered.Skip(1));
            task.EffectiveUrl = null;
            task.FileName = FilenamePolicy.SanitizeFileName(meta.FileName);
            if (meta.Size > 0) task.TotalBytes = meta.Size;
            var algo = new[] { "sha256", "sha1", "md5", "crc32" }.FirstOrDefault(a => meta.Hashes.ContainsKey(a));
            if (algo is not null) task.ExpectedHash = $"{algo}:{meta.Hashes[algo]}";
            if (meta.Pieces is not null) task.PieceHashes = meta.Pieces.ToJson();
            if (meta.SignaturePgp.Length > 0)
            {
                task.PgpSignature = Security.PgpVerifier.NormalizeSignaturePayload(meta.SignaturePgp);
                task.PgpStatus = Domain.PgpStatus.Pending;
            }
            var maxConn = meta.Mirrors.OrderBy(m => m.Priority).Select(m => m.MaxConnections).FirstOrDefault(v => v.HasValue);
            if (maxConn is { } mc && mc > 0) task.MaxConnections = Math.Clamp(mc, 1, 32);
            EngineLog.Info($"metalink[{task.TaskKey}]: {ordered.Count} mirror(s), hash={algo ?? "none"}, pgp={(task.PgpSignature.Length > 0 ? "yes" : "no")}");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            EngineLog.Warn($"metalink[{task.TaskKey}]: {ex.Message} — falling back to plain download");
            return false;
        }
    }

    /// <summary>RFC 6249 Metalink/HTTP: mirrors from Link rel=duplicate + Digest instance hashes.</summary>
    private void ApplyMetalinkHttp(DownloadTask task, HttpProbeResult probe)
    {
        try
        {
            if (probe.LinkHeaders.Count == 0 && probe.DigestHeader is null) return;
            var parsed = Metalink.ParseMetalinkHttp(probe.LinkHeaders, probe.DigestHeader);
            if (parsed is null) return;
            var (mirrors, hashes) = parsed.Value;
            if (mirrors.Count > 0)
            {
                var merged = task.Mirrors
                    .Concat(mirrors.OrderBy(m => m.Priority).Select(m => m.Url))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(u => !string.Equals(u, task.EffectiveUrl ?? task.Url, StringComparison.OrdinalIgnoreCase));
                task.MirrorUrls = string.Join('\n', merged);
                EngineLog.Info($"metalink/http[{task.TaskKey}]: +{mirrors.Count} mirror(s)");
            }
            var algo = new[] { "sha256", "sha1", "md5", "crc32" }.FirstOrDefault(a => hashes.ContainsKey(a));
            if (algo is not null && task.ExpectedHash is null && string.IsNullOrEmpty(task.ExpectedSha256))
                task.ExpectedHash = $"{algo}:{hashes[algo]}";
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"metalink/http[{task.TaskKey}]: {ex.Message}");
        }
    }

    /// <summary>Best-effort checksum sidecar discovery (.sha256/.md5/.sfv next to the file URL).</summary>
    private async Task TryApplySidecarAsync(DownloadTask task, CancellationToken ct)
    {
        try
        {
            if (Setting("engine.sidecar_verify") == "false") return;
            if (task.ExpectedHash is not null || !string.IsNullOrEmpty(task.ExpectedSha256)) return;
            var url = task.EffectiveUrl ?? task.Url;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            var found = await Metalink.FetchSidecarAsync(_http, url, task.FileName, ct);
            if (found is not null)
            {
                task.ExpectedHash = $"{found.Value.Algo}:{found.Value.Hex}";
                EngineLog.Info($"sidecar[{task.TaskKey}]: {found.Value.Algo} discovered");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* sidecar probing is best-effort by design */ }
    }

    /// <summary>
    /// v1.5.0 (G26): PGP detached-signature verification after download, before finalize.
    /// Signature sources: metalink &lt;signature type="pgp"&gt; (captured at analyze) and
    /// best-effort .sig/.asc sidecars. Policy: engine.metalink_pgp = off | warn | require.
    /// Returns true when the task was terminally failed (caller must stop).
    /// </summary>
    private async Task<bool> VerifyPgpAsync(DownloadTask task, DownloadDataFile file, CancellationToken ct)
    {
        var policy = Security.PgpPolicyExtensions.Parse(Setting(Security.PgpKeyringService.PolicySetting));
        if (policy == Security.PgpPolicy.Off) return false;

        if (task.PgpSignature.Length == 0)
        {
            await TryFetchPgpSidecarAsync(task, ct);
            if (task.PgpSignature.Length == 0)
            {
                if (policy == Security.PgpPolicy.Require)
                {
                    Fail(task, new NovaError("pgp_required", ErrorCategory.Pgp,
                        "No PGP signature was found, but the current policy requires one."), retryable: false);
                    return true;
                }
                return false; // warn-policy + no signature → nothing to do
            }
        }

        SetState(task, DownloadState.Verifying, null);
        _persistTask(task);

        Security.PgpVerifyOutcome outcome;
        try
        {
            var data = await File.ReadAllBytesAsync(file.TempPath, ct);
            var keyring = _pgpKeys?.ArmoredKeys() ?? new List<string>();
            outcome = Security.PgpVerifier.VerifyDetached(data, task.PgpSignature, keyring);
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"pgp[{task.TaskKey}]: verify error: {ex.Message}");
            task.PgpStatus = Domain.PgpStatus.Error;
            _persistTask(task);
            if (policy == Security.PgpPolicy.Require)
            {
                Fail(task, new NovaError("pgp_error", ErrorCategory.Pgp,
                    "The PGP signature could not be processed."), retryable: false);
                return true;
            }
            return false;
        }

        switch (outcome)
        {
            case Security.PgpVerifyOutcome.Verified:
                task.PgpStatus = Domain.PgpStatus.Verified;
                EngineLog.Info($"pgp[{task.TaskKey}]: signature VERIFIED");
                break;
            case Security.PgpVerifyOutcome.NoKey:
                task.PgpStatus = Domain.PgpStatus.NoKey;
                EngineLog.Warn($"pgp[{task.TaskKey}]: signature present but key not in keyring");
                if (policy == Security.PgpPolicy.Require)
                {
                    Fail(task, new NovaError("pgp_no_key", ErrorCategory.Pgp,
                        "The file is signed with a key that is not in your PGP keyring."), retryable: false);
                    return true;
                }
                break;
            default:
                task.PgpStatus = Domain.PgpStatus.Failed;
                EngineLog.Warn($"pgp[{task.TaskKey}]: signature INVALID");
                if (policy == Security.PgpPolicy.Require)
                {
                    Fail(task, new NovaError("pgp_failed", ErrorCategory.Pgp,
                        "The PGP signature does not match this file — it may have been tampered with."),
                        retryable: false);
                    return true;
                }
                _events.Emit(task, DownloadState.Verifying, "PGP signature check FAILED (warn policy)");
                break;
        }
        _persistTask(task);
        return false;
    }

    /// <summary>Best-effort fetch of a detached signature sidecar (url + ".sig" / ".asc").</summary>
    private async Task TryFetchPgpSidecarAsync(DownloadTask task, CancellationToken ct)
    {
        try
        {
            if (Setting("engine.sidecar_verify") == "false") return;
            var url = task.EffectiveUrl ?? task.Url;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var ext in new[] { ".sig", ".asc" })
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url.Split('#')[0] + ext);
                HttpProber.AddDefaultHeaders(req, task.Referer);
                using var resp = await _prober.SendGuardedAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (body.Length == 0 || body.Length > 1024 * 1024) continue;
                if (!body.Contains("PGP SIGNATURE", StringComparison.OrdinalIgnoreCase) &&
                    !body.Contains("PGP MESSAGE", StringComparison.OrdinalIgnoreCase)) continue;
                task.PgpSignature = Security.PgpVerifier.NormalizeSignaturePayload(body);
                task.PgpStatus = Domain.PgpStatus.Pending;
                EngineLog.Info($"pgp-sidecar[{task.TaskKey}]: {ext} signature discovered");
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* best-effort by design */ }
    }

    /// <summary>Metalink piece-hash verification + in-place repair (aria2 --check-integrity parity).</summary>
    private async Task RepairPiecesAsync(DownloadTask task, SourcePool sources, DownloadDataFile file,
        PieceHashInfo pieces, CancellationToken ct)
    {
        for (var pass = 1; pass <= 2; pass++)
        {
            var bad = await FindBadPiecesAsync(file.TempPath, pieces, task.TotalBytes, ct);
            if (bad.Count == 0) return;
            EngineLog.Warn($"piece verify[{task.TaskKey}]: {bad.Count} damaged piece(s) — repair pass {pass}");
            foreach (var idx in bad)
            {
                if (ct.IsCancellationRequested) return;
                var start = (long)idx * pieces.PieceSize;
                var length = Math.Min(pieces.PieceSize, task.TotalBytes - start);
                if (length <= 0) continue;
                var buf = await FetchRangeAsync(sources.Next(), start, length, task, ct);
                await file.WriteAsync(start, buf, ct);
            }
        }

        var remaining = await FindBadPiecesAsync(file.TempPath, pieces, task.TotalBytes, ct);
        if (remaining.Count > 0)
            throw new NovaHttpException("piece_verify_failed", ErrorCategory.HashMismatch,
                "Some parts of the file could not be repaired after verification.", retryable: false);
    }

    private static async Task<List<int>> FindBadPiecesAsync(string path, PieceHashInfo pieces, long totalBytes, CancellationToken ct)
    {
        var bad = new List<int>();
        if (!File.Exists(path)) return bad;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buf = new byte[pieces.PieceSize];
        for (var i = 0; i < pieces.Hashes.Count; i++)
        {
            var pieceStart = (long)i * pieces.PieceSize;
            if (totalBytes > 0 && pieceStart >= totalBytes) break;
            var read = 0;
            while (read < buf.Length)
            {
                var n = await fs.ReadAsync(buf.AsMemory(read), ct);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) break;
            var actual = await Hashing.ComputeAsync(new MemoryStream(buf, 0, read, writable: false), pieces.Algo, ct);
            if (!string.Equals(actual, pieces.Hashes[i], StringComparison.OrdinalIgnoreCase))
                bad.Add(i);
        }
        return bad;
    }

    private async Task<byte[]> FetchRangeAsync(string url, long start, long length, DownloadTask task, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, start + length - 1);
        HttpProber.AddDefaultHeaders(req, task.Referer);
        ApplyTaskHeaders(req, task);
        using var resp = await _prober.SendGuardedAsync(req, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
            throw NovaHttpException.FromStatus(resp.StatusCode, url);
        var data = new byte[length];
        var read = 0;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        while (read < length)
        {
            var n = await stream.ReadAsync(data.AsMemory(read), ct);
            if (n <= 0) break;
            read += n;
        }
        if (read < length)
            throw new NovaHttpException("short_read", ErrorCategory.Network,
                "Repair fetch returned fewer bytes than requested.");
        return data;
    }

    /// <summary>SFTP path (v1.3.0): SSH.NET multi-connection segmented download.</summary>
    private async Task RunSftpAsync(DownloadTask task, CancellationToken ct)
    {
        SetState(task, DownloadState.Downloading, null);
        _persistTask(task);
        var creds = _vault?.Find(task.Url) is { } v ? new TaskCredentials(v.UserName, v.Password) : null;
        var finalPath = await SftpTask.RunAsync(task, creds, (delta, rate) =>
        {
            if (delta > 0) { Interlocked.Add(ref _runReceived, delta); Speed.Add(delta); _quotaHook?.Invoke(delta); }
            _events.MaybeProgress(task, rate > 0 ? rate : Speed.SpeedBps, 500);
        }, ct);

        task.FileName = Path.GetFileName(finalPath);
        task.CompletedAt = DateTimeOffset.UtcNow;
        SetState(task, DownloadState.Completed, null);
        _persistTask(task);
        _events.Completed(task, finalPath);
    }

    /// <summary>BitTorrent path (v1.4.0): metadata → swarm download → optional seeding.</summary>
    private async Task RunTorrentAsync(DownloadTask task, CancellationToken ct)
    {
        SetState(task, DownloadState.Downloading, null);
        _persistTask(task);
        var finalPath = await Torrent.TorrentTask.RunAsync(task, (delta, rate) =>
        {
            if (delta > 0)
            {
                Interlocked.Add(ref _runReceived, delta);
                Speed.Add(delta);
                task.ReceivedBytes += delta;
                _quotaHook?.Invoke(delta);
            }
            _events.MaybeProgress(task, rate > 0 ? rate : Speed.SpeedBps, 500);
        }, ct, Setting);

        task.FileName = Path.GetFileName(finalPath);
        task.CompletedAt = DateTimeOffset.UtcNow;
        SetState(task, DownloadState.Completed, null);
        _persistTask(task);
        _events.Completed(task, finalPath);
    }

    /// <summary>FTP/FTPS path: shares the crash-safe temp/final flow + progress events.</summary>
    private async Task RunFtpAsync(DownloadTask task, CancellationToken ct)
    {
        SetState(task, DownloadState.Downloading, null);
        _persistTask(task);
        var creds = _vault?.Find(task.Url) is { } v ? new TaskCredentials(v.UserName, v.Password) : null;
        var finalPath = await FtpTask.RunAsync(task, creds, (delta, rate) =>
        {
            if (delta > 0) { Interlocked.Add(ref _runReceived, delta); Speed.Add(delta); _quotaHook?.Invoke(delta); }
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
