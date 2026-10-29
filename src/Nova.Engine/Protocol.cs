using System.Net;
using System.Net.Http;

namespace Nova.Engine;

/// <summary>
/// v1.8.0 (IDM 6.43 parity: "HTTP/2 downloading"). When enabled (engine.http2,
/// default ON) every https request is offered HTTP/2 via ALPN with automatic
/// HTTP/1.1 fallback (<see cref="HttpVersionPolicy.RequestVersionOrHigher"/>).
/// Plain http:// stays 1.1 — h2c prior-knowledge is not offered by mainstream
/// servers, so loopback test servers and http-only mirrors are unaffected.
/// Multiplexed H2 streams also multiplex our parallel segment workers onto one
/// TCP connection when the server supports it, cutting handshake overhead the
/// way IDM's "reuse available connections" does.
/// </summary>
public static class HttpProtocol
{
    private static int _useHttp2 = 1; // default ON (IDM behaviour)

    /// <summary>Global switch; set at engine start and on settings change.</summary>
    public static bool UseHttp2
    {
        get => Volatile.Read(ref _useHttp2) == 1;
        set => Volatile.Write(ref _useHttp2, value ? 1 : 0);
    }

    /// <summary>Applies the H2 offer to a request (https only; idempotent).</summary>
    public static void Apply(HttpRequestMessage req)
    {
        if (!UseHttp2) return;
        if (req.RequestUri is { IsAbsoluteUri: true } u && u.Scheme == Uri.UriSchemeHttps)
        {
            req.Version = HttpVersion.Version20;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        }
    }
}
