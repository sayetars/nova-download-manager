using System.IO.Pipes;

namespace Nova.Ipc;

/// <summary>
/// IPC client for local trusted components (Nova.BrowserHost, tests, CLI tools).
/// Connects to the engine's named pipe, handshakes, and offers request/response + event stream.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private NamedPipeClientStream _pipe = null!;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<Envelope>> _pending = new();
    private CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private bool _connected;
    private int _seq;

    public bool IsConnected => _connected;
    public event Action<Envelope>? EventReceived;
    public event Action? Disconnected;

    public async Task<bool> ConnectAsync(string token, string clientName, int timeoutMs = 4000, string? pipeName = null)
    {
        _pipe = new NamedPipeClientStream(".", pipeName ?? Protocol.ResolvedPipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(timeoutMs);
        await Framing.WriteFrameAsync(_pipe,
            Framing.Encode(new Envelope
            {
                Type = "hello",
                Token = token,
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(
                    new HelloRequest { Client = clientName, ProtocolVersion = Protocol.Version },
                    Protocol.Json),
            }), _cts.Token);

        var first = await Framing.ReadFrameAsync(_pipe, _cts.Token);
        var env = Framing.Decode(first);
        if (env is null || env.Type != "ok")
            throw new IpcFramingException($"Handshake rejected: {env?.Type ?? "closed"}");
        _connected = true;
        _readLoop = Task.Run(ReadLoopAsync);
        return true;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (_connected && _pipe.IsConnected)
            {
                var frame = await Framing.ReadFrameAsync(_pipe, _cts.Token);
                if (frame is null) break;
                var env = Framing.Decode(frame);
                if (env is null) continue;
                if (env.Id is not null)
                {
                    TaskCompletionSource<Envelope>? tcs;
                    lock (_pending)
                        _pending.TryGetValue(env.Id, out tcs);
                    if (tcs is not null)
                    {
                        lock (_pending) _pending.Remove(env.Id);
                        tcs.TrySetResult(env);
                        continue;
                    }
                }
                if (env.Type != "ok" && env.Type != "error" && env.Type != "bye" && env.Type != "ping")
                    EventReceived?.Invoke(env);
                else if (env.Type == "bye") break;
            }
        }
        catch { /* connection lost */ }
        finally
        {
            _connected = false;
            Disconnected?.Invoke();
            lock (_pending)
            {
                foreach (var kv in _pending) kv.Value.TrySetCanceled();
                _pending.Clear();
            }
        }
    }

    public async Task<Envelope> RequestAsync(string type, object? payload = null, int timeoutMs = Protocol.RequestTimeoutMs)
    {
        if (!_connected) throw new InvalidOperationException("IPC client not connected");
        var id = $"c{Interlocked.Increment(ref _seq)}";
        var env = new Envelope
        {
            Id = id,
            Type = type,
            Payload = payload is null ? null
                : System.Text.Json.JsonSerializer.SerializeToElement(payload, Protocol.Json),
        };
        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        try
        {
            await Framing.WriteFrameAsync(_pipe, Framing.Encode(env), _cts.Token);
        }
        catch
        {
            lock (_pending) _pending.Remove(id);
            throw;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(timeoutMs);
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            throw new TimeoutException($"IPC request '{type}' timed out after {timeoutMs}ms");
        }
    }

    /// <summary>Request that also unwraps errors into exceptions for terse callers.</summary>
    public async Task<T?> CallAsync<T>(string type, object? payload = null, int timeoutMs = Protocol.RequestTimeoutMs) where T : class
    {
        var resp = await RequestAsync(type, payload, timeoutMs);
        if (resp.Type == "error")
        {
            var err = resp.PayloadAs<IpcError>();
            throw new NovaIpcException(err?.Code ?? "error", err?.Message ?? "unknown error");
        }
        return resp.PayloadAs<T>();
    }

    public async ValueTask DisposeAsync()
    {
        _connected = false;
        try
        {
            if (_pipe is { IsConnected: true })
                await Framing.WriteFrameAsync(_pipe,
                    Framing.Encode(new Envelope { Type = "bye" }), CancellationToken.None);
        }
        catch { /* ignore */ }
        _cts.Cancel();
        if (_pipe is not null) await _pipe.DisposeAsync();
        if (_readLoop is not null) { try { await _readLoop; } catch { } }
        _cts.Dispose();
        _lock.Dispose();
    }
}

public sealed class NovaIpcException : Exception
{
    public string Code { get; }
    public NovaIpcException(string code, string message) : base(message) => Code = code;
}
