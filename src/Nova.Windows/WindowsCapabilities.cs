using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nova.Scheduler;

namespace Nova.Windows;

/// <summary>Real battery state via GetSystemPowerStatus.</summary>
public sealed class WindowsBatteryStatus : IBatteryStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS status);

    public bool OnAcPower
    {
        get
        {
            var s = new SYSTEM_POWER_STATUS();
            return GetSystemPowerStatus(ref s) && s.ACLineStatus == 1;
        }
    }
}

/// <summary>Run-at-login (HKCU — user level, no admin) registration for the engine host.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NovaDownloadManager";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Enable(string engineExePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{engineExePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// Native Messaging Host registration for Chrome/Brave (HKCU registry) and
/// Firefox (manifest JSON next to the host + about:config allowed-manifests note).
/// </summary>
public static class NativeMessagingRegistration
{
    private const string ChromeKey = @"Software\Google\Chrome\NativeMessagingHosts";
    private const string BraveKey = @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts";
    private const string HostName = "com.nova.downloadmanager";

    public static string HostNameValue => HostName;

    public static void RegisterChromium(string hostManifestPath, string allowedExtensionId)
    {
        foreach (var root in new[] { ChromeKey, BraveKey })
        {
            using var key = Registry.CurrentUser.CreateSubKey($"{root}\\{HostName}");
            key.SetValue(string.Empty, hostManifestPath);
        }
        _ = allowedExtensionId;
    }

    public static void UnregisterChromium()
    {
        foreach (var root in new[] { ChromeKey, BraveKey })
        {
            using var key = Registry.CurrentUser.OpenSubKey(root, writable: true);
            key?.DeleteSubKey(HostName, throwOnMissingSubKey: false);
        }
    }

    /// <summary>Firefox loads native manifests from %APPDATA%\mozilla\NativeMessagingHosts (browsers ≥ FF50 via manifest dir policy).</summary>
    public static string FirefoxManifestDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mozilla", "NativeMessagingHosts");
}

/// <summary>
/// Power actions after queue completion — ALWAYS requires explicit user opt-in;
/// executed via documented Windows commands (shutdown.exe), not raw API abuse.
/// </summary>
public static class PowerActions
{
    public static void Sleep() => Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
    public static void Hibernate() => Run("shutdown.exe", "/h");
    public static void Shutdown() => Run("shutdown.exe", "/s /t 30");

    private static void Run(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        System.Diagnostics.Process.Start(psi);
    }
}
