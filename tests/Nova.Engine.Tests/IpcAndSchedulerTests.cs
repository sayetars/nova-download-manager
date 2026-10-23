using System.Text.Json;
using Nova.Ipc;
using Nova.Persistence;
using Nova.Scheduler;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>Full IPC stack test: real pipe server + client, handshake auth, request/response, events.</summary>
public class IpcTests : IAsyncLifetime
{
    private sealed class EchoHandler : IIpcMessageHandler
    {
        public int Hits;
        public Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct)
        {
            Interlocked.Increment(ref Hits);
            return Task.FromResult<Envelope?>(Envelope.Ok(request.Id, new { echo = request.Type }));
        }
    }

    private IpcServer _server = null!;
    private readonly string _token = "test-token-" + Guid.NewGuid().ToString("N");

    public Task InitializeAsync()
    {
        _server = new IpcServer(new EchoHandler(), _token);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Handshake_Request_Response_Roundtrip()
    {
        await using var client = new IpcClient();
        await client.ConnectAsync(_token, "test-client");
        var resp = await client.RequestAsync("hello_world", new { x = 1 });
        Assert.Equal("ok", resp.Type);
        var payload = resp.Payload!.Value.GetProperty("echo").GetString();
        Assert.Equal("hello_world", payload);
    }

    [Fact]
    public async Task Bad_Token_Is_Rejected()
    {
        await using var client = new IpcClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync("wrong-token", "intruder"));
    }

    [Fact]
    public async Task Oversized_Frame_Is_Rejected_Without_Crashing_Server()
    {
        await using var badPipe = new System.IO.Pipes.NamedPipeClientStream(".", Protocol.PipeName,
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await badPipe.ConnectAsync(3000);
        // announce a 100MB frame → server must drop the connection, not crash
        var prefix = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix, 100 * 1024 * 1024);
        await badPipe.WriteAsync(prefix);
        await badPipe.FlushAsync();

        await using var client = new IpcClient();
        await client.ConnectAsync(_token, "legit"); // server must still accept new clients
        var resp = await client.RequestAsync("ping");
        Assert.Equal("ok", resp.Type);
    }

    [Fact]
    public async Task Concurrent_Clients_And_Requests()
    {
        var clients = new List<IpcClient>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var c = new IpcClient();
                await c.ConnectAsync(_token, $"client-{i}");
                clients.Add(c);
            }
            var tasks = clients.Select(async (c, i) =>
            {
                for (var r = 0; r < 20; r++)
                {
                    var resp = await c.RequestAsync($"req_{i}_{r}");
                    Assert.Equal("ok", resp.Type);
                }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var c in clients) await c.DisposeAsync();
        }
    }
}

public class SchedulerTests
{
    [Fact]
    public void Window_Rule_Evaluates_Day_And_Time()
    {
        
        // rule: every day 01:00-06:00
        var rule = new SchedulerService.RuleDto { DaysOfWeek = "", StartTime = "01:00", EndTime = "06:00", Enabled = true };
        Assert.True(SchedulerService.RulesProbe.IsRuleActiveAt(rule, new DateTime(2026, 10, 23, 3, 30, 0)));
        Assert.False(SchedulerService.RulesProbe.IsRuleActiveAt(rule, new DateTime(2026, 10, 23, 12, 0, 0)));

        // overnight window 22:00→06:00
        var night = new SchedulerService.RuleDto { DaysOfWeek = "", StartTime = "22:00", EndTime = "06:00", Enabled = true };
        Assert.True(SchedulerService.RulesProbe.IsRuleActiveAt(night, new DateTime(2026, 10, 23, 23, 59, 0)));
        Assert.True(SchedulerService.RulesProbe.IsRuleActiveAt(night, new DateTime(2026, 10, 23, 5, 0, 0)));
        Assert.False(SchedulerService.RulesProbe.IsRuleActiveAt(night, new DateTime(2026, 10, 23, 12, 0, 0)));

        // day filter: Fridays only
        var friday = new SchedulerService.RuleDto { DaysOfWeek = "5", StartTime = "00:00", EndTime = "23:59", Enabled = true };
        Assert.True(SchedulerService.RulesProbe.IsRuleActiveAt(friday, new DateTime(2026, 10, 23, 10, 0, 0))); // 2026-10-23 is Friday
        Assert.False(SchedulerService.RulesProbe.IsRuleActiveAt(friday, new DateTime(2026, 10, 24, 10, 0, 0)));
    }
}

public class PersistenceTests : IDisposable
{
    private string _dir = Path.Combine(Path.GetTempPath(), "nova-dbtest-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Migrations_Are_Idempotent_And_Data_Survives_Reopen()
    {
        var dbPath = Path.Combine(_dir, "t.db");
        Directory.CreateDirectory(_dir);

        long id;
        {
            await using var db = new NovaDatabase(dbPath);
            var repo = new DownloadTaskRepository(db);
            id = repo.Insert(new Domain.DownloadTask { Url = "https://x/y.bin", FileName = "y.bin", DestinationDir = _dir });
        }
        {
            await using var db = new NovaDatabase(dbPath); // reopen → migrations must not re-run destructively
            var repo = new DownloadTaskRepository(db);
            var t = repo.Get(id);
            Assert.NotNull(t);
            Assert.Equal("y.bin", t!.FileName);
            var queues = new QueueRepository(db).GetAll();
            Assert.Contains(queues, q => q.Name == "main");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public class FramingTests
{
    [Fact]
    public void Roundtrip_Preserves_Envelope()
    {
        var env = new Envelope { Id = "42", Type = "add_download", Payload = JsonSerializer.SerializeToElement(new { url = "https://x" }) };
        var bytes = Framing.Encode(env);
        var back = Framing.Decode(bytes);
        Assert.NotNull(back);
        Assert.Equal("42", back!.Id);
        Assert.Equal("add_download", back.Type);
        Assert.Equal("https://x", back.Payload!.Value.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Oversize_Payload_Is_Rejected()
    {
        using var ms = new MemoryStream();
        var big = new byte[Protocol.MaxFrameBytes + 1];
        await Assert.ThrowsAsync<IpcFramingException>(() => Framing.WriteFrameAsync(ms, big, CancellationToken.None));
    }
}
