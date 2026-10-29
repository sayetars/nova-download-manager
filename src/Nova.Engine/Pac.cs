using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Jint;
using Nova.Domain;

namespace Nova.Engine;

/// <summary>
/// v1.8.0 (IDM "proxy.pac files" parity): PAC (Proxy Auto-Config, the
/// FindProxyForURL contract) evaluation via Jint (MIT, no JS engine monopoly).
/// Resolves DIRECT / PROXY host:port / SOCKS host:port per URL, with the
/// standard PAC helper environment (shExpMatch, dnsDomainIs, isInNet, …)
/// implemented natively so real-world scripts work.
/// Honest limitation: .NET's IWebProxy has no automatic chain fallback, so a
/// "PROXY a; PROXY b" chain resolves to its first usable entry (malformed
/// tokens are skipped per the PAC contract, the next token is then used).
/// </summary>
public sealed record PacDecision(string Mode, string Host, int Port)
{
    /// <summary>direct | http | socks4 | socks5</summary>
    public string Mode { get; } = Mode;

    public Uri ToProxyUri() => new($"{Mode}://{Host}:{Port}");

    public override string ToString() => Mode == "direct" ? "DIRECT" : $"{Mode.ToUpperInvariant()} {Host}:{Port}";
}

public sealed class PacResolver
{
    private readonly Jint.Engine _engine;
    private readonly object _lock = new();

    public PacResolver(string script)
    {
        _engine = new Jint.Engine(o => o
            .TimeoutInterval(TimeSpan.FromSeconds(2))
            .LimitRecursion(32)
            .MaxStatements(200_000));
        // ---- standard PAC helper environment (implemented natively) ----
        _engine.SetValue("isPlainHostName", (Func<string, bool>)(h => !h.Contains('.')));
        _engine.SetValue("dnsDomainIs", (Func<string, string, bool>)((h, d) =>
            h.EndsWith(d, StringComparison.OrdinalIgnoreCase)));
        _engine.SetValue("localHostOrDomainIs", (Func<string, string, bool>)((h, dom) =>
            string.Equals(h, dom, StringComparison.OrdinalIgnoreCase) ||
            (!h.Contains('.') && dom.StartsWith(h + ".", StringComparison.OrdinalIgnoreCase))));
        _engine.SetValue("dnsDomainLevels", (Func<string, int>)(h => h.Count(c => c == '.')));
        _engine.SetValue("shExpMatch", (Func<string, string, bool>)ShExpMatch);
        _engine.SetValue("dnsResolve", (Func<string, string>)(h => DnsResolve(h)?.FirstOrDefault()?.ToString() ?? ""));
        _engine.SetValue("isResolvable", (Func<string, bool>)(h => DnsResolve(h) is not null));
        _engine.SetValue("isInNet", (Func<string, string, string, bool>)((h, pattern, mask) =>
        {
            var addr = ParseIp(h);
            var pat = ParseIp(pattern);
            var m = ParseIp(mask);
            if (addr is null || pat is null || m is null) return false;
            var a = addr.GetAddressBytes();
            var p = pat.GetAddressBytes();
            var mb = m.GetAddressBytes();
            if (a.Length != p.Length || a.Length != mb.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if ((a[i] & mb[i]) != (p[i] & mb[i])) return false;
            return true;
        }));
        _engine.SetValue("myIpAddress", (Func<string>)(() => MyIpAddress()));
        _engine.Execute(script);
        if (_engine.GetValue("FindProxyForURL").IsUndefined())
            throw new ArgumentException("The PAC script does not define FindProxyForURL(url, host).");
    }

    /// <summary>Resolves one URL. Null → DIRECT (or unusable script result).</summary>
    public PacDecision? Resolve(string url, string host)
    {
        string result;
        lock (_lock)
        {
            try
            {
                var value = _engine.Invoke("FindProxyForURL", url, host);
                result = value.IsString() ? value.AsString() : "";
            }
            catch (Exception ex)
            {
                EngineLog.Warn($"PAC FindProxyForURL failed: {ex.Message}");
                return null;
            }
        }
        return ParseResult(result);
    }

    /// <summary>Parses a PAC result string ("DIRECT", "PROXY h:p; SOCKS h:p", …).
    /// First usable directive wins; malformed tokens are skipped (PAC contract).</summary>
    public static PacDecision? ParseResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        foreach (var raw in result.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim();
            if (token.Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
                return null;
            var sp = token.IndexOf(' ');
            if (sp <= 0) continue;
            var verb = token[..sp].Trim().ToUpperInvariant();
            var target = token[(sp + 1)..].Trim();
            var colon = target.LastIndexOf(':');
            if (colon <= 0 || colon == target.Length - 1) continue;
            var host = target[..colon].Trim();
            if (host.Length == 0 || !int.TryParse(target[(colon + 1)..], out var port) || port is < 1 or > 65535) continue;
            var mode = verb switch
            {
                "PROXY" or "PROXY!" => "http",
                "SOCKS" or "SOCKS5" => "socks5",
                "SOCKS4" => "socks4",
                _ => null,
            };
            if (mode is null) continue; // unknown verb → skip to next directive
            return new PacDecision(mode, host, port);
        }
        return null;
    }

    // ---- PAC helpers (native) ------------------------------------------------

    internal static bool ShExpMatch(string value, string pattern)
    {
        // case-insensitive glob with * and ?
        var v = value.ToLowerInvariant();
        var p = pattern.ToLowerInvariant();
        return Glob(v, 0, p, 0);
    }

    private static bool Glob(string v, int vi, string p, int pi)
    {
        while (true)
        {
            if (pi == p.Length) return vi == v.Length;
            if (vi == v.Length) return p[pi..].All(c => c == '*');
            if (p[pi] == '?') { vi++; pi++; continue; }
            if (p[pi] == '*')
            {
                for (var skip = 0; vi + skip <= v.Length; skip++)
                    if (Glob(v, vi + skip, p, pi + 1)) return true;
                return false;
            }
            if (v[vi] != p[pi]) return false;
            vi++; pi++;
        }
    }

    private static IPAddress? ParseIp(string s) =>
        IPAddress.TryParse(s.Trim(), out var a) ? a : null;

    private static IPAddress[]? DnsResolve(string host)
    {
        if (ParseIp(host) is { } literal) return new[] { literal };
        try
        {
            var task = Dns.GetHostAddressesAsync(host);
            return task.Wait(2000) ? task.Result : null;
        }
        catch { return null; }
    }

    private static string _myIp = "";

    private static string MyIpAddress()
    {
        if (_myIp.Length > 0) return _myIp;
        try
        {
            // UDP connect picks a route without sending a packet
            using var s = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            s.Connect("8.8.8.8", 53);
            _myIp = (s.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch { _myIp = "127.0.0.1"; }
        return _myIp;
    }
}

/// <summary>
/// Loads a PAC script from proxy.pac_url: http(s):// (fetched, 2 MiB cap),
/// file:/// (or any local path). Errors are honest exceptions, never fake data.
/// </summary>
public static class PacSource
{
    public const int MaxScriptBytes = 2 * 1024 * 1024;

    public static async Task<string> LoadAsync(string source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("PAC URL is empty.");
        var trimmed = source.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var noProxy = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(10) };
            var bytes = await noProxy.GetByteArrayAsync(uri, ct);
            if (bytes.Length > MaxScriptBytes) throw new InvalidOperationException("PAC script exceeds 2 MiB.");
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        var path = uri is { Scheme: "file" } ? uri.LocalPath
            : (uri is null && !trimmed.Contains("://") ? trimmed : null)
              ?? throw new ArgumentException($"Unsupported PAC source '{trimmed}'.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("PAC file not found.", path);
        if (info.Length > MaxScriptBytes) throw new InvalidOperationException("PAC script exceeds 2 MiB.");
        return await File.ReadAllTextAsync(path, ct);
    }

    public static string Load(string source) => LoadAsync(source, CancellationToken.None).GetAwaiter().GetResult();
}

/// <summary>
/// IWebProxy backed by a PAC script: GetProxy evaluates FindProxyForURL per
/// destination (5-minute cache per scheme+host). DIRECT → the destination
/// itself (per the IWebProxy contract). Bypass wildcards are honoured.
/// </summary>
public sealed class PacProxySelector : IWebProxy
{
    private readonly PacResolver _resolver;
    private readonly string[] _bypass;
    private readonly ConcurrentDictionary<string, (DateTime At, Uri? Proxy)> _cache = new();
    public ICredentials? Credentials { get; set; }

    public PacProxySelector(PacResolver resolver, IEnumerable<string>? bypass = null, ICredentials? credentials = null)
    {
        _resolver = resolver;
        _bypass = (bypass ?? []).ToArray();
        Credentials = credentials;
    }

    public Uri GetProxy(Uri destination)
    {
        var key = destination.Scheme + "://" + destination.Host;
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5))
            return hit.Proxy ?? destination;
        PacDecision? decision;
        try { decision = _resolver.Resolve(destination.ToString(), destination.Host); }
        catch { decision = null; }
        var proxy = decision?.ToProxyUri();
        _cache[key] = (DateTime.UtcNow, proxy);
        return proxy ?? destination;
    }

    public bool IsBypassed(Uri host)
    {
        foreach (var pattern in _bypass)
            if (UrlPatterns.WildcardMatch(host.Host, pattern)) return true;
        return false;
    }
}
