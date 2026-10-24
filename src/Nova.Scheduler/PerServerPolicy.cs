using System.Text.Json;
using Nova.Domain;
using Nova.Persistence;

namespace Nova.Scheduler;

/// <summary>
/// IDM parity — per-server connection-count exceptions:
/// a global default cap plus per-host overrides (settings
/// "scheduler.per_server_max" and "scheduler.server_exceptions").
/// </summary>
public static class PerServerPolicy
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed class ExceptionEntry
    {
        public string Host { get; set; } = "";
        public int Max { get; set; }
    }

    /// <summary>Normalized host key: lowercase, leading "www." stripped; "" when unparseable.</summary>
    public static string HostOf(string url) => UrlHost.Of(url);

    /// <summary>Effective cap for a host: override wins, else the global default; 0 = unlimited.</summary>
    public static int CapFor(string host, ISettingsRepository settings)
    {
        host = Normalize(host);
        if (host.Length == 0) return 0;
        var exceptions = ParseExceptions(settings.Get("scheduler.server_exceptions"));
        foreach (var e in exceptions)
            if (HostMatches(host, Normalize(e.Host)))
                return e.Max;
        return ParseDefault(settings.Get("scheduler.per_server_max"));
    }

    /// <summary>Lowercase + strip a leading "www." from a bare host (defensive normalization).</summary>
    private static string Normalize(string host)
    {
        var h = (host ?? "").Trim().ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }

    /// <summary>True when a task from <paramref name="host"/> may start given the running-per-host counts.</summary>
    public static bool MayStart(string host, IReadOnlyDictionary<string, int> activePerHost, ISettingsRepository settings)
    {
        if (host.Length == 0) return true;
        var cap = CapFor(host, settings);
        if (cap <= 0) return true;
        return activePerHost.GetValueOrDefault(host, 0) < cap;
    }

    public static List<ExceptionEntry> ParseExceptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<ExceptionEntry>>(json, JsonOpts) ?? new(); }
        catch { return new(); }
    }

    public static string SerializeExceptions(List<ExceptionEntry> entries) =>
        JsonSerializer.Serialize(entries, JsonOpts);

    public static int ParseDefault(string? value) =>
        int.TryParse(value, out var v) && v >= 0 ? v : 0;

    private static bool HostMatches(string host, string pattern)
    {
        var p = (pattern ?? "").Trim().ToLowerInvariant();
        if (p.Length == 0) return false;
        if (p.StartsWith("*."))
            return host == p[2..] || host.EndsWith(p[1..]);
        return host == p;
    }
}
