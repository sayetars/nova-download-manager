// Integration test: starts the real engine daemon (in-process IpcServer),
// connects an IpcClient over Unix Domain Socket, adds a real HTTP download,
// and verifies the file lands on disk.
using Nova.Core;
using Nova.Core.Models;
using Nova.Core.Protocol;
using Nova.Ipc;
using Nova.Host;

Console.Error.WriteLine("[check] starting engine host ...");
var dbPath = Path.Combine(Path.GetTempPath(), $"nova-integration-{Guid.NewGuid():N}.db");
Environment.SetEnvironmentVariable("NOVA_DATA_DIR", Path.GetDirectoryName(dbPath));

var hostTask = Task.Run(async () =>
{
    var h = new EngineHost(dbPath);
    await h.RunAsync(CancellationToken.None);
});

await Task.Delay(1200); // listen

Console.WriteLine("[check] connecting IPC client (UDS) ...");
var client = await IpcClient.ConnectAsync(timeoutMs: 5000);
await client.StartAsync();
Console.WriteLine("[check] connected");

// 1. hello handshake
var hello = await client.CallAsync(MessageTypes.Hello);
Console.WriteLine($"[check] hello -> {hello.Payload}");

// 2. add real download (reachable in this sandbox)
var testUrl = "https://httpbin.org/bytes/102400";
var dir = Path.Combine(Path.GetTempPath(), "nova-integration-out");
Directory.CreateDirectory(dir);
var add = await client.CallAsync(MessageTypes.AddUrl, new AddUrlRequest(testUrl, SaveDirectory: dir, Category: "test"));
var task = add.PayloadAs<DownloadTask>();
Console.WriteLine($"[check] task added: {task?.Id} -> {task?.FileName}");

// 3. poll until terminal state (max 60s)
DownloadTask? final = null;
var sw = System.Diagnostics.Stopwatch.StartNew();
while (sw.Elapsed < TimeSpan.FromSeconds(60))
{
    await Task.Delay(1000);
    var list = await client.CallAsync(MessageTypes.List);
    var tasks = list.PayloadAs<List<DownloadTask>>() ?? new();
    final = tasks.FirstOrDefault(t => t.Id == task?.Id);
    if (final == null) continue;
    Console.WriteLine($"[check] state={final.State} recv={final.ReceivedBytes}/{final.TotalBytes} segs={final.Segments.Count}");
    if (DownloadStateMachine.IsTerminal(final.State)) break;
}

Console.WriteLine($"[check] final state: {final?.State} bytes={final?.ReceivedBytes}");

// 4. verify file exists
if (final?.State == DownloadState.Completed)
{
    var file = Path.Combine(dir, final.FileName);
    var ok = File.Exists(file);
    var size = ok ? new FileInfo(file).Length : -1;
    Console.WriteLine($"[check] file exists: {ok}, size: {size}");
    Console.WriteLine(ok && size > 0 ? "INTEGRATION-PASS" : "INTEGRATION-FAIL-FILE");
}
else
{
    Console.WriteLine("INTEGRATION-FAIL-STATE");
}

await client.DisposeAsync();
Environment.Exit(0);
