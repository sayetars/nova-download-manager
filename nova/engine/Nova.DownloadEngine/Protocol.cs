using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nova.DownloadEngine;

/// <summary>
/// Versioned JSON-line IPC protocol (v1).
/// Request : {"v":1,"type":"add-download","payload":{...}}  + '\n'
/// Response: {"ok":true,"data":{...}}  + '\n'
/// </summary>
public static class Protocol
{
    public const int Version = 1;

    public sealed class Envelope
    {
        [JsonPropertyName("v")] public int V { get; set; } = Version;
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("payload")] public JsonElement? Payload { get; set; }
    }

    public sealed class AddDownloadRequest
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("filePath")] public string? FilePath { get; set; }
        [JsonPropertyName("segments")] public int? Segments { get; set; }
    }

    public sealed class TaskInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("filePath")] public string FilePath { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("received")] public long Received { get; set; }
        [JsonPropertyName("total")] public long Total { get; set; }
        [JsonPropertyName("speedBps")] public double SpeedBytesPerSec { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    public static string Reply(bool ok, object? data) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = ok, ["data"] = data });
}
