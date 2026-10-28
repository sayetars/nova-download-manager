using System.Collections.Concurrent;
using System.Text.Json;
using Nova.Core;

namespace Nova.Ipc;

/// <summary>
/// Client used by tests, CLI mode and future remote tooling. Tracks pending
/// RPC calls by RequestId and surfaces pushed events through a callback.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly ITransportConnection _conn;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcEnvelope>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private long _requestSeq;
    private Task? _loop;

    public event Action<IpcEnvelope>? EventReceived;

    private IpcClient(ITransportConnection conn) => _conn = conn;

    public static async Task<IpcClient> ConnectAsync(string? endpoint = null, int timeoutMs = 5000, CancellationToken ct = default)
    {
        endpoint ??= TransportFactory.DefaultEndpoint();
        ITransportConnection conn;
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            var name = endpoint.Replace(@"\\.\pipe\", "");
            conn = await new NamedPipeClient(name).ConnectAsync(timeoutMs, ct);
        }
        else
        {
            conn = await UnixDomainSocketClient.ConnectAsync(endpoint, timeoutMs, ct);
        }
        return new IpcClient(conn);
    }

    public Task StartAsync()
    {
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var raw = await Task.Run(() => Frame.Read(_conn.InputStream, _cts.Token), _cts.Token);
                    if (raw is null) break;
                    var env = JsonSerializer.Deserialize<IpcEnvelope>(raw, Json.Options);
                    if (env is null) continue;

                    if (env.RequestId is { } id && _pending.TryRemove(id, out var tcs))
                        tcs.TrySetResult(env);
                    else
                        EventReceived?.Invoke(env);
                }
            }
            catch { _cts.Cancel(); }
            finally
            {
                foreach (var (_, tcs) in _pending) tcs.TrySetCanceled();
                _pending.Clear();
            }
        });
        return Task.CompletedTask;
    }

    public async Task<IpcEnvelope> CallAsync(string type, object? payload = null, int timeoutSec = 30)
    {
        var id = Interlocked.Increment(ref _requestSeq);
        var tcs = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var env = new IpcEnvelope
        {
            V = Json.ProtocolVersion, Type = type, RequestId = id,
            Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload, Json.Options)
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(env, Json.Options);
        await _writeLock.WaitAsync(_cts.Token);
        try { Frame.Write(_conn.OutputStream, bytes); }
        finally { _writeLock.Release(); }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            var reply = await tcs.Task.WaitAsync(cts.Token);
            if (reply.Type == Nova.Core.Protocol.MessageTypes.Error)
                throw new IpcRemoteException(reply.Error ?? "remote error");
            return reply;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        await _conn.DisposeAsync();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}

public sealed class IpcRemoteException(string message) : Exception(message);
