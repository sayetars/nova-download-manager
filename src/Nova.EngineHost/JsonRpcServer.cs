using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Nova.Domain;
using Nova.Ipc;

namespace Nova.EngineHost;

/// <summary>
/// v1.3.0 remote control (aria2-RPC / FDM-remote parity): a token-protected JSON-RPC 2.0
/// endpoint on 127.0.0.1 that maps Nova methods onto the engine router, plus a WebSocket
/// channel streaming live engine events. Localhost-only by design; the token is required
/// on every request (header "Authorization: Bearer …" or ?token= query parameter).
/// </summary>
public sealed class JsonRpcServer : IDisposable
{
    private static readonly Dictionary<string, string> MethodMap = new(StringComparer.Ordinal)
    {
        ["engine.ping"] = "ping",
        ["engine.status"] = "engine_status",
        ["engine.quota"] = "quota_state",
        ["task.list"] = "list_tasks",
        ["task.get"] = "get_task",
        ["task.add"] = "add_download",
        ["task.add_batch"] = "add_downloads",
        ["task.pause"] = "pause_task",
        ["task.resume"] = "resume_task",
        ["task.cancel"] = "cancel_task",
        ["task.retry"] = "retry_task",
        ["task.remove"] = "remove_task",
        ["task.segments"] = "task_segments",
        ["task.update"] = "update_task",
        ["task.postprocess"] = "task_postprocess",
        ["queue.list"] = "list_queues",
        ["queue.save"] = "save_queue",
        ["settings.get"] = "get_settings",
        ["settings.set"] = "set_settings",
        ["feed.list"] = "list_feed_rules",
        ["feed.save"] = "save_feed_rule",
        ["feed.delete"] = "delete_feed_rule",
        ["feed.run"] = "run_feed",
        ["sync.list"] = "list_sync_rules",
        ["sync.run"] = "run_sync",
        ["system.shutdown"] = "shutdown_engine",
    };

    private readonly IpcMessageRouter _router;
    private readonly string _token;
    private readonly int _port;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public JsonRpcServer(IpcMessageRouter router, string token, int port)
    {
        _router = router;
        _token = token;
        _port = port;
    }

    public int Port { get; private set; }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        // honor the configured port; when busy (or 0 = auto), walk forward to a free one.
        // A failed Start() leaves the HttpListener unusable → create a fresh one per attempt.
        var configured = _port;
        var bound = false;
        for (var attempt = 0; attempt < 20 && !bound; attempt++)
        {
            var port = configured == 0 ? Random.Shared.Next(20000, 40000) : configured + attempt;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/rpc/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                bound = true;
            }
            catch (HttpListenerException ex) when (ex.ErrorCode is 98 or 32 or 183 or 5)
            {
                try { listener.Close(); } catch { }
            }
        }
        if (!bound || _listener is null)
        {
            EngineLog.Warn("rpc: could not bind any port — JSON-RPC disabled for this run");
            return;
        }
        _ = AcceptLoopAsync(_cts.Token);
        EngineLog.Info($"rpc: JSON-RPC listening on http://127.0.0.1:{Port}/rpc/ (token required)");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                EngineLog.Warn($"rpc accept failed: {ex.Message}");
                continue;
            }
            _ = HandleContextAsync(ctx, ct);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var token = GetToken(ctx.Request);
            if (token != _token)
            {
                await WriteJsonAsync(ctx.Response, 401,
                    JsonSerializer.Serialize(new { jsonrpc = "2.0", error = new { code = -32001, message = "invalid or missing token" } }), ct);
                return;
            }

            if (ctx.Request.IsWebSocketRequest)
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                await WebSocketLoopAsync(wsCtx.WebSocket, ct);
                return;
            }

            if (ctx.Request.HttpMethod != "POST")
            {
                await WriteJsonAsync(ctx.Response, 200,
                    "{\"jsonrpc\":\"2.0\",\"result\":\"nova-jsonrpc\",\"id\":null}", ct);
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            var result = await HandleRpcAsync(body, ct);
            await WriteJsonAsync(ctx.Response, 200, result, ct);
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"rpc request failed: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private static string? GetToken(HttpListenerRequest req)
    {
        var auth = req.Headers["Authorization"];
        if (auth is not null)
        {
            var parts = auth.Split(' ', 2);
            if (parts.Length == 2 && parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
                return parts[1].Trim();
        }
        var q = req.QueryString["token"];
        return string.IsNullOrEmpty(q) ? null : q;
    }

    /// <summary>Handles single JSON-RPC requests (batch arrays supported for POSTs).</summary>
    internal async Task<string> HandleRpcAsync(string body, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var results = new List<string>();
                foreach (var item in doc.RootElement.EnumerateArray())
                    results.Add(await HandleSingleAsync(item, ct));
                return "[" + string.Join(",", results) + "]";
            }
            return await HandleSingleAsync(doc.RootElement, ct);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = (object?)null,
                error = new { code = -32700, message = "parse error" } });
        }
    }

    private async Task<string> HandleSingleAsync(JsonElement req, CancellationToken ct)
    {
        var id = req.TryGetProperty("id", out var idEl) ? idEl.Clone() : (JsonElement?)null;
        try
        {
            var method = req.GetProperty("method").GetString() ?? "";
            var paramsEl = req.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?)null;

            if (!MethodMap.TryGetValue(method, out var envType))
                return Error(id, -32601, $"unknown method '{method}'");

            // system.shutdown maps to the engine's own shutdown semantics via cancel_all? Honest: unsupported
            if (envType == "shutdown_engine")
                return Error(id, -32601, "system.shutdown is not permitted over RPC");

            var payload = paramsEl?.ValueKind == JsonValueKind.Object
                ? (JsonElement?)paramsEl.Value
                : paramsEl?.ValueKind == JsonValueKind.String
                    ? JsonSerializer.Deserialize<JsonElement>(paramsEl.Value.GetString() ?? "{}")
                    : null;

            // JSON-RPC params accept snake_case (IPC style); accept camelCase too via a light pass
            var envelope = new Envelope
            {
                Id = "rpc",
                Type = envType,
                Payload = NormalizeParams(payload),
            };
            var resp = await _router.HandleAsync(envelope, ct);

            if (resp is null)
                return Error(id, -32000, "engine produced no response");

            if (resp.Type == "error")
            {
                var err = resp.PayloadAs<IpcError>();
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32000, message = err?.Message ?? "engine error",
                                  data = new { code = err?.Code, category = err?.Category } },
                }, Protocol.Json);
            }

            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = resp.Payload }, Protocol.Json);
        }
        catch (Exception ex)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    /// <summary>Converts camelCase RPC param names to the IPC snake_case wire names.</summary>
    private static JsonElement? NormalizeParams(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p) return payload;
        var dict = new Dictionary<string, object?>();
        foreach (var prop in p.EnumerateObject())
            dict[ToSnakeCase(prop.Name)] = prop.Value.Clone();
        return JsonSerializer.SerializeToElement(dict, Protocol.Json);
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Error(JsonElement? id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });

    // ---- WebSocket live events ------------------------------------------------

    private async Task WebSocketLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        // authenticate once via first frame {"token": "..."}
        var authed = false;
        using var periodic = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            // drain incoming (auth frame / pings) and push status snapshots
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var recvTask = ws.ReceiveAsync(buffer.AsMemory(), linked.Token).AsTask();
            var tickTask = periodic.WaitForNextTickAsync(linked.Token).AsTask();
            var done = await Task.WhenAny(recvTask, tickTask);
            if (done == tickTask)
            {
                if (!authed) continue;
                var status = await _router.HandleAsync(new Envelope { Id = "ws", Type = "engine_status" }, ct);
                if (status?.Payload is { } payload)
                {
                    var json = JsonSerializer.Serialize(new { @event = "engine_status", data = payload }, Protocol.Json);
                    await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
                }
                linked.Cancel();
                _ = recvTask.ContinueWith(_ => { }, TaskScheduler.Default);
            }
            else
            {
                var res = await recvTask;
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                var text = Encoding.UTF8.GetString(buffer, 0, res.Count);
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var tok = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
                    if (tok == _token && !authed)
                    {
                        authed = true;
                        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"event\":\"hello\"}"), WebSocketMessageType.Text, true, ct);
                    }
                }
                catch { /* malformed frame — ignore */ }
            }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int status, string json, CancellationToken ct)
    {
        resp.StatusCode = status;
        resp.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct);
        resp.Close();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); _listener?.Close(); } catch { }
    }
}
