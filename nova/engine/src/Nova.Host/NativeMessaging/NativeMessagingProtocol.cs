using System.Buffers.Binary;
using System.Text;

namespace Nova.Host.NativeMessaging;

/// <summary>
/// Chrome/Firefox Native Messaging protocol: every message on stdio is
/// prefixed with a 4-byte little-endian length. Max message 1 MiB. Chrome
/// launches the host from a registry manifest; Firefox via a JSON manifest.
/// The host relays extension messages to the engine over IPC and pipes
/// replies back — so the extension is a first-class engine client.
/// </summary>
public static class NativeMessagingProtocol
{
    public const int MaxMessageBytes = 1024 * 1024;

    public static byte[] Encode(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(json), "native message over 1 MiB");
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        return frame;
    }

    /// <summary>Returns null when the browser closed the pipe.</summary>
    public static string? Read(Stream stdin)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        if (!ReadExact(stdin, lenBuf)) return null;
        var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (len < 0 || len > MaxMessageBytes)
            throw new IOException($"native message length out of range: {len}");
        var payload = new byte[len];
        if (len > 0 && !ReadExact(stdin, payload)) return null;
        return Encoding.UTF8.GetString(payload);
    }

    public static void Write(Stream stdout, string json)
    {
        var frame = Encode(json);
        stdout.Write(frame);
        stdout.Flush();
    }

    private static bool ReadExact(Stream s, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer[total..]);
            if (n <= 0) return total == 0;
            total += n;
        }
        return true;
    }
}

/// <summary>
/// Windows installation manifests for the native host. Chrome needs the
/// registry under HKCU\Software\Google\Chrome\NativeMessagingHosts; Firefox
/// reads a JSON manifest. This class generates both.
/// </summary>
public static class NativeHostRegistration
{
    public const string HostName = "com.nova.downloadmanager";

    public static string ChromeManifestJson(string hostExePath, string allowedOriginsCsv)
    {
        var origins = allowedOriginsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => $"\"{o}\"");
        return $$"""
        {
          "name": "{{HostName}}",
          "description": "Nova Download Manager native host",
          "path": "{{hostExePath.Replace("\\", "\\\\")}}",
          "type": "stdio",
          "allowed_origins": [{{string.Join(", ", origins)}}]
        }
        """;
    }

    public static string FirefoxManifestJson(string hostExePath, string allowedExtensionsCsv)
    {
        var exts = allowedExtensionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => $"\"{e}\"");
        return $$"""
        {
          "name": "{{HostName}}",
          "description": "Nova Download Manager native host",
          "path": "{{hostExePath.Replace("\\", "\\\\")}}",
          "type": "stdio",
          "allowed_extensions": [{{string.Join(", ", exts)}}]
        }
        """;
    }

    /// <summary>Registry install commands for Chrome/Edge/Brave (Windows).</summary>
    public static (string RegistryPath, string RegAddCommand) ChromeRegCommands(string manifestPath)
    {
        var regPath = @$"Software\Google\Chrome\NativeMessagingHosts\{HostName}";
        var cmd = $"reg add \"HKCU\\{regPath}\" /ve /t REG_SZ /d \"{manifestPath}\" /f";
        return (regPath, cmd);
    }
}
