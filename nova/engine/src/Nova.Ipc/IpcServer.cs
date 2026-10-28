using System.Collections.Concurrent;
using System.Text.Json;
using Nova.Core;
using Nova.Core.Protocol;

namespace Nova.Ipc;

/// <summary>
/// JSON-RPC-style dispatcher shared by all server transports (UI socket +
/// native messaging host). Handlers are registered per message type; a single
/// serialized dispatch per connection keeps ordering deterministic.
/// </summary>
public sealed class IpcDispatcher
{
    private readonly ConcurrentDictionary<string, Func<IpcEnvelope, CancellationToken, Task<object?>>> _routes = new();
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<object?>>> _voidRoutes = new();

    public void Register<TReq>(string type, Func<TReq, CancellationToken, Task<object?>> handler)
    {
        _routes[type] = async (env, ct) =>
        {
            var req = env.PayloadAs<TReq>() ?? Activator.CreateInstance<TReq>();
            return await handler(req, ct);
        };
    }

    public void Register(string type, Func<CancellationToken, Task<object?>> handler)
        => _voidRoutes[type] = handler;

    public async Task<IpcEnvelope> DispatchAsync(IpcEnvelope envelope, CancellationToken ct)
    {
        try
        {
            if (envelope.V != Json.ProtocolVersion)
                return Err(envelope, $"protocol version mismatch: client={envelope.V} server={Json.ProtocolVersion}");

            if (_routes.TryGetValue(envelope.Type, out var h))
            {
                object? result = await h(envelope, ct);
                return new IpcEnvelope { V = Json.ProtocolVersion, Type = MessageTypes.Ok, RequestId = envelope.RequestId,
                    Payload = result == null ? null : JsonSerializer.SerializeToElement(result, Json.Options) };
            }
            if (_voidRoutes.TryGetValue(envelope.Type, out var h2))
            {
                object? result = await h2(ct);
                return new IpcEnvelope { V = Json.ProtocolVersion, Type = MessageTypes.Ok, RequestId = envelope.RequestId,
                    Payload = result == null ? null : JsonSerializer.SerializeToElement(result, Json.Options) };
            }

            return Err(envelope, $"unknown message type: {envelope.Type}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Err(envelope, ex.Message);
        }
    }

    private static IpcEnvelope Err(IpcEnvelope req, string message) => new()
    {
        V = Json.ProtocolVersion, Type = MessageTypes.Error, RequestId = req.RequestId, Error = message
    };
}

/// <summary>
/// One connected client session: reads frames, dispatches, writes responses,
/// and fans engine events out to the client. Events use RequestId=null so
/// clients distinguish them from RPC replies.
/// </summary>
public sealed class IpcSession : IAsyncDisposable
{
    private readonly ITransportConnection _conn;
    private readonly IpcDispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<IDisposable> _eventSubs = new();
    private Task? _readerLoop;
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];

    public IpcSession(ITransportConnection conn, IpcDispatcher dispatcher)
    {
        _conn = conn;
        _dispatcher = dispatcher;
    }

    public Task RunAsync()
    {
        _readerLoop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var raw = await Task.Run(() => Frame.Read(_conn.InputStream, _cts.Token), _cts.Token);
                    if (raw is null) break;
                    var envelope = JsonSerializer.Deserialize<IpcEnvelope>(raw, Json.Options);
                    if (envelope is null) continue;

                    var reply = await _dispatcher.DispatchAsync(envelope, _cts.Token);
                    await SendAsync(reply);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* client dropped mid-frame */ }
        });
        return _readerLoop;
    }

    /// <summary>Push an event frame to this client (engine -> UI direction).</summary>
    public async Task PushEventAsync(string type, object payload)
    {
        var env = new IpcEnvelope
        {
            V = Json.ProtocolVersion, Type = type,
            Payload = JsonSerializer.SerializeToElement(payload, Json.Options)
        };
        await SendAsync(env);
    }

    private async Task SendAsync(IpcEnvelope env)
    {
        await _writeLock.WaitAsync(_cts.Token);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(env, Json.Options);
            Frame.Write(_conn.OutputStream, bytes);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var s in _eventSubs) s.Dispose();
        _eventSubs.Clear();
        try { if (_readerLoop is not null) await _readerLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* ignore */ }
        await _conn.DisposeAsync();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>Accepts transports, spawns sessions, owns dispatcher lifetime.</summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly IpcDispatcher _dispatcher;
    private readonly List<IpcSession> _sessions = new();
    private readonly object _gate = new();
    private CancellationTokenSource _cts = new();

    public string Endpoint { get; }

    public IpcServer(IpcDispatcher dispatcher, string? endpoint = null)
    {
        _dispatcher = dispatcher;
        Endpoint = endpoint ?? TransportFactory.DefaultEndpoint();
    }

    public async Task ListenAsync(Func<IpcSession, Task>? onClientConnected = null)
    {
        var server = TransportFactory.CreateServer(Endpoint);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var conn = await server.AcceptAsync(_cts.Token);
                if (conn is null) break;

                var session = new IpcSession(conn, _dispatcher);
                lock (_gate) _sessions.Add(session);
                _ = Task.Run(async () =>
                {
                    await session.RunAsync();
                    lock (_gate) _sessions.Remove(session);
                    await session.DisposeAsync();
                });
                if (onClientConnected != null) await onClientConnected(session);
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    public Task BroadcastAsync(string type, object payload)
    {
        IpcSession[] snapshot;
        lock (_gate) snapshot = _sessions.ToArray();
        return Task.WhenAll(snapshot.Select(s => s.PushEventAsync(type, payload)));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        IpcSession[] snapshot;
        lock (_gate) snapshot = _sessions.ToArray();
        foreach (var s in snapshot) await s.DisposeAsync();
        _cts.Dispose();
    }
}
