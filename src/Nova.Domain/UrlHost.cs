namespace Nova.Domain;

/// <summary>URL host normalization shared by the engine and scheduler (per-server caps).</summary>
public static class UrlHost
{
    /// <summary>Lowercase host with leading "www." stripped; "" when unparseable.</summary>
    public static string Of(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return "";
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }
}
