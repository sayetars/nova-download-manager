using System.Diagnostics;
using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// Post-completion actions (IDM parity): exit engine, shut down, hibernate,
/// sleep, log off — fired when all downloads have finished. Windows actions
/// degrade gracefully on the Linux self-test platform.
/// </summary>
public static class PostActionRunner
{
    public const string None = "";
    public const string ExitEngine = "exit";
    public const string Shutdown = "shutdown";
    public const string Hibernate = "hibernate";
    public const string Sleep = "sleep";
    public const string LogOff = "logoff";
    /// <summary>v1.8.0 (IDM Scheduler parity): "Open the following file when done" —
    /// encoded as <c>open:&lt;path&gt;</c>. The path may be a document (opens with the
    /// default application), an executable, or a .bat/.cmd script (IDM documents the
    /// .bat trick for running several programs).</summary>
    public const string OpenPrefix = "open:";

    public static readonly string[] All = { None, ExitEngine, Shutdown, Hibernate, Sleep, LogOff };

    public static bool IsValid(string action)
        => All.Contains(action) || IsOpen(action ?? "");

    /// <summary>True for a well-formed <c>open:&lt;path&gt;</c> action with a non-empty target.</summary>
    public static bool IsOpen(string action)
        => action.StartsWith(OpenPrefix, StringComparison.OrdinalIgnoreCase)
           && action.Trim().Length > OpenPrefix.Length;

    /// <summary>The path of an <c>open:</c> action (trimmed); empty otherwise.</summary>
    public static string OpenTarget(string action)
        => IsOpen(action) ? action[OpenPrefix.Length..].Trim() : "";

    /// <summary>Pure builder (testable): the process start for an open action.
    /// Documents/bat/exe open via the shell so file associations apply.</summary>
    public static ProcessStartInfo BuildOpenStartInfo(string target) => new(target)
    {
        UseShellExecute = true,
    };

    /// <summary>Executes the action. Engine exit is requested via callback so the host can shut down cleanly.</summary>
    public static void Execute(string action, Action requestEngineExit, Action<string>? log = null)
    {
        var line = log ?? (_ => { });
        if (IsOpen(action))
        {
            var target = OpenTarget(action);
            line($"PostAction: opening '{target}'.");
            try
            {
                using var p = Process.Start(BuildOpenStartInfo(target));
                if (p is not null && !p.WaitForExit(60_000)) p.Kill();
            }
            catch (Exception ex)
            {
                EngineLog.Warn($"PostAction open failed: {ex.Message}");
            }
            return;
        }
        switch (action)
        {
            case None:
                break;
            case ExitEngine:
                line("PostAction: engine exit requested.");
                requestEngineExit();
                break;
            case Shutdown:
                line("PostAction: system shutdown.");
                if (OperatingSystem.IsWindows()) Run("shutdown", "/s /t 30");
                else if (OperatingSystem.IsLinux()) Run("sudo", "-n shutdown -h now");
                else if (OperatingSystem.IsMacOS()) Run("osascript", "-e 'tell app \"System Events\" to shut down'");
                break;
            case Hibernate:
                line("PostAction: hibernate.");
                if (OperatingSystem.IsWindows()) Run("shutdown", "/h");
                else line("Hibernate not supported on this OS.");
                break;
            case Sleep:
                line("PostAction: sleep.");
                if (OperatingSystem.IsWindows()) Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
                else if (OperatingSystem.IsLinux()) Run("systemctl", "suspend");
                break;
            case LogOff:
                line("PostAction: log off.");
                if (OperatingSystem.IsWindows()) Run("shutdown", "/l");
                else line("Log off not supported on this OS.");
                break;
            default:
                line($"PostAction: unknown '{action}' ignored.");
                break;
        }
    }

    private static void Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            if (p is not null && !p.WaitForExit(60_000)) p.Kill();
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"PostAction run failed: {ex.Message}");
        }
    }
}
