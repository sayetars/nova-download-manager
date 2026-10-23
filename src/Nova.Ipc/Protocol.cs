using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nova.Ipc;

/// <summary>Protocol constants — MUST stay in sync with Dart (apps/nova_desktop) and extensions/shared/protocol.ts.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const string PipeName = "nova-engine-v1";
    public const int MaxFrameBytes = 4 * 1024 * 1024;      // 4 MiB hard cap
    public const int MaxConnections = 16;
    public const int HandshakeTimeoutMs = 8000;
    public const int RequestTimeoutMs = 30000;

    /// <summary>Local-user secret shared via user-profile config; read by engine &amp; trusted local clients.</summary>
    public const string AuthTokenSetting = "ipc.auth_token";

    /// <summary>
    /// Actual pipe/socket name used at runtime. Windows keeps the classic named pipe;
    /// Unix maps "name" to /tmp/CoreFxPipe_name (System.IO.Pipes) unless NOVA_PIPE_PATH
    /// points at an absolute socket path (hardened setups). MUST stay in sync with
    /// apps/nova_desktop/lib/core/protocol.dart (pipePath).
    /// </summary>
    public static string ResolvedPipeName
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("NOVA_PIPE_PATH");
            if (!string.IsNullOrWhiteSpace(env) && Path.IsPathRooted(env)) return env;
            return PipeName;
        }
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>Wire envelope. Every frame is exactly one envelope.</summary>
public sealed class Envelope
{
    public int V { get; set; } = Protocol.Version;
    public string? Id { get; set; }
    /// <summary>Message type: request name, "ok", "error", or an event name.</summary>
    public string Type { get; set; } = string.Empty;
    public JsonElement? Payload { get; set; }
    /// <summary>Handshake auth token (only on "hello").</summary>
    public string? Token { get; set; }
    public long Ts { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static Envelope Ok(string? id, object? payload = null) => new()
    {
        Id = id,
        Type = "ok",
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, Protocol.Json),
    };

    public static Envelope Error(string? id, string code, string category, string message,
        Dictionary<string, string>? details = null) => new()
    {
        Id = id,
        Type = "error",
        Payload = JsonSerializer.SerializeToElement(new IpcError
        {
            Code = code,
            Category = category,
            Message = message,
            Details = details,
        }, Protocol.Json),
    };
}

public static class EnvelopeExtensions
{
    public static T? PayloadAs<T>(this Envelope env) where T : class
        => env.Payload is null ? null : env.Payload.Value.Deserialize<T>(Protocol.Json);
}

public sealed class IpcError
{
    public string Code { get; set; } = "internal_error";
    public string Category { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, string>? Details { get; set; }
}
