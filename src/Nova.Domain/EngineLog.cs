namespace Nova.Domain;

/// <summary>
/// Process-wide logger for engine-side components. Sink is set by the host; default writes
/// to stderr — NEVER stdout, because Native Messaging owns stdout.
/// </summary>
public static class EngineLog
{
    public static Action<string>? Sink { get; set; } = static m => Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {m}");
    public static void Info(string m) => Sink?.Invoke($"INFO  {m}");
    public static void Warn(string m) => Sink?.Invoke($"WARN  {m}");
    public static void Error(string m) => Sink?.Invoke($"ERROR {m}");
}
