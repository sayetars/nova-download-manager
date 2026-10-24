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

    public static readonly string[] All = { None, ExitEngine, Shutdown, Hibernate, Sleep, LogOff };

    public static bool IsValid(string action) => All.Contains(action);

    /// <summary>Executes the action. Engine exit is requested via callback so the host can shut down cleanly.</summary>
    public static void Execute(string action, Action requestEngineExit, Action<string>? log = null)
    {
        var line = log ?? (_ => { });
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
