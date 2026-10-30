using System.IO.Pipes;
using System.Runtime.InteropServices;
using Nova.Domain;
using System.Text.Json;

namespace Nova.Ipc;

/// <summary>Handles one decoded envelope. Returns response (or null for pure notifications).</summary>
public interface IIpcMessageHandler
{
    Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct);
}

/// <summary>
/// Named-pipe IPC server. On Windows the pipe is ACL-restricted to the current user;
/// on Linux System.IO.Pipes maps it to a user-owned Unix domain socket (used by tests/dev only).
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly IIpcMessageHandler _handler;
    private readonly string _expectedToken;
    private readonly string _pipeName;
    private readonly List<IpcConnection> _connections = new();
    private readonly object _connLock = new();
    private CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private bool _started;

    public event Action<IpcConnection>? ClientConnected;
    public event Action<IpcConnection>? ClientDisconnected;

    public IpcServer(IIpcMessageHandler handler, string expectedToken, string? pipeName = null)
    {
        _handler = handler;
        _expectedToken = expectedToken;
        // Injectable for tests/isolated instances; default = engine's well-known name.
        _pipeName = pipeName ?? Protocol.ResolvedPipeName;
    }

    public int ConnectedCount
    {
        get { lock (_connLock) return _connections.Count; }
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        // T-026 (Windows): named pipes have NO kernel backlog — a client connecting
        // while the single accept loop is between instances gets "all pipe instances
        // busy" and times out (Linux UDS queues connects, which is why one loop was
        // enough locally). Run a small pool of concurrent accept loops so several
        // instances are always waiting; Protocol.MaxConnections still caps the total.
        var loops = Math.Min(4, Math.Max(1, Protocol.MaxConnections));
        _acceptLoop = Task.WhenAll(Enumerable.Range(0, loops)
            .Select(_ => Task.Run(() => AcceptLoopAsync(_cts.Token))));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // ACL: current user only — other local accounts cannot connect.
                    // T-026: FullControl, not bare ReadWrite — Windows checks the
                    // FIRST instance's DACL for the CreatePipeInstance right when
                    // spawning ADDITIONAL instances; a ReadWrite-only ACE made every
                    // second Create fail with "Access to the path is denied", so the
                    // engine could serve exactly ONE connection per pipe on Windows
                    // (CI trace: Concurrent_Clients starved, loops retrying forever).
                    // The boundary stays "current user only".
                    var acl = new PipeSecurity();
                    acl.AddAccessRule(new PipeAccessRule(
                        WindowsIdentityGetter.Current, PipeAccessRights.FullControl,
                        System.Security.AccessControl.AccessControlType.Allow));
                    pipe = NamedPipeServerStreamAcl.Create(
                        _pipeName, PipeDirection.InOut,
                        Protocol.MaxConnections, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 0, 0, acl);
                }
                else
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName, PipeDirection.InOut,
                        Protocol.MaxConnections, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }
            }
            catch (Exception ex)
            {
                // Pipe name taken by a previous instance winding down — retry briefly.
                // T-026: this was a silent black hole — now logged so CI can see it.
                EngineLog.Warn($"ipc[{_pipeName}]: instance create failed: {ex.Message}");
                await Task.Delay(500, ct);
                continue;
            }

            try
            {
                EngineLog.Info($"ipc[{_pipeName}]: listening (instances now waiting)");
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch (IOException) { pipe.Dispose(); continue; }

            EngineLog.Info($"ipc[{_pipeName}]: connection accepted");

            var conn = new IpcConnection(pipe, _handler, _expectedToken, OnClientGone);
            lock (_connLock) _connections.Add(conn);
            ClientConnected?.Invoke(conn);
            _ = conn.RunAsync();
        }
    }

    private void OnClientGone(IpcConnection conn)
    {
        lock (_connLock) _connections.Remove(conn);
        ClientDisconnected?.Invoke(conn);
    }

    /// <summary>Push an event envelope to every connected (authenticated) client.</summary>
    public void Broadcast(string type, object payload)
    {
        IpcConnection[] targets;
        lock (_connLock)
            targets = _connections.Where(c => c.Authenticated).ToArray();
        if (targets.Length == 0) return;
        var env = new Envelope { Id = null, Type = type, Payload = JsonSerializer.SerializeToElement(payload, Protocol.Json) };
        foreach (var c in targets) _ = c.TrySendAsync(env);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        IpcConnection[] conns;
        lock (_connLock) conns = _connections.ToArray();
        foreach (var c in conns) await c.DisposeAsync();
        try { if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }
}

internal static class WindowsIdentityGetter
{
    public static System.Security.Principal.SecurityIdentifier Current
        => System.Security.Principal.WindowsIdentity.GetCurrent().User!;
}

/// <summary>One connected client. Owns the read loop, handshake and response writes.</summary>
public sealed class IpcConnection : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly IIpcMessageHandler _handler;
    private readonly string _expectedToken;
    private readonly Action<IpcConnection> _onClosed;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _closed;
    private DateTime _connectedAt = DateTime.UtcNow;

    public bool Authenticated { get; private set; }
    public string? ClientName { get; private set; }

    internal IpcConnection(NamedPipeServerStream pipe, IIpcMessageHandler handler,
        string expectedToken, Action<IpcConnection> onClosed)
    {
        _pipe = pipe;
        _handler = handler;
        _expectedToken = expectedToken;
        _onClosed = onClosed;
    }

    internal async Task RunAsync()
    {
        var ct = CancellationToken.None;
        try
        {
            // --- Handshake: first frame MUST be "hello" with a valid token, within timeout. ---
            using (var handshakeCts = new CancellationTokenSource(Protocol.HandshakeTimeoutMs))
            {
                var first = await Framing.ReadFrameAsync(_pipe, handshakeCts.Token);
                var env = Framing.Decode(first);
                if (env is null || env.Type != "hello")
                {
                    await SafeSend(Envelope.Error(env?.Id, "bad_handshake", "protocol",
                        "First message must be a versioned hello."), CancellationToken.None);
                    return;
                }
                if (!string.Equals(env.Token, _expectedToken, StringComparison.Ordinal))
                {
                    await SafeSend(Envelope.Error(env.Id, "auth_failed", "protocol",
                        "Invalid IPC token."), CancellationToken.None);
                    return;
                }
                var hello = env.PayloadAs<HelloRequest>();
                if (hello is { ProtocolVersion: > Protocol.Version })
                {
                    await SafeSend(Envelope.Error(env.Id, "version_unsupported", "protocol",
                        $"Server speaks nipc/{Protocol.Version}; client wants {hello.ProtocolVersion}."), CancellationToken.None);
                    return;
                }
                ClientName = hello.Client;
                Authenticated = true;
                await SafeSend(Envelope.Ok(env.Id, new { server_version = "1.0.0", protocol_version = Protocol.Version }), CancellationToken.None);
            }

            // --- Main loop. Protocol violations never crash the server. ---
            while (Authenticated && !_closed && _pipe.IsConnected)
            {
                byte[]? frame;
                try { frame = await Framing.ReadFrameAsync(_pipe, ct); }
                catch (IpcFramingException)
                {
                    Authenticated = false;
                    break;
                }
                if (frame is null) break; // client closed

                Envelope? env;
                try { env = Framing.Decode(frame); }
                catch (JsonException) { env = null; }

                if (env is null)
                {
                    await SafeSend(Envelope.Error(null, "bad_envelope", "protocol", "Undecodable frame or unsupported version."), ct);
                    continue;
                }
                if (env.Type == "ping")
                {
                    await SafeSend(Envelope.Ok(env.Id, new { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }), ct);
                    continue;
                }
                if (env.Type == "bye") break;

                try
                {
                    var resp = await _handler.HandleAsync(env, ct);
                    if (resp is not null) await SafeSend(resp, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    await SafeSend(Envelope.Error(env.Id, "internal_error", "unknown",
                        "Request failed; see engine log."), ct);
                    EngineLog.Warn($"handler error for {env.Type}: {ex.Message}");
                }
            }
        }
        catch (IOException) { /* disconnected mid-frame — normal */ }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            EngineLog.Warn($"connection error: {ex.Message}");
        }
        finally
        {
            _onClosed(this);
            // T-026 (Windows): the connection owns its pipe instance — dispose it here,
            // not only from the server's connection list. RunAsync returning removes the
            // connection from that list first, so without this the NamedPipeServerStream
            // handle leaks on every handshake rejection/drop; on Windows leaked instances
            // exhaust maxNumberOfServerInstances and deadlock later connects.
            try { await _pipe.DisposeAsync(); } catch { /* already gone */ }
            _writeLock.Dispose();
        }
    }

    internal async Task<bool> TrySendAsync(Envelope env)
    {
        if (_closed || !Authenticated || !_pipe.IsConnected) return false;
        return await SafeSend(env, CancellationToken.None);
    }

    private async Task<bool> SafeSend(Envelope env, CancellationToken ct)
    {
        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                var bytes = Framing.Encode(env);
                await Framing.WriteFrameAsync(_pipe, bytes, ct);
                return true;
            }
            finally { _writeLock.Release(); }
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        Authenticated = false;
        try
        {
            if (_pipe.IsConnected)
            {
                await TrySendAsync(new Envelope { Type = "bye" });
            }
        }
        catch { /* ignore */ }
        // Idempotent: RunAsync's finally may already have disposed both.
        try { await _pipe.DisposeAsync(); } catch { /* already gone */ }
        try { _writeLock.Dispose(); } catch (ObjectDisposedException) { }
    }
}

