using System.Net;

namespace Nova.Engine;

/// <summary>
/// Proxy configuration parsed from settings keys (IDM parity):
///   proxy.enabled, proxy.scheme (http|https|socks4|socks4a|socks5),
///   proxy.host, proxy.port, proxy.user, proxy.pass,
///   proxy.bypass (";"-separated host wildcards), proxy.for_ftp,
///   proxy.mode ("manual" (default) | "pac"), proxy.pac_url (v1.8.0).
/// .NET 8 SocketsHttpHandler supports HTTP(S) and SOCKS4/4a/5 schemes natively.
/// </summary>
public sealed record ProxyConfig
{
    public bool Enabled { get; init; }
    public string Scheme { get; init; } = "http";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string? User { get; init; }
    public string? Password { get; init; }
    public string[] Bypass { get; init; } = [];
    public bool ForFtp { get; init; } = true;
    /// <summary>v1.8.0: "pac" → resolve per-URL via the PAC script at <see cref="PacUrl"/>.</summary>
    public string Mode { get; init; } = "manual";
    public string? PacUrl { get; init; }

    public bool UsesPac => Enabled && Mode == "pac" && !string.IsNullOrWhiteSpace(PacUrl);

    public static ProxyConfig FromSettings(Func<string, string?> get)
    {
        var scheme = (get("proxy.scheme") ?? "http").ToLowerInvariant();
        if (scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")) scheme = "http";
        return new ProxyConfig
        {
            Enabled = get("proxy.enabled") == "true" || get("proxy.enabled") == "1",
            Scheme = scheme,
            Host = get("proxy.host") ?? "",
            Port = int.TryParse(get("proxy.port"), out var p) ? p : 0,
            User = get("proxy.user"),
            Password = get("proxy.pass"),
            Bypass = (get("proxy.bypass") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ForFtp = get("proxy.for_ftp") != "false",
            Mode = string.Equals(get("proxy.mode"), "pac", StringComparison.OrdinalIgnoreCase) ? "pac" : "manual",
            PacUrl = get("proxy.pac_url"),
        };
    }

    public bool IsValid => Enabled && !string.IsNullOrWhiteSpace(Host) && Port is > 0 and < 65536;

    /// <summary>True when <paramref name="host"/> matches the bypass list (wildcards * ?).</summary>
    public bool IsBypassed(string host)
    {
        foreach (var pattern in Bypass)
            if (UrlPatterns.WildcardMatch(host, pattern)) return true;
        return false;
    }

    /// <summary>Apply to a SocketsHttpHandler. Caller keeps its redirect policy and timeouts.</summary>
    public void Apply(SocketsHttpHandler handler, string targetHost)
    {
        if (!IsValid) return;
        if (IsBypassed(targetHost)) return;
        var wp = new WebProxy($"{Scheme}://{Host}:{Port}") { BypassProxyOnLocal = true };
        if (!string.IsNullOrEmpty(User))
            wp.Credentials = new NetworkCredential(User, Password ?? "");
        if (Bypass.Length > 0) wp.BypassList = Bypass.Select(b => "^" + b.Replace(".", "\\.").Replace("*", ".*").Replace("?", ".") + "$").ToArray();
        handler.Proxy = wp;
        handler.UseProxy = true;
    }
}

/// <summary>Typed credentials for per-task / vault auth (Basic via header; NTLM/Negotiate via engine-wide settings).</summary>
public sealed record TaskCredentials(string UserName, string Password, string Scheme = "basic")
{
    public string BasicHeader()
        => "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{UserName}:{Password}"));
}
