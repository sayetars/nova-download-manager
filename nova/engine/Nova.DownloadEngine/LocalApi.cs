using System.Text.Json;

namespace Nova.DownloadEngine;

/// <summary>
/// Dispatches versioned JSON IPC messages to the engine.
/// </summary>
public sealed class LocalApi
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly DownloadEngine _engine;

    public LocalApi(DownloadEngine engine) => _engine = engine;

    public async Task<string> HandleAsync(string line, CancellationToken ct)
    {
        try
        {
            var env = JsonSerializer.Deserialize<Protocol.Envelope>(line, Json)
                      ?? throw new JsonException("empty message");
            if (env.V != Protocol.Version)
                return Protocol.Reply(false, $"protocol version {env.V} is not supported (expected {Protocol.Version})");

            object result = env.Type switch
            {
                "ping" => new { message = "pong" },
                "add-download" => ToInfo(await HandleAddAsync(Require<Protocol.AddDownloadRequest>(env.Payload))),
                "list-downloads" => _engine.List(),
                "pause-download" => ToInfo(await _engine.PauseAsync(RequireId(env.Payload))),
                "resume-download" => ToInfo(await _engine.ResumeAsync(RequireId(env.Payload))),
                "cancel-download" => ToInfo(await _engine.CancelAsync(RequireId(env.Payload))),
                _ => throw new NotSupportedException($"unknown message type '{env.Type}'"),
            };
            return Protocol.Reply(true, result);
        }
        catch (Exception ex)
        {
            return Protocol.Reply(false, ex.Message);
        }
    }

    async Task<DownloadTask> HandleAddAsync(Protocol.AddDownloadRequest r)
        => await _engine.AddAsync(r.Url, r.FilePath, r.Segments);

    static T Require<T>(JsonElement? payload)
        => payload is null
            ? throw new ArgumentException("missing payload")
            : payload.Value.Deserialize<T>(Json) ?? throw new JsonException($"invalid payload for {typeof(T).Name}");

    static string RequireId(JsonElement? payload)
    {
        if (payload is { } p && p.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
            return s;
        throw new ArgumentException("missing payload.id");
    }

    static Protocol.TaskInfo ToInfo(DownloadTask t) => new()
    {
        Id = t.Id,
        Url = t.Url,
        FilePath = t.FilePath,
        State = t.State.ToString(),
        Received = t.Received,
        Total = t.TotalSize,
        SpeedBytesPerSec = t.SpeedBps,
        Error = t.Error,
    };
}
