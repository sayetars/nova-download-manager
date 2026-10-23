using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Nova.Domain;
using Nova.Ipc;

namespace Nova.BrowserHost;

/// <summary>
/// Native Messaging Host.
///
/// Contract (Chrome/Brave/Firefox):
///   - stdin/stdout carry messages framed as [4-byte little-endian length][JSON UTF-8].
///   - stdout is SACRED: only protocol frames ever go there (logs → stderr).
///   - Host process is spawned per-extension by the browser; it exits when stdin closes.
///
/// Responsibilities: validate extension origin + message shape, forward allowed calls to the
/// Nova engine over its named pipe, return engine responses. It can launch the engine on
/// demand (user level). No shell execution, no arbitrary commands — ever.
/// </summary>
public static class Program
{
    private const int MaxNativeMessageBytes = 1024 * 1024;
    private const int EngineStartTimeoutMs = 8000;

    // Allowed extension IDs are configured in browserhost.config.json next to the exe.
    private static HashSet<string> _allowedExtensionIds = new(StringComparer.OrdinalIgnoreCase);
    private static string _enginePath = "";
    private static IpcClient? _ipc;

    public static async Task<int> Main(string[] args)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "browserhost.config.json");
        LoadConfig(configPath);

        // Browser passes the calling extension's origin as the first argument,
        // e.g. chrome-extension://<id>/ or moz-extension://<uuid>/.
        var origin = args.FirstOrDefault() ?? "";
        if (!IsAllowedOrigin(origin))
        {
            // Not a recognized caller → speak nothing on stdout and die.
            EngineLog.Error($"Rejected origin: '{origin}'");
            return 2;
        }

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        try
        {
            while (true)
            {
                var frame = await ReadNativeFrameAsync(stdin);
                if (frame is null) break; // browser closed stdin → exit quietly

                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(frame); }
                catch (JsonException) { await WriteNativeAsync(stdout, JsonSerializer.SerializeToUtf8Bytes(Error("bad_json", "Malformed message."))); continue; }

                var response = await DispatchAsync(doc.RootElement);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                await WriteNativeAsync(stdout, bytes);
            }
            return 0;
        }
        catch (IOException)
        {
            return 0; // broken pipe when browser closes — normal shutdown path
        }
    }

    private static async Task<object> DispatchAsync(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        var payload = root.TryGetProperty("payload", out var p) ? p : default;

        switch (type)
        {
            case "ping":
                return Ok(new { engine = "up" });

            case "hello":
                // extension announces itself; we try engine connection
                var connected = await EnsureEngineAsync();
                if (!connected)
                    return Error("engine_down", "Nova engine is not running.");
                return Ok(new { engine = "up", protocol = Protocol.Version });

            case "add_download":
            case "add_downloads":
            case "media_probe":
            case "engine_status":
                return await ForwardToEngineAsync(type!, payload);

            case "task_status":
                return await ForwardToEngineAsync("get_task", payload);

            default:
                return Error("unknown_type", $"Unsupported message '{type}'.");
        }
    }

    /// <summary>Forwards an allowed request to the engine, returning its raw response.</summary>
    private static async Task<object> ForwardToEngineAsync(string type, JsonElement payload)
    {
        if (!await EnsureEngineAsync())
            return Error("engine_down", "Nova engine is not running.");

        try
        {
            var env = new Envelope { Id = "bh" + Guid.NewGuid().ToString("N")[..8], Type = type, Payload = payload };
            var resp = await _ipc!.RequestAsync(type, payload.ValueKind == JsonValueKind.Undefined ? null
                : JsonDocument.Parse(payload.GetRawText()).RootElement);
            _ = env;
            if (resp.Type == "error")
            {
                var err = resp.PayloadAs<IpcError>();
                return Error(err?.Code ?? "engine_error", err?.Message ?? "Engine rejected the request.");
            }
            if (resp.Payload is null) return Ok(null);
            // Wrap engine payloads in the extension-side {ok, data} envelope (consistent contract).
            return Ok(JsonDocument.Parse(resp.Payload.Value.GetRawText()).RootElement);
        }
        catch (TimeoutException)
        {
            return Error("engine_timeout", "Nova engine did not respond in time.");
        }
        catch (Exception ex)
        {
            EngineLog.Error($"forward failed: {ex.Message}");
            return Error("engine_unreachable", "Nova engine could not be reached.");
        }
    }

    // ---- engine lifecycle -----------------------------------------------------

    private static async Task<bool> EnsureEngineAsync()
    {
        if (_ipc is { IsConnected: true }) return true;
        try
        {
            var client = new IpcClient();
            await client.ConnectAsync(await ReadEngineTokenAsync(), "browser-host", 2500);
            _ipc = client;
            client.Disconnected += () => _ipc = null;
            return true;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"engine connect failed: {ex.GetType().Name}: {ex.Message}");
            // Try to START the engine at user level (never admin).
            if (!await TryStartEngineAsync()) return false;
            try
            {
                await Task.Delay(1200);
                var client = new IpcClient();
                await client.ConnectAsync(await ReadEngineTokenAsync(), "browser-host", 4000);
                _ipc = client;
                client.Disconnected += () => _ipc = null;
                return true;
            }
            catch (Exception ex2)
            {
                EngineLog.Error($"engine restart connect failed: {ex2.GetType().Name}: {ex2.Message}");
                return false;
            }
        }
    }

    private static async Task<bool> TryStartEngineAsync()
    {
        try
        {
            if (!File.Exists(_enginePath)) return false;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _enginePath,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            System.Diagnostics.Process.Start(psi);
            await Task.Delay(600);
            return true;
        }
        catch (Exception ex)
        {
            EngineLog.Error($"cannot start engine: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads the engine IPC token from the engine data dir (NOVA_DATA_DIR overrides; default profile dir otherwise).</summary>
    private static async Task<string> ReadEngineTokenAsync()
    {
        var dir = Environment.GetEnvironmentVariable("NOVA_DATA_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            dir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova")
                : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? ".", ".local", "share", "nova");
        }
        var path = Path.Combine(dir, "ipc.token");
        if (File.Exists(path))
        {
            var t = (await File.ReadAllTextAsync(path)).Trim();
            if (t.Length >= 32) return t;
        }
        // Engine not initialized yet — deterministic dev fallback (tests only).
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Environment.MachineName + "_nova_dev")));
    }

    // ---- native framing ---------------------------------------------------------

    private static async Task<byte[]?> ReadNativeFrameAsync(Stream s)
    {
        var prefix = new byte[4];
        if (!await ReadExactAsync(s, prefix)) return null;
        var len = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (len is <= 0 or > MaxNativeMessageBytes) return null;
        var payload = new byte[len];
        return await ReadExactAsync(s, payload) ? payload : null;
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(off));
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    private static async Task WriteNativeAsync(Stream s, byte[] payload)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await s.WriteAsync(prefix);
        await s.WriteAsync(payload);
        await s.FlushAsync();
    }

    // ---- helpers ----------------------------------------------------------------

    private static object Ok(object? data) => data is null ? new { ok = true } : new { ok = true, data };
    private static object Error(string code, string message) => new { ok = false, error = new { code, message } };

    private static bool IsAllowedOrigin(string origin)
    {
        // origin like "chrome-extension://abcdef.../" or "moz-extension://<uuid>/"
        var id = origin.Split("//").LastOrDefault()?.TrimEnd('/') ?? "";
        return id.Length > 0 && _allowedExtensionIds.Contains(id);
    }

    private static void LoadConfig(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("allowed_extension_ids", out var ids))
                foreach (var id in ids.EnumerateArray())
                    _allowedExtensionIds.Add(id.GetString() ?? "");
            if (doc.RootElement.TryGetProperty("engine_path", out var ep))
                _enginePath = ep.GetString() ?? "";
        }
        catch (Exception ex)
        {
            EngineLog.Error($"config load failed: {ex.Message}");
        }
    }
}
