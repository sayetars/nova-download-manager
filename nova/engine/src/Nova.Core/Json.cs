using System.Text;
using System.Text.Json;

namespace Nova.Core;

/// <summary>
/// Central JSON serialization options for all IPC and storage documents.
/// Versioned envelope: every message carries ProtocolVersion so old clients
/// can be rejected gracefully instead of mis-parsed.
/// </summary>
public static class Json
{
    public const int ProtocolVersion = 1;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)
        }
    };

    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, Options);

    public static byte[] SerializeToUtf8<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8)
        => JsonSerializer.Deserialize<T>(utf8, Options);

    public static byte[] ToArray(this MemoryStream s) => s.ToArray();
}

/// <summary>
/// Envelope for every frame crossing the IPC boundary (UI <-> engine,
/// extension -> native host -> engine). Mirrors IDM's internal command bus
/// but with explicit version negotiation.
/// </summary>
public sealed class IpcEnvelope
{
    public int V { get; set; } = Json.ProtocolVersion;
    public string Type { get; set; } = "";
    public long? RequestId { get; set; }
    public JsonElement? Payload { get; set; }
    public string? Error { get; set; }
}

public static class IpcEnvelopeExtensions
{
    public static T? PayloadAs<T>(this IpcEnvelope e)
        => e.Payload.HasValue
            ? e.Payload.Value.Deserialize<T>(Json.Options)
            : default;
}
