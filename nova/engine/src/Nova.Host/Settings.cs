namespace Nova.Host;

/// <summary>Default settings used when no database exists yet.</summary>
public sealed class Settings
{
    public static Nova.Core.Protocol.SettingsDocument LoadDefault()
    {
        var dataDir = EngineHost.GetDataDir();
        return new Nova.Core.Protocol.SettingsDocument
        {
            DefaultSaveDirectory = Path.Combine(
                Environment.GetFolderPath(OperatingSystem.IsWindows()
                    ? Environment.SpecialFolder.UserProfile
                    : Environment.SpecialFolder.UserProfile), "Downloads"),
            MaxConcurrentTasks = 3,
            MaxSegmentsPerTask = 8,
            SpeedLimitBytesPerSec = 0
        };
    }
}
