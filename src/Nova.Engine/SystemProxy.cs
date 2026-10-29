using System.Diagnostics;
using System.Net;
using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// v1.8.0 (IDM Proxy tab "Get System" button parity): reads the OS proxy
/// configuration. Windows → registry (Internet Settings), Linux → gsettings
/// (GNOME proxy, honoured by most desktops), macOS → scutil. Pure parsers are
/// separated from OS glue so they are unit-testable on any platform. Failures
/// are honest: Available=false, never invented values.
/// </summary>
public sealed record SystemProxyInfo(
    bool Available, string Mode, string Scheme, string Host, int Port,
    string Bypass, string? PacUrl, string Source)
{
    public static SystemProxyInfo Unavailable(string source) =>
        new(false, "none", "http", "", 0, "", null, source);
}

public static class SystemProxy
{
    /// <summary>Reads the current OS proxy. Never throws.</summary>
    public static SystemProxyInfo Read()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindows();
            if (OperatingSystem.IsLinux()) return ReadLinux();
            if (OperatingSystem.IsMacOS()) return ReadMac();
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"system proxy detection failed: {ex.Message}");
        }
        return SystemProxyInfo.Unavailable("none");
    }

    // ---- Windows: registry -----------------------------------------------------

    private static SystemProxyInfo ReadWindows()
    {
        const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        var enabled = Microsoft.Win32.Registry.GetValue(key, "ProxyEnable", 0) is int i && i == 1;
        var autoConfig = Microsoft.Win32.Registry.GetValue(key, "AutoConfigURL", null) as string;
        var server = Microsoft.Win32.Registry.GetValue(key, "ProxyServer", null) as string;
        var bypass = Microsoft.Win32.Registry.GetValue(key, "ProxyOverride", null) as string;

        if (!string.IsNullOrWhiteSpace(autoConfig))
            return new SystemProxyInfo(true, "pac", "http", "", 0, "", autoConfig, "windows-registry");
        if (!enabled || string.IsNullOrWhiteSpace(server))
            return SystemProxyInfo.Unavailable("windows-registry");

        var parsed = ParseWindowsProxyServer(server);
        if (parsed is null) return SystemProxyInfo.Unavailable("windows-registry");
        var (scheme, host, port) = parsed.Value;
        var cleanBypass = (bypass ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => b != "<local>")
            .ToArray();
        return new SystemProxyInfo(true, "manual", scheme, host, port,
            string.Join(";", cleanBypass), null, "windows-registry");
    }

    /// <summary>Parses ProxyServer: "host:port" or "http=h:p;https=h:p;socks=h:p". Prefers http → https → socks.</summary>
    public static (string Scheme, string Host, int Port)? ParseWindowsProxyServer(string server)
    {
        if (string.IsNullOrWhiteSpace(server)) return null;

        if (!server.Contains('='))
        {
            var p = ParseEndpoint(server);
            return p is null ? null : ("http", p.Value.Host, p.Value.Port);
        }

        var perProtocol = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var wanted in new[] { "http", "https", "socks" })
        {
            foreach (var entry in perProtocol)
            {
                var eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                var name = entry[..eq].Trim().ToLowerInvariant();
                if (name != wanted) continue;
                var ep = ParseEndpoint(entry[(eq + 1)..]);
                if (ep is null) continue;
                var scheme = name == "socks" ? "socks5" : "http";
                return (scheme, ep.Value.Host, ep.Value.Port);
            }
        }
        return null;
    }

    // ---- Linux: gsettings --------------------------------------------------------

    /// <summary>Runs `gsettings get <schema> <key>` and returns the cleaned scalar ("" on failure).</summary>
    private static string Gsettings(string spec)
    {
        var parts = spec.Split(' ', 2);
        if (parts.Length != 2) return "";
        var raw = RunCapture("gsettings", $"get {parts[0]} {parts[1]}", 3000);
        return raw is null ? "" : GsettingsValue(raw);
    }

    private static SystemProxyInfo ReadLinux()
    {
        var mode = Gsettings("org.gnome.system.proxy mode");
        if (mode == "auto")
        {
            var pac = Gsettings("org.gnome.system.proxy autoconfig-url");
            if (!string.IsNullOrWhiteSpace(pac))
                return new SystemProxyInfo(true, "pac", "http", "", 0, "", pac, "gsettings");
        }
        if (mode != "manual")
            return SystemProxyInfo.Unavailable("gsettings");

        var host = Gsettings("org.gnome.system.proxy.http host");
        var port = ParseInt(Gsettings("org.gnome.system.proxy.http port"));
        if (string.IsNullOrWhiteSpace(host) || port is null)
        {
            // fall back to the SOCKS entry when no HTTP proxy is set
            var sHost = Gsettings("org.gnome.system.proxy.socks host");
            var sPort = ParseInt(Gsettings("org.gnome.system.proxy.socks port"));
            if (string.IsNullOrWhiteSpace(sHost) || sPort is null)
                return SystemProxyInfo.Unavailable("gsettings");
            return new SystemProxyInfo(true, "manual", "socks5", sHost, sPort.Value, IgnoreHosts(), null, "gsettings");
        }
        return new SystemProxyInfo(true, "manual", "http", host, port.Value, IgnoreHosts(), null, "gsettings");
    }

    private static string IgnoreHosts()
    {
        var raw = Gsettings("org.gnome.system.proxy ignore-hosts");
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var items = raw.Trim('[', ']', ' ')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(i => i.Trim('\'', '"'))
            .Where(i => i.Length > 0 && i != "localhost" && !i.StartsWith("127.0.0"))
            .ToArray();
        return string.Join(";", items);
    }

    /// <summary>Parses one gsettings scalar: 'manual' / '1.2.3.4' / (uint32) 8080 / "…".</summary>
    public static string GsettingsValue(string raw)
    {
        var v = raw.Trim();
        if (v.StartsWith("(uint32)", StringComparison.OrdinalIgnoreCase)) v = v["(uint32)".Length..].Trim();
        if (v.StartsWith("(int32)", StringComparison.OrdinalIgnoreCase)) v = v["(int32)".Length..].Trim();
        if (v.StartsWith("(string)", StringComparison.OrdinalIgnoreCase)) v = v["(string)".Length..].Trim();
        return v.Trim('\'', '"');
    }

    // ---- macOS: scutil -----------------------------------------------------------

    private static SystemProxyInfo ReadMac()
    {
        var raw = RunCapture("scutil", "--proxy", 3000);
        if (raw is null) return SystemProxyInfo.Unavailable("scutil");

        if (ParseScutilFlag(raw, "ProxyAutoConfigEnable") == 1 &&
            ParseScutilValue(raw, "ProxyAutoConfigURLString") is { Length: > 0 } pacUrl)
            return new SystemProxyInfo(true, "pac", "http", "", 0, "", pacUrl, "scutil");

        if (ParseScutilFlag(raw, "HTTPEnable") == 1)
        {
            var host = ParseScutilValue(raw, "HTTPProxy") ?? "";
            var port = ParseInt(ParseScutilValue(raw, "HTTPPort") ?? "");
            if (host.Length > 0 && port is > 0)
            {
                var bypass = ParseScutilBypass(raw);
                return new SystemProxyInfo(true, "manual", "http", host, port.Value, bypass, null, "scutil");
            }
        }
        if (ParseScutilFlag(raw, "SOCKSEnable") == 1)
        {
            var host = ParseScutilValue(raw, "SOCKSProxy") ?? "";
            var port = ParseInt(ParseScutilValue(raw, "SOCKSPort") ?? "");
            if (host.Length > 0 && port is > 0)
                return new SystemProxyInfo(true, "manual", "socks5", host, port.Value, "", null, "scutil");
        }
        return SystemProxyInfo.Unavailable("scutil");
    }

    public static int ParseScutilFlag(string raw, string key)
    {
        var v = ParseScutilValue(raw, key);
        return int.TryParse(v, out var i) ? i : 0;
    }

    public static string? ParseScutilValue(string raw, string key)
    {
        foreach (var line in raw.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            if (line[..idx].Trim() != key) continue;
            return line[(idx + 1)..].Trim();
        }
        return null;
    }

    private static string ParseScutilBypass(string raw)
    {
        // ExceptionsList : <array> { 1.2.3.4 5.6.7.8 } — best effort
        var items = new List<string>();
        var inList = false;
        foreach (var line in raw.Split('\n'))
        {
            if (line.Contains("ExceptionsList")) { inList = true; continue; }
            if (inList)
            {
                if (line.Contains('}')) break;
                var v = line.Trim();
                if (v.Length > 0) items.Add(v);
            }
        }
        return string.Join(";", items.Where(i => i != "localhost" && !i.StartsWith("127.0.0")));
    }

    // ---- shared plumbing ----------------------------------------------------------

    private static (string Host, int Port)? ParseEndpoint(string s)
    {
        var idx = s.LastIndexOf(':');
        if (idx <= 0 || idx == s.Length - 1) return null;
        var host = s[..idx].Trim(' ', '[', ']');
        if (host.Length == 0) return null;
        if (!int.TryParse(s[(idx + 1)..], out var port) || port is < 1 or > 65535) return null;
        return (host, port);
    }

    private static int? ParseInt(string? s) =>
        int.TryParse(s, out var i) && i is > 0 and < 65536 ? i : null;

    private static string? RunCapture(string exe, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
