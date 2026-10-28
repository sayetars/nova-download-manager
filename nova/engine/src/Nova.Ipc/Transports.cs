using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Nova.Ipc;

/// <summary>
/// Transport factory: Named Pipes on Windows (the production path used by
/// the Flutter UI), Unix Domain Sockets on Linux (dev/self-test path).
/// Same framing on both — only the OS primitive differs.
/// </summary>
public static class TransportFactory
{
    public const string DefaultPipeName = "nova.download.engine.v1";

    public static string DefaultEndpoint() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? $@"\\.\pipe\{DefaultPipeName}"
        : "/tmp/nova-engine.sock";

    public static ITransportServer CreateServer(string? endpoint = null, CancellationToken ct = default)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new NamedPipeServer(endpoint ?? DefaultPipeName)
            : new UnixDomainSocketServer(endpoint ?? DefaultEndpoint());
}

// ---------------------------------------------------------------------------
// Windows Named Pipes (production)
// ---------------------------------------------------------------------------

public sealed class NamedPipeServer(string pipeName) : ITransportServer
{
    private CancellationTokenSource _cts = new();

    public string Endpoint => $@"\\.\pipe\{pipeName}";

    public async Task<ITransportConnection?> AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                return new StreamConnection(pipe, Endpoint);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return null;
            }
            catch (IOException)
            {
                // client vanished before handshake — keep serving
                await pipe.DisposeAsync();
            }
        }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class NamedPipeClient(string pipeName) : IAsyncDisposable
{
    public async Task<ITransportConnection> ConnectAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(Math.Max(200, timeoutMs - (int)sw.ElapsedMilliseconds), ct);
                return new StreamConnection(pipe, $@"\\.\pipe\{pipeName}");
            }
            catch (TimeoutException) { break; }
            catch (IOException) when (sw.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(120, ct);
            }
        }
        throw new IOException($"engine pipe not reachable: {pipeName}");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ---------------------------------------------------------------------------
// Unix Domain Sockets (Linux dev/self-test)
// ---------------------------------------------------------------------------

public sealed class UnixDomainSocketServer : ITransportServer
{
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly string _path;

    public UnixDomainSocketServer(string path)
    {
        _path = path;
        try { File.Delete(path); } catch { /* first run */ }
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(16);
    }

    public string Endpoint => _path;

    public async Task<ITransportConnection?> AcceptAsync(CancellationToken ct)
    {
        try
        {
            var sock = await _listener.AcceptAsync(ct);
            return new StreamConnection(new NetworkStream(sock, ownsSocket: true), Endpoint);
        }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) when (ct.IsCancellationRequested) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        try { File.Delete(_path); } catch { /* best effort */ }
        await ValueTask.CompletedTask;
    }
}

public sealed class UnixDomainSocketClient
{
    public static async Task<ITransportConnection> ConnectAsync(string path = "/tmp/nova-engine.sock",
        int timeoutMs = 5000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                return new StreamConnection(new NetworkStream(sock, ownsSocket: true), path);
            }
            catch (SocketException) when (sw.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(120, ct);
            }
        }
    }
}

// ---------------------------------------------------------------------------

internal sealed class StreamConnection(Stream stream, string describe) : ITransportConnection
{
    public Stream InputStream => stream;
    public Stream OutputStream => stream;
    public string Describe => describe;

    public async ValueTask DisposeAsync()
    {
        try { await stream.DisposeAsync(); }
        catch { /* socket already dead */ }
    }
}
