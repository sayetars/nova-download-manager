using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nova.Domain;
using Nova.Engine;
using Nova.Persistence;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>
/// End-to-end test of the BROWSER path (minus a real browser):
/// real EngineHost process (owns engine + IPC pipe server)
/// → spawned NovaBrowserHost process fed native-messaging frames on stdin
/// → engine → real HTTP download → file on disk → response frame back.
/// </summary>
public class BrowserPathTests : IClassFixture<BrowserPathFixture>
{
    private readonly BrowserPathFixture _fx;

    public BrowserPathTests(BrowserPathFixture fx) => _fx = fx;

    [Fact]
    public async Task Hello_Reaches_Engine()
    {
        var resp = await _fx.SendAsync(new { type = "hello" });
        Assert.True(resp.GetProperty("ok").GetBoolean());
        Assert.True(resp.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task Add_Download_Via_Host_Downloads_Real_File()
    {
        var data = TestBytes.Pattern(512 * 1024, 313);
        _fx.Server.AddFile("frombrowser.bin", data);

        var resp = await _fx.SendAsync(new
        {
            type = "add_download",
            payload = new { url = _fx.Server.UrlFor("frombrowser.bin"), auto_start = true },
        });

        Assert.True(resp.GetProperty("ok").GetBoolean());
        var taskKey = resp.GetProperty("data").GetProperty("task_key").GetString();
        Assert.NotNull(taskKey);

        // wait for engine to complete the task (real HTTP + real disk write)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        string? finalPath = null;
        while (DateTime.UtcNow < deadline)
        {
            var status = await _fx.SendAsync(new { type = "task_status", payload = new { task_key = taskKey } });
            if (status.TryGetProperty("data", out var d) &&
                d.GetProperty("state").GetString() == "completed")
            {
                finalPath = d.GetProperty("final_path").GetString();
                break;
            }
            await Task.Delay(300);
        }
        Assert.NotNull(finalPath);
        Assert.True(File.Exists(finalPath));
        Assert.True(data.SequenceEqual(await File.ReadAllBytesAsync(finalPath)));
    }

    [Fact]
    public async Task Unknown_Type_Is_Rejected_Not_Crashed()
    {
        var resp = await _fx.SendAsync(new { type = "shell_exec_please" });
        Assert.False(resp.GetProperty("ok").GetBoolean());
        // host must stay alive afterwards
        var hello = await _fx.SendAsync(new { type = "hello" });
        Assert.True(hello.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Malformed_Json_Gets_Error_Frame()
    {
        var raw = await _fx.SendRawAsync("this is not json"u8.ToArray());
        var resp = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.False(resp.GetProperty("ok").GetBoolean());
    }
}

public sealed class BrowserPathFixture : IAsyncLifetime
{
    public TestHttpServer Server { get; } = new();
    private Process? _host;
    private Process? _engine;
    public string DataDir { get; } =
        Path.Combine(Path.GetTempPath(), "nova-e2e-" + Guid.NewGuid().ToString("N"));

    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>DOTNET_ROOT so spawned apphosts find the runtime in test environments.</summary>
    private static string DotnetRoot
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
            var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            // .../shared/Microsoft.NETCore.App/8.0.x → strip two levels
            return Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        }
    }

    private static string EngineHostBin =>
        Path.Combine(RepoRoot, "src", "Nova.EngineHost", "bin", "Debug", "net8.0",
            OperatingSystem.IsWindows() ? "NovaEngine.exe" : "NovaEngine");
    private static string BrowserHostBin =>
        Path.Combine(RepoRoot, "src", "Nova.BrowserHost", "bin", "Debug", "net8.0",
            OperatingSystem.IsWindows() ? "NovaBrowserHost.exe" : "NovaBrowserHost");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDir);
        var downloads = Path.Combine(DataDir, "downloads");
        Directory.CreateDirectory(downloads);

        // 1) real engine host with our temp data dir
        var enginePsi = new ProcessStartInfo(EngineHostBin)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        enginePsi.ArgumentList.Add("--data");
        enginePsi.ArgumentList.Add(DataDir);
        enginePsi.Environment["DOTNET_ROOT"] = DotnetRoot;
        _engine = Process.Start(enginePsi);
        Assert.NotNull(_engine);
        await Task.Delay(1500); // engine boots: DB, token, pipe, scheduler

        // 2) allow the integration-test extension origin via host config (next to the exe)
        const string extId = "integrationtest0000000000000";
        var hostDir = Path.GetDirectoryName(BrowserHostBin)!;
        File.WriteAllText(Path.Combine(hostDir, "browserhost.config.json"),
            JsonSerializer.Serialize(new { allowed_extension_ids = new[] { extId } }));

        // 3) real browser host — speaks native-messaging on stdio.
        //    HOME points at our temp dir so it reads the SAME engine token the engine wrote.
        var hostPsi = new ProcessStartInfo(BrowserHostBin, $"chrome-extension://{extId}/")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        hostPsi.Environment["HOME"] = DataDir;
        hostPsi.Environment["NOVA_DATA_DIR"] = DataDir;
        hostPsi.Environment["DOTNET_ROOT"] = DotnetRoot;
        _host = Process.Start(hostPsi);
        Assert.NotNull(_host);
        await Task.Delay(400);
        Assert.False(_host.HasExited, "browser host exited immediately");
    }

    private static void WriteFrame(Stream s, byte[] payload)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        s.Write(prefix);
        s.Write(payload);
        s.Flush();
    }

    private static async Task<byte[]> ReadFrameAsync(Stream s)
    {
        var prefix = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = await s.ReadAsync(prefix.AsMemory(read, 4 - read));
            if (n <= 0) throw new IOException("host closed stdout");
            read += n;
        }
        var len = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        var payload = new byte[len];
        read = 0;
        while (read < len)
        {
            var n = await s.ReadAsync(payload.AsMemory(read, len - read));
            if (n <= 0) throw new IOException("host closed stdout mid-frame");
            read += n;
        }
        return payload;
    }

    public async Task<JsonElement> SendAsync(object msg) =>
        JsonSerializer.Deserialize<JsonElement>(
            await SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(msg)));

    public async Task<byte[]> SendRawAsync(byte[] payload)
    {
        var host = _host!;
        await host.StandardInput.BaseStream.WriteAsync(LengthPrefix(payload));
        await host.StandardInput.BaseStream.WriteAsync(payload);
        await host.StandardInput.BaseStream.FlushAsync();
        return await ReadFrameAsync(host.StandardOutput.BaseStream);
    }

    private static byte[] LengthPrefix(byte[] payload)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(p, payload.Length);
        return p;
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_host is { HasExited: false })
            {
                _host.Kill();
                await _host.WaitForExitAsync();
            }
        }
        catch { }
        try
        {
            if (_engine is { HasExited: false })
            {
                _engine.Kill();
                await _engine.WaitForExitAsync();
            }
        }
        catch { }
        Server.Dispose();
        try { Directory.Delete(DataDir, true); } catch { }
    }
}
