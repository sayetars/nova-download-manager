using Nova.Core;
using Nova.Core.Protocol;
using Nova.Engine;
using Nova.Host.NativeMessaging;

namespace Nova.Host;

/// <summary>
/// Entry point. Modes (zero external CLI deps — hand-rolled parser):
///   nova-host                                engine daemon (started by UI / service host)
///   nova-host --native-messaging             browser native host bridge
///   nova-host cli add <url> [-o dir] [-n segs]
///   nova-host cli list | pause <id> | resume <id> | cancel <id>
///   nova-host cli speed [<kbps>]
///   nova-host cli grab <site> [-d depth] [-f "*.jpg;*.png"]
///   nova-host install-chrome-host --exe <path> [--origins csv] [--firefox-exts csv]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--native-messaging" && args.Length == 1)
                return await RunNativeMessagingAsync(args.Length == 1);

            switch (args[0].ToLowerInvariant())
            {
                case "--native-messaging":
                    return await RunNativeMessagingAsync(true);
                case "daemon":
                case "serve":
                    await RunDaemonAsync();
                    return 0;
                case "cli":
                    return await RunCliAsync(args[1..]);
                case "install-chrome-host":
                    InstallHost(args);
                    return 0;
                case "--help":
                case "-h":
                case "help":
                    PrintHelp();
                    return 0;
                default:
                    // auto-detect native messaging context: no console args but stdin is a pipe
                    if (!Console.IsInputRedirected)
                    {
                        await RunDaemonAsync();
                        return 0;
                    }
                    PrintHelp();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nova] fatal: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        Nova Download Manager engine 1.0.0
        ---------------------------------
        nova-host                 run engine daemon (IPC server)
        nova-host --native-messaging
                                  run as browser native messaging host
        nova-host cli add <url> [-o dir] [-n segments]
        nova-host cli list
        nova-host cli pause|resume|cancel <taskId>
        nova-host cli speed [<kbps>]          0 = unlimited
        nova-host cli grab <site> [-d depth] [-f filter]
        nova-host install-chrome-host --exe <path>
        """);
    }

    // ------------------------------------------------------------------
    // Mode 1: engine daemon
    // ------------------------------------------------------------------

    private static async Task RunDaemonAsync()
    {
        var dataDir = EngineHost.GetDataDir();
        Directory.CreateDirectory(dataDir);
        var host = new EngineHost(Path.Combine(dataDir, "nova.db"));
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        await host.RunAsync(cts.Token);
    }

    // ------------------------------------------------------------------
    // Mode 2: browser native messaging host
    // ------------------------------------------------------------------

    private static async Task<int> RunNativeMessagingAsync(bool standalone)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Nova.Ipc.IpcClient? client = null;
        try
        {
            client = await Nova.Ipc.IpcClient.ConnectAsync(timeoutMs: 1500, ct: cts.Token);
            await client.StartAsync();
        }
        catch
        {
            // engine not running — attempt to connect again, then give a clean error
            try
            {
                client = await Nova.Ipc.IpcClient.ConnectAsync(timeoutMs: 3000, ct: cts.Token);
                await client.StartAsync();
            }
            catch
            {
                NativeMessagingProtocol.Write(Console.OpenStandardOutput(),
                    Core.Json.Serialize(new { type = "error", error = "engine unreachable" }));
                return 1;
            }
        }

        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        while (!cts.IsCancellationRequested)
        {
            var raw = NativeMessagingProtocol.Read(stdin);
            if (raw is null) break;

            try
            {
                var envelope = Core.Json.Deserialize<Core.IpcEnvelope>(raw);
                if (envelope is null) continue;

                var reply = await client.CallAsync(envelope.Type, envelope.PayloadAs<object?>(), 15);
                NativeMessagingProtocol.Write(stdout,
                    Core.Json.Serialize(new Core.IpcEnvelope
                    {
                        V = 1, Type = reply.Type, RequestId = envelope.RequestId,
                        Payload = reply.Payload
                    }));
            }
            catch (Exception ex)
            {
                NativeMessagingProtocol.Write(stdout,
                    Core.Json.Serialize(new { type = "error", error = ex.Message }));
            }
        }
        if (client != null) await client.DisposeAsync();
        return 0;
    }

    // ------------------------------------------------------------------
    // Mode 3: CLI
    // ------------------------------------------------------------------

    private static async Task<int> RunCliAsync(string[] args)
    {
        if (args.Length == 0) { PrintHelp(); return 1; }
        var cmd = args[0].ToLowerInvariant();
        var rest = args[1..];

        switch (cmd)
        {
            case "add":
            {
                if (rest.Length == 0) { Console.Error.WriteLine("usage: cli add <url> [-o dir] [-n segs]"); return 1; }
                var url = rest[0];
                string? dir = null;
                var segs = 8;
                for (var i = 1; i < rest.Length - 1; i++)
                {
                    if (rest[i] is "-o" or "--out") dir = rest[i + 1];
                    else if (rest[i] is "-n" or "--segments") int.TryParse(rest[i + 1], out segs);
                }
                return await CliAddAsync(url, dir, segs);
            }
            case "list":
                return await CliListAsync();
            case "pause" or "resume" or "cancel" or "retry" or "remove":
            {
                if (rest.Length == 0) { Console.Error.WriteLine($"usage: cli {cmd} <taskId>"); return 1; }
                return await CliControlAsync($"task.{cmd}", rest[0]);
            }
            case "speed":
            {
                long? kbps = rest.Length > 0 && long.TryParse(rest[0], out var v) ? v : null;
                return await CliSpeedAsync(kbps);
            }
            case "grab":
            {
                if (rest.Length == 0) { Console.Error.WriteLine("usage: cli grab <site> [-d depth] [-f filter]"); return 1; }
                var depth = 2;
                string? filter = null;
                for (var i = 1; i < rest.Length - 1; i++)
                {
                    if (rest[i] is "-d" or "--depth") int.TryParse(rest[i + 1], out depth);
                    else if (rest[i] is "-f" or "--filter") filter = rest[i + 1];
                }
                return await CliGrabAsync(rest[0], depth, filter);
            }
            default:
                Console.Error.WriteLine($"unknown cli command: {cmd}");
                return 1;
        }
    }

    private static async Task<int> CliAddAsync(string url, string? outDir, int segments)
    {
        var client = await ConnectCliAsync();
        if (client == null) { Console.Error.WriteLine("engine not running — start nova-host first"); return 1; }
        try
        {
            var reply = await client.CallAsync(MessageTypes.AddUrl, new AddUrlRequest(
                url, SaveDirectory: outDir, Headers: new Dictionary<string, string>()));
            var task = reply.PayloadAs<Core.Models.DownloadTask>();
            Console.WriteLine(task != null ? $"added: {task.Id} {task.FileName}" : "added");
            return 0;
        }
        catch (Nova.Ipc.IpcRemoteException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        finally { await client.DisposeAsync(); }
    }

    private static async Task<int> CliListAsync()
    {
        var client = await ConnectCliAsync();
        if (client == null) { Console.Error.WriteLine("engine not running — start nova-host first"); return 1; }
        var reply = await client.CallAsync(MessageTypes.List);
        var tasks = reply.PayloadAs<List<Core.Models.DownloadTask>>() ?? new();
        Console.WriteLine($"{"ID",-14} {"STATE",-12} {"PROGRESS",-10} {"FILE",-40}");
        foreach (var t in tasks)
            Console.WriteLine($"{t.Id[..Math.Min(14, t.Id.Length)],-14} {t.State,-12} {t.Progress * 100,8:0.0}%   {Trunc(t.FileName, 40),-40}");
        await client.DisposeAsync();
        return 0;
    }

    private static async Task<int> CliControlAsync(string type, string id)
    {
        var client = await ConnectCliAsync();
        if (client == null) { Console.Error.WriteLine("engine not running"); return 1; }
        try { await client.CallAsync(type, id); Console.WriteLine("OK"); return 0; }
        catch (Nova.Ipc.IpcRemoteException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        finally { await client.DisposeAsync(); }
    }

    private static async Task<int> CliSpeedAsync(long? kbps)
    {
        var client = await ConnectCliAsync();
        if (client == null) { Console.Error.WriteLine("engine not running"); return 1; }
        if (kbps is { } kb)
        {
            await client.CallAsync(MessageTypes.SetGlobalSpeedLimit, new { limit = kb * 1024 });
            Console.WriteLine($"limit set to {kb} KB/s");
        }
        else
        {
            Console.WriteLine("pass KB/s value (0 = unlimited)");
        }
        await client.DisposeAsync();
        return 0;
    }

    private static async Task<int> CliGrabAsync(string site, int depth, string? filter)
    {
        Console.WriteLine($"grabbing {site} (depth {depth})...");
        var http = DownloadEngine.BuildHttpClient(Settings.LoadDefault());
        var grabber = new Nova.Scheduler.SiteGrabber(http);
        var count = 0;
        grabber.FileFound += f => Console.WriteLine($"  [{++count}] {f.Url}");
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        await grabber.GrabAsync(new Nova.Scheduler.SiteGrabber.GrabOptions
        {
            StartUrl = site, MaxDepth = depth, IncludeFilter = filter,
            SaveDirectory = Path.Combine(EngineHost.GetDataDir(), "grabber")
        }, cts.Token);
        Console.WriteLine($"done: {count} files");
        return 0;
    }

    private static async Task<Nova.Ipc.IpcClient?> ConnectCliAsync()
    {
        try
        {
            var client = await Nova.Ipc.IpcClient.ConnectAsync(timeoutMs: 2500);
            await client.StartAsync();
            return client;
        }
        catch { return null; }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ------------------------------------------------------------------
    // Mode 4: native host installer
    // ------------------------------------------------------------------

    private static void InstallHost(string[] args)
    {
        var exePath = GetOpt(args, "--exe") ?? Environment.ProcessPath ?? "";
        var origins = GetOpt(args, "--origins") ?? "chrome-extension://nova/";
        var firefoxExts = GetOpt(args, "--firefox-exts") ?? "nova@nova.dm";

        var dataDir = EngineHost.GetDataDir();
        Directory.CreateDirectory(dataDir);

        var chromeManifest = NativeHostRegistration.ChromeManifestJson(exePath, origins);
        var chromePath = Path.Combine(dataDir, $"{NativeHostRegistration.HostName}.chrome.json");
        File.WriteAllText(chromePath, chromeManifest);

        var ffManifest = NativeHostRegistration.FirefoxManifestJson(exePath, firefoxExts);
        var ffPath = Path.Combine(dataDir, $"{NativeHostRegistration.HostName}.firefox.json");
        File.WriteAllText(ffPath, ffManifest);

        Console.WriteLine($"chrome manifest : {chromePath}");
        Console.WriteLine($"firefox manifest: {ffPath}");

        if (OperatingSystem.IsWindows())
        {
            var (_, cmd) = NativeHostRegistration.ChromeRegCommands(chromePath);
            Console.WriteLine("run to register with Chrome (HKCU):");
            Console.WriteLine($"  {cmd}");
        }
        else
        {
            Console.WriteLine("Linux/macOS: copy firefox manifest to ~/.mozilla/native-messaging-hosts/");
        }
    }

    private static string? GetOpt(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
