using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace Nova.DownloadEngine;

/// <summary>
/// IPC transport: Named Pipes on Windows (native, production path),
/// Unix domain sockets on Linux/macOS (self-test path). Same JSON-line framing on both.
/// </summary>
public static class Ipc
{
    public const string PipeName = "nova-engine-v1";

    public static string UnixSocketPath => Path.Combine(Path.GetTempPath(), "nova-engine-v1.sock");

    public static async Task ServeAsync(
        Func<string, CancellationToken, Task<string>> onMessage,
        CancellationToken ct)
    {
        Socket? listener = null;
        if (!OperatingSystem.IsWindows())
        {
            try { File.Delete(UnixSocketPath); } catch { /* best effort */ }
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(UnixSocketPath));
            listener.Listen(16);
        }

        while (!ct.IsCancellationRequested)
        {
            Stream client;
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                client = pipe;
            }
            else
            {
                var sock = await listener!.AcceptAsync(ct);
                client = new NetworkStream(sock, ownsSocket: true);
            }
            _ = Task.Run(() => HandleClientAsync(client, onMessage, ct));
        }
        listener?.Dispose();
    }

    static async Task HandleClientAsync(
        Stream stream,
        Func<string, CancellationToken, Task<string>> onMessage,
        CancellationToken ct)
    {
        try
        {
            using var _ = stream;
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            { AutoFlush = true };

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;                       // client disconnected
                if (string.IsNullOrWhiteSpace(line)) continue;
                var reply = await onMessage(line, ct);
                await writer.WriteLineAsync(reply);
            }
        }
        catch
        {
            // connection-level error — drop the client
        }
    }
}

/// <summary>
/// Minimal one-shot client used by the CLI to talk to a running 'serve' process.
/// </summary>
public static class IpcClient
{
    public static async Task<string> SendAsync(string jsonLine)
    {
        if (OperatingSystem.IsWindows())
        {
            using var pipe = new NamedPipeClientStream(".", Ipc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000);
            return await RoundTripAsync(pipe, jsonLine);
        }
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(Ipc.UnixSocketPath));
        using var ns = new NetworkStream(sock, ownsSocket: true);
        return await RoundTripAsync(ns, jsonLine);
    }

    static async Task<string> RoundTripAsync(Stream s, string jsonLine)
    {
        using var writer = new StreamWriter(s, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(s, Encoding.UTF8, false, 1024, leaveOpen: true);
        await writer.WriteLineAsync(jsonLine);
        return await reader.ReadLineAsync() ?? "";
    }
}
