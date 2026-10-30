using Microsoft.Win32;
using Nova.Domain;

namespace Nova.EngineHost;

/// <summary>
/// v1.8.0 (IDM "Automatic startup" / run-at-login parity): registers the engine
/// host to start with the OS. Windows: HKCU Run key (user level, no admin — the
/// same surface Nova.Windows.AutoStart uses for the installer). Linux: XDG
/// autostart .desktop. macOS: login items are a System Settings action —
/// reported honestly as unregistered rather than faked.
/// </summary>
public static class AutoStartRegister
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NovaDownloadManager";

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }
        if (OperatingSystem.IsLinux())
            return File.Exists(XdgPath);
        return false;
    }

    public static void Set(bool enabled, string exePath)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (enabled)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                    key.SetValue(ValueName, $"\"{exePath}\"");
                }
                else
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                    key?.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { EngineLog.Warn($"autostart registry update failed: {ex.Message}"); }
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (enabled)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(XdgPath)!);
                    File.WriteAllText(XdgPath,
                        "[Desktop Entry]\nType=Application\nName=Nova Download Manager Engine\n" +
                        "Comment=Background download engine for Nova\n" +
                        $"Exec={exePath}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
                }
                else if (File.Exists(XdgPath)) File.Delete(XdgPath);
            }
            catch (Exception ex) { EngineLog.Warn($"autostart desktop entry failed: {ex.Message}"); }
        }
    }

    private static string XdgPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "autostart", "nova-engine.desktop");
}
