using System.Text.Json;
using Nova.Ipc;

namespace Nova.Cli;

/// <summary>
/// Nova command line (IDM parity). Modern verbs + IDM-compatible switches:
///   nova /d URL       download now (IDM /d)
///   nova /a URL       add without starting (IDM /a)
///   nova /p URL       save-as dialog is owned by the UI; CLI adds paused (IDM /p best-effort)
///   nova /f FILE      save to file name (IDM /f)
///   nova /n USER PASS login (IDM /n)
///   nova /q           start main queue (IDM /q)
///   nova /s           stop engine + all queues (IDM /s)
/// Modern: add, list, pause, resume, cancel, remove, speed, video, grab,
/// ftp-ls, zip, export, import, status, help.
/// </summary>
public static class Program
{
    private static IpcClient? _client;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) return Help();

        var first = args[0];

        // engine data dir override (--data path, or NOVA_DATA env) — token lives there
        _dataDirOverride = ArgValue(args, "--data") ?? Environment.GetEnvironmentVariable("NOVA_DATA");
        args = args.Where(a => !a.StartsWith("--data") && a != (_dataDirOverride ?? "__x__")).ToArray();
        if (args.Length == 0) return Help();
        first = args[0];

        // ---- IDM-style switches ------------------------------------------
        if (first.StartsWith("/"))
        {
            var url = args.Skip(1).FirstOrDefault(a => !a.StartsWith("/")) ?? "";
            switch (first.ToLowerInvariant())
            {
                case "/d": return await Add(url, autoStart: true);
                case "/a": return await Add(url, autoStart: false);
                case "/p": return await Add(url, autoStart: false);   // paused add; the UI owns the save dialog
                case "/f":
                    var file = args.Skip(1).FirstOrDefault(a => !a.StartsWith("/")) ?? "";
                    return file.Length == 0 ? Help() : await Add(file, autoStart: true);
                case "/n":
                    var user = args.ElementAtOrDefault(1) ?? "";
                    var pass = args.ElementAtOrDefault(2) ?? "";
                    var host = args.ElementAtOrDefault(3) ?? "*";
                    return await Send("save_login", new { host, userName = user, password = pass, scheme = "basic" });
                case "/q": return await Send("resume_all", new { });
                case "/s":
                    await Send("pause_all", new { });
                    return await Send("engine_status", new { });   // engine stays alive; queue stopped
                default: return Help();
            }
        }

        try
        {
            return first switch
            {
                "add" or "download" => await Add(args.Skip(1).FirstOrDefault() ?? "", autoStart: true, args: args),
                "list" => await Send("list_tasks", new { filter = "all", limit = 500 }),
                "status" => await Send("engine_status", new { }),
                "pause" => await KeyCommand(args, "pause_task"),
                "resume" => await KeyCommand(args, "resume_task"),
                "cancel" => await KeyCommand(args, "cancel_task"),
                "remove" => await Send("remove_task", new { task_key = Val(args, "--key") ?? args.Skip(1).FirstOrDefault(), delete_file = Has(args, "--files") }),
                "speed" => await Speed(args),
                "pause-all" => await Send("pause_all", new { }),
                "resume-all" => await Send("resume_all", new { }),
                "video" => await Video(args),
                "grab" => await Grab(args),
                "ftp-ls" => await Ftp(args),
                "zip" => await Zip(args),
                "export" => await Send("export_tasks", new { }),
                "import" => await Send("import_tasks", new { json = await File.ReadAllTextAsync(Val(args, "--file") ?? args.Skip(1).FirstOrDefault() ?? throw new ArgumentException("--file required")) }),
                "expand" => await Send("expand_pattern", new { root_url = args.Skip(1).FirstOrDefault() }),
                "help" or "--help" or "-h" => Help(),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static async Task<int> Add(string url, bool autoStart, string[]? args = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("error: URL required");
            return 2;
        }
        var payload = new Dictionary<string, object?> { ["url"] = url, ["auto_start"] = autoStart };
        if (args != null)
        {
            if (Val(args, "--file") is { } f) payload["file_name"] = f;
            if (Val(args, "--dir") is { } d) payload["destination_dir"] = d;
            if (Val(args, "--connections") is { } c && int.TryParse(c, out var con)) payload["max_connections"] = con;
            if (Val(args, "--queue") is { } q) payload["queue_name"] = q;
            if (Val(args, "--mirrors") is { } m) payload["mirrors"] = m.Split(',', ';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (Val(args, "--start-at") is { } s && DateTimeOffset.TryParse(s, out var t)) payload["start_at_ms"] = t.ToUnixTimeMilliseconds();
            if (Val(args, "--post") is { } pa) payload["post_action"] = pa;
        }
        return await Send("add_download", payload);
    }

    private static async Task<int> Speed(string[] args)
    {
        var v = args.Skip(1).FirstOrDefault();
        if (v is null) return await Send("get_settings", new { });
        if (!long.TryParse(v, out var kbps))
        {
            Console.Error.WriteLine("error: speed expects KB/s (0 = unlimited)");
            return 2;
        }
        var settings = new Dictionary<string, string> { ["engine.global_speed_cap"] = (kbps * 1024).ToString() };
        return await Send("set_settings", new { values = settings });
    }

    private static async Task<int> Video(string[] args)
    {
        var url = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (url is null) { Console.Error.WriteLine("error: URL required"); return 2; }
        var quality = Val(args, "--quality");
        if (quality is null)
            return await Send("media_probe", new { url });
        return await Send("add_download", new Dictionary<string, object?>
        {
            ["url"] = url, ["kind"] = "auto", ["auto_start"] = true,
        });
    }

    private static async Task<int> Grab(string[] args)
    {
        var url = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (url is null) { Console.Error.WriteLine("error: URL required"); return 2; }
        return await Send("grabber_start", new Dictionary<string, object?>
        {
            ["root_url"] = url,
            ["max_depth"] = int.TryParse(Val(args, "--depth"), out var d) ? d : 3,
            ["save_directory"] = Val(args, "--dir") ?? "",
            ["include_filter"] = Val(args, "--include") ?? "",
            ["exclude_filter"] = Val(args, "--exclude") ?? "",
            ["scripts"] = Has(args, "--scripts"),
            ["video"] = Has(args, "--video"),
            ["images"] = !Has(args, "--no-images"),
            ["rewrite_for_offline"] = !Has(args, "--no-offline"),
        });
    }

    private static async Task<int> Ftp(string[] args)
    {
        var url = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (url is null) { Console.Error.WriteLine("error: URL required"); return 2; }
        return await Send("ftp_list", new Dictionary<string, object?>
        {
            ["url"] = url,
            ["auth_user"] = Val(args, "--user"),
            ["auth_password"] = Val(args, "--pass"),
        });
    }

    private static async Task<int> Zip(string[] args)
    {
        var url = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (url is null) { Console.Error.WriteLine("error: URL required"); return 2; }
        return await Send("zip_list", new Dictionary<string, object?> { ["url"] = url });
    }

    private static Task<int> KeyCommand(string[] args, string type) =>
        Send(type, new { task_key = Val(args, "--key") ?? args.Skip(1).FirstOrDefault() });

    // ---------------------------------------------------------------- transport
    private static async Task<int> Send(string type, object? payload)
    {
        _client ??= await ConnectOrLaunchAsync();
        if (_client is null)
        {
            Console.Error.WriteLine("error: engine offline and could not be started");
            return 3;
        }
        var resp = await _client.RequestAsync(type, payload);
        if (resp.Type == "error")
        {
            var err = resp.PayloadAs<IpcError>();
            Console.Error.WriteLine($"error: {err?.Code}: {err?.Message}");
            return 2;
        }
        Console.WriteLine(JsonSerializer.Serialize(resp.Payload, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<IpcClient?> ConnectOrLaunchAsync()
    {
        var token = ReadToken();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var client = new IpcClient();
            try
            {
                if (await client.ConnectAsync(token, "nova-cli", timeoutMs: 800))
                    return client;
            }
            catch { }
            try { client.DisposeAsync().AsTask().Wait(200); } catch { }

            if (attempt == 0) LaunchEngine();

            await Task.Delay(700);
        }
        return null;
    }

    private static void LaunchEngine()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var candidates = OperatingSystem.IsWindows()
                ? new[] { Path.Combine(exeDir, "NovaEngine.exe"), Path.Combine(exeDir, "..", "NovaEngine", "NovaEngine.exe") }
                : new[] { Path.Combine(exeDir, "NovaEngine"), Path.Combine(exeDir, "..", "NovaEngine", "NovaEngine") };
            foreach (var c in candidates.Where(File.Exists))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(c) { UseShellExecute = false, CreateNoWindow = true };
                if (!string.IsNullOrEmpty(_dataDirOverride)) psi.ArgumentList.Add("--data");
                if (!string.IsNullOrEmpty(_dataDirOverride)) psi.ArgumentList.Add(_dataDirOverride!);
                System.Diagnostics.Process.Start(psi);
                return;
            }
        }
        catch { /* best effort */ }
    }

    private static string? _dataDirOverride;

    private static string DataDir()
    {
        if (!string.IsNullOrEmpty(_dataDirOverride)) return _dataDirOverride!;
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova")
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? ".", ".local", "share", "nova");
    }

    private static string ReadToken()
    {
        try { return File.ReadAllText(Path.Combine(DataDir(), "ipc.token")).Trim(); }
        catch { return ""; }
    }

    private static string? Val(string[] a, string name)
    {
        for (var i = 0; i < a.Length - 1; i++)
            if (a[i] == name) return a[i + 1];
        return null;
    }

    private static string? ArgValue(string[] a, string name)
    {
        var v = Val(a, name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static bool Has(string[] a, string name) => a.Contains(name);

    private static int Help()
    {
        Console.WriteLine("""
            nova — Nova Download Manager command line (IDM parity)

            Modern verbs:
              nova add URL [--file F] [--dir D] [--connections N] [--queue Q]
                         [--mirrors U1,U2] [--start-at '2026-01-01 10:00'] [--post shutdown]
              nova list | status
              nova pause|resume|cancel|remove TASK_KEY [--files]
              nova speed [KBPS]            get or set the global speed limit
              nova pause-all | resume-all
              nova video URL [--quality Q] probe stream variants / download
              nova grab URL [--depth N] [--dir D] [--include P] [--exclude P] [--scripts] [--video] [--no-images]
              nova ftp-ls URL [--user U --pass P]
              nova zip URL                 remote ZIP central-directory preview
              nova export | import --file F
              nova expand PATTERN           expand https://x/file[01-99].jpg

            IDM-compatible switches:
              nova /d URL     download now          nova /a URL   add without starting
              nova /p URL     add paused            nova /f FILE  download to file
              nova /n USER PASS [HOST]  store login  nova /q       start queue
              nova /s         stop queues

            The CLI auto-starts the engine when it is offline.
            """);
        return 0;
    }
}
