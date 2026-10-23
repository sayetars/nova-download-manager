using System.Runtime.InteropServices;
using Nova.Domain;
using Nova.EngineHost;

namespace Nova.EngineHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var dataDir = ArgValue(args, "--data");
        var verbose = Array.Exists(args, a => a is "--verbose" or "-v");

        if (!verbose)
            EngineLog.Sink = m => { /* silent background mode: logs go nowhere by default */ };

        // Single instance per user session.
        using var mutex = TryAcquireSingleInstance();
        if (mutex is null)
        {
            EngineLog.Info("Another Nova engine instance is already running. Exiting.");
            return 0;
        }

        Console.CancelKeyPress += (_, ea) => { ea.Cancel = true; Environment.Exit(0); };

        try
        {
            await using var app = new EngineHostApp(dataDir);
            app.Run();
            if (verbose) EngineLog.Info("Engine running. Press Ctrl+C to stop.");
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal: {ex}");
            return 1;
        }
    }

    private static IDisposable? TryAcquireSingleInstance()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var mutex = new System.Threading.Mutex(initiallyOwned: true, "Local\\NovaEngineSingleInstance", out var created);
            if (!created)
            {
                try { mutex.ReleaseMutex(); } catch { }
                return null;
            }
            return mutex;
        }
        else
        {
            // POSIX: lock file with PID — stale locks (crashed/killed engine) are reclaimed.
            var lockPath = Path.Combine(EngineHostApp.DefaultDataDir(), "engine.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var fs = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    var pid = System.Text.Encoding.UTF8.GetBytes($"{Environment.ProcessId}");
                    fs.Write(pid, 0, pid.Length);
                    fs.Flush();
                    return fs;
                }
                catch (IOException) when (attempt == 0)
                {
                    if (!IsStaleLock(lockPath)) return null;
                    try { File.Delete(lockPath); } catch { return null; }
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>A lock is stale when the recorded PID no longer exists.</summary>
    private static bool IsStaleLock(string lockPath)
    {
        try
        {
            var text = File.ReadAllText(lockPath).Trim();
            if (!int.TryParse(text, out var pid) || pid <= 0) return true;
            return !File.Exists($"/proc/{pid}");
        }
        catch
        {
            return true;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
