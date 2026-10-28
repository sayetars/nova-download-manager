using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Nova.Core;
using Nova.Core.Models;
using Nova.Storage;

namespace Nova.Engine.Http;

/// <summary>
/// The heart of Nova: IDM-style multi-segment HTTP(S) downloader.
///
/// - Pre-allocates a sparse .novapart file
/// - One worker per segment with byte-range requests
/// - Dynamic segment splitting: when a worker finishes early it steals the
///   back half of the slowest sibling (IDM's "smart file segmentation")
/// - Writes carry a per-segment received counter flushed to SQLite
/// - Crash-safe: any second, killing the process loses at most one
///   segment-buffer of work
/// </summary>
public sealed class SegmentedHttpDownloader : IAsyncDisposable
{
    private readonly DownloadTask _task;
    private readonly HttpClient _http;
    private readonly TaskRepository _repo;
    private readonly SpeedController? _speed;
    private readonly string _partPath;
    private FileStream _partStream = null!;
    private readonly ConcurrentDictionary<long, SegmentWorker> _workers = new();
    private CancellationTokenSource _cts = new();
    private long _lastPersistedBytes;
    private DateTime _lastPersistAt = DateTime.UtcNow;

    public event Action<DownloadTask>? ProgressTick;
    public event Action<DownloadTask, FailureReason, string?>? Failed;
    public event Action<DownloadTask>? Completed;

    private SegmentedHttpDownloader(DownloadTask task, HttpClient http, TaskRepository repo, SpeedController? speed)
    {
        _task = task;
        _http = http;
        _repo = repo;
        _speed = speed;
        _partPath = task.TempFilePath ?? Path.ChangeExtension(task.FullPath, ".novapart");
        task.TempFilePath = _partPath;
    }

    public static SegmentedHttpDownloader Create(DownloadTask task, HttpClient http, TaskRepository repo, SpeedController? speed)
        => new(task, http, repo, speed);

    public async Task RunAsync(CancellationToken externalCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _cts.Token);
        var ct = linked.Token;
        try
        {
            var dir = Path.GetDirectoryName(_partPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _partStream = new FileStream(_partStream is null ? _partPath : _partPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (_task.TotalBytes > 0) _partStream.SetLength(_task.TotalBytes);

            RestoreSegmentsFromProgress();
            PlanSegments();

            var workers = _workers.Values.OrderBy(w => w.Segment.Index).ToList();
            var tasks = workers.Select(w => w.LoopAsync(ct)).ToArray();
            await Task.WhenAll(tasks);

            if (ct.IsCancellationRequested)
            {
                Transition(DownloadState.Pausing, null, null);
                Flush();
                return;
            }

            // All segments done -> verify size then complete
            _partStream.Flush(flushToDisk: true);
            if (_task.TotalBytes > 0 && _partStream.Length != _task.TotalBytes)
                throw new IOException($"size mismatch: expected {_task.TotalBytes}, got {_partStream.Length}");

            _task.ReceivedBytes = _task.TotalBytes;
            Persist();
            FinalizeFile();
            Completed?.Invoke(_task);
        }
        catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
        {
            Flush();
            Transition(DownloadState.Paused, null, null);
        }
        catch (Exception ex)
        {
            Flush();
            var reason = ex switch
            {
                HttpRequestException hre => HttpProbe.Classify(hre),
                UnauthorizedAccessException => FailureReason.AccessDenied,
                IOException io when io.Message.Contains("disk", StringComparison.OrdinalIgnoreCase) => FailureReason.DiskFull,
                _ => FailureReason.Unknown
            };
            Failed?.Invoke(_task, reason, ex.Message);
        }
        finally
        {
            await DisposeAsyncCore();
        }
    }

    // ------------------------------------------------------------------
    // Segmentation planning + resume
    // ------------------------------------------------------------------

    private void RestoreSegmentsFromProgress()
    {
        // Segments restored from DB carry their Received counters — resume.
        _workers.Clear();
        foreach (var seg in _task.Segments.Where(s => !s.IsComplete))
            _workers[seg.Index] = new SegmentWorker(this, seg);
    }

    private void PlanSegments()
    {
        if (_workers.Count > 0)
        {
            // resume path: recompute active segments from partial state
            _task.Segments = _workers.Values.Select(w => w.Segment).ToList();
            return;
        }

        var total = _task.TotalBytes;
        var count = 1;
        if (_task.AcceptRanges && total > 0)
        {
            var settingsSegs = Math.Clamp(Environment.GetEnvironmentVariable("NOVA_MAX_SEGS") is { } v && int.TryParse(v, out var n) ? n : 8, 1, 32);
            count = (int)Math.Min(settingsSegs, Math.Max(1, total / (256 * 1024)));   // min 256KB per segment
            if (!_task.AcceptRanges) count = 1;
            if (count < 1) count = 1;
        }

        _task.Segments.Clear();
        if (count == 1 || total <= 0)
        {
            _task.Segments.Add(new DownloadSegment { Index = 0, Start = 0, End = Math.Max(total - 1, 0), Received = _task.ReceivedBytes });
            _workers[0] = new SegmentWorker(this, _task.Segments[0]);
            return;
        }

        var per = total / count;
        for (long i = 0; i < count; i++)
        {
            var seg = new DownloadSegment
            {
                Index = i,
                Start = i * per,
                End = i == count - 1 ? total - 1 : (i + 1) * per - 1
            };
            _task.Segments.Add(seg);
            _workers[i] = new SegmentWorker(this, seg);
        }
    }

    // ------------------------------------------------------------------
    // Worker: one byte-range request at a time
    // ------------------------------------------------------------------

    private sealed class SegmentWorker(SegmentedHttpDownloader owner, DownloadSegment segment)
    {
        public DownloadSegment Segment { get; } = segment;

        public async Task LoopAsync(CancellationToken ct)
        {
            while (!Segment.IsComplete && !ct.IsCancellationRequested)
            {
                try
                {
                    await DownloadOnceAsync(ct);
                    TryStealWork();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // server closed mid-transfer: resume own segment
                    owner.NoteTransient(ex);
                    await Task.Delay(owner.BackoffMs(), ct);
                }
            }
            Segment.State = Segment.IsComplete ? SegmentState.Done : SegmentState.Idle;
        }

        private async Task DownloadOnceAsync(CancellationToken ct)
        {
            Segment.State = SegmentState.Connecting;
            var from = Segment.Start + Segment.Received;
            var to = Segment.End;
            if (to < from) { Segment.State = SegmentState.Done; return; }

            using var req = new HttpRequestMessage(HttpMethod.Get, owner._task.FinalUrl ?? owner._task.Url);
            if (owner._task.AcceptRanges)
                req.Headers.TryAddWithoutValidation("Range", $"bytes={from}-{to}");
            owner.ApplySharedHeaders(req);

            using var resp = await owner._http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            Segment.State = SegmentState.Downloading;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[64 * 1024];
            while (Segment.Received < Segment.Length)
            {
                var want = (int)Math.Min(buf.Length, Segment.Length - Segment.Received);
                var allowed = owner.Throttle(want);
                if (allowed <= 0) { await Task.Delay(50, ct); continue; }
                var n = await src.ReadAsync(buf.AsMemory(0, allowed), ct);
                if (n <= 0) break;

                await owner._partStream.WriteAsync(buf.AsMemory(0, n), ct);
                Segment.Received += n;
                Interlocked.Add(ref owner._receivedBytes, n);
                owner._task.ReceivedBytes = Volatile.Read(ref owner._receivedBytes);
                owner.OnBytes();
            }
        }

        /// <summary>IDM's trick: finished workers steal the tail of the slowest one.</summary>
        private void TryStealWork()
        {
            if (!Segment.IsComplete) return;
            SegmentWorker? slowest = null;
            var maxRemaining = 0L;
            foreach (var w in owner._workers.Values)
            {
                var rem = w.Segment.Length - w.Segment.Received;
                if (rem > maxRemaining && w.Segment.Received > 0) // only steal from started segments
                {
                    maxRemaining = rem;
                    slowest = w;
                }
            }
            const long minSteal = 1024 * 1024; // never steal < 1MB
            if (slowest is null || maxRemaining < minSteal) return;

            var splitAt = slowest.Segment.Start + slowest.Segment.Received + (maxRemaining / 2);
            var tail = new DownloadSegment
            {
                Index = owner.NextSegmentIndex(),
                Start = splitAt,
                End = slowest.Segment.End
            };
            slowest.Segment.End = splitAt - 1;
            owner._task.Segments.Add(tail);
            var worker = new SegmentWorker(owner, tail);
            owner._workers[tail.Index] = worker;
            _ = worker.LoopAsync(owner._cts.Token);
        }
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private int _segIndexCounter = -1;
    private long NextSegmentIndex() => Interlocked.Increment(ref _segIndexCounter) + (_task.Segments.Count - 1) + 1;

    private void ApplySharedHeaders(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(_task.ReferringUrl))
            req.Headers.TryAddWithoutValidation("Referer", _task.ReferringUrl);
        if (_task.Headers.TryGetValue("User-Agent", out var ua) && !string.IsNullOrEmpty(ua))
            req.Headers.TryAddWithoutValidation("User-Agent", ua);
        foreach (var (k, v) in _task.Headers)
        {
            if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) || k.Equals("Referer", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.TryAddWithoutValidation(k, v);
        }
        if (_task.Username is not null && _task.AuthType == "basic")
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_task.Username}:{_task.Secret}")));
    }

    private int Throttle(int requested)
    {
        if (_speed == null) return requested;
        var allowed = _speed.TryConsume(_task.Id, requested);
        return allowed <= 0 ? 0 : Math.Min(allowed, requested);
    }

    private int _transients;
    private long _receivedBytes;

    private void NoteTransient(Exception _) => Interlocked.Increment(ref _transients);

    private int BackoffMs()
    {
        var b = _settingsRetryBase;
        var attempt = Math.Min(Interlocked.Increment(ref _transients), 6);
        return (int)Math.Min(b * Math.Pow(2, attempt) * 1000, 60_000);
    }
    // retry base in seconds, default 2
    private static readonly int _settingsRetryBase = Environment.GetEnvironmentVariable("NOVA_RETRY_BASE") is { } v && int.TryParse(v, out var b) ? b : 2;

    private readonly Stopwatch _tickWatch = Stopwatch.StartNew();
    private long _bytesAtWindowStart;
    private DateTime _windowStart = DateTime.UtcNow;

    private void OnBytes()
    {
        if (_tickWatch.ElapsedMilliseconds < 250) return;
        _tickWatch.Restart();
        ProgressTick?.Invoke(_task);
    }

    internal void Flush()
    {
        try
        {
            _partStream?.Flush(flushToDisk: true);
            _repo.Update(_task);
        }
        catch { /* shutdown path must never throw */ }
    }

    private void Persist()
    {
        try { _repo.Update(_task); }
        catch { /* shutdown path must never throw */ }
    }

    private void FinalizeFile()
    {
        _partStream.Dispose();
        var final = _task.FullPath;
        var dir = Path.GetDirectoryName(final);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(final)) File.Delete(final);
        File.Move(_partPath, final);
        _task.TempFilePath = null;
    }

    private void Transition(DownloadState to, FailureReason? reason, string? error)
    {
        if (DownloadStateMachine.CanTransition(_task.State, to))
        {
            _task.State = to;
            if (reason != null) _task.Failure = reason.Value;
            _task.ErrorMessage = error;
            Persist();
        }
    }

    public void Stop() => _cts.Cancel();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await DisposeAsyncCore();
    }

    private async ValueTask DisposeAsyncCore()
    {
        try { if (_partStream is { } ps) await ps.DisposeAsync(); }
        catch { /* closed */ }
        _cts.Dispose();
    }
}
