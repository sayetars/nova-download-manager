using System.Text.Json;
using Nova.DownloadEngine;

var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var dataDir = Path.Combine(home, ".nova");
Directory.CreateDirectory(dataDir);

if (args.Length == 0) { PrintHelp(); return 0; }

using var store = new SqliteStore(Path.Combine(dataDir, "nova.db"));
await using var engine = new DownloadEngine(store);

switch (args[0].ToLowerInvariant())
{
    case "serve":
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var endpoint = OperatingSystem.IsWindows()
            ? $@"\\.\pipe\{Ipc.PipeName}"
            : Ipc.UnixSocketPath;
        Console.WriteLine($"nova-engine v0.1.0 — IPC: {endpoint}");
        await engine.RecoverAsync();
        var api = new LocalApi(engine);
        try { await Ipc.ServeAsync(api.HandleAsync, cts.Token); }
        catch (OperationCanceledException) { }
        Console.WriteLine("nova-engine stopped.");
        return 0;
    }

    case "download":
    {
        string? url = null, outPath = null;
        int? segments = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--out") outPath = Val(args, ref i);
            else if (args[i] is "--segments") segments = int.Parse(Val(args, ref i));
            else if (!args[i].StartsWith('-') && url is null) url = args[i];
            else { Console.Error.WriteLine($"unknown argument: {args[i]}"); return 2; }
        }
        if (url is null)
        {
            Console.Error.WriteLine("usage: NovaEngine download <url> [--out <file>] [--segments <n>]");
            return 2;
        }

        var task = await engine.AddAsync(url, outPath, segments);
        while (true)
        {
            var t = engine.GetTask(task.Id) ?? task;
            var total = t.TotalSize >= 0 ? FormatBytes(t.TotalSize) : "?";
            var pct = t.TotalSize > 0 ? $"{t.Received * 100 / t.TotalSize,3}%" : "  ?";
            Console.Write($"\r  [{t.State,-11}] {FormatBytes(t.Received),10} / {total,10}  {pct}   ");
            if (t.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled or DownloadState.Paused)
            {
                Console.WriteLine();
                Console.WriteLine($"  file: {t.FilePath}  ({t.SegmentCount} segment(s))");
                if (t.Error is not null) Console.WriteLine($"  error: {t.Error}");
                return t.State == DownloadState.Completed ? 0 : 1;
            }
            await Task.Delay(200);
        }
    }

    case "list":
    {
        foreach (var t in engine.List())
            Console.WriteLine($"{t.Id[..8]}  {t.State,-11}  {FormatBytes(t.Received),10}  {(t.Total >= 0 ? FormatBytes(t.Total) : "?"),10}  {t.FilePath}");
        return 0;
    }

    case "pause" or "resume" or "cancel":
    {
        if (args.Length < 2) { Console.Error.WriteLine($"usage: NovaEngine {args[0]} <task-id>"); return 2; }
        var type = args[0].ToLowerInvariant() switch
        {
            "pause" => "pause-download",
            "resume" => "resume-download",
            _ => "cancel-download",
        };
        var json = JsonSerializer.Serialize(new { v = 1, type, payload = new { id = args[1] } });
        try
        {
            Console.WriteLine(await IpcClient.SendAsync(json));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cannot reach engine service — is 'NovaEngine serve' running? ({ex.Message})");
            return 1;
        }
    }

    default:
        Console.Error.WriteLine($"unknown command: {args[0]}");
        PrintHelp();
        return 2;
}

static string Val(string[] a, ref int i)
{
    if (i + 1 >= a.Length) throw new ArgumentException($"missing value for {a[i]}");
    return a[++i];
}

static string FormatBytes(double b) => b >= 1L << 40 ? $"{b / (1L << 40):F2} TiB"
    : b >= 1L << 30 ? $"{b / (1L << 30):F2} GiB"
    : b >= 1L << 20 ? $"{b / (1L << 20):F2} MiB"
    : b >= 1024 ? $"{b / 1024:F1} KiB"
    : $"{b:F0} B";

static void PrintHelp()
{
    Console.WriteLine("""
        Nova Download Engine v0.1.0
        ---------------------------
          serve                     run the engine service (IPC: Named Pipe / Unix socket)
          download <url>            download a file
            --out <path>            target file path
            --segments <n>          parallel connections (1-32)
          list                      list all tasks (from the local database)
          pause <id> | resume <id> | cancel <id>   control a running task (needs 'serve')
        """);
}
