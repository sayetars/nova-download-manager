using System.Text.Json;
using Nova.Core.Protocol;
using Nova.Engine;
using Xunit;
using Nova.Core;
using Nova.Core.Models;
using Nova.Ipc;

namespace Nova.Tests;

public class FrameTests
{
    [Fact]
    public void WriteRead_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 255, 0, 128 };
        using var ms = new MemoryStream();
        Frame.Write(ms, payload);
        ms.Position = 0;
        var read = Frame.Read(ms);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task WriteRead_LargePayload_RoundTrips()
    {
        var payload = new byte[300_000];
        new Random(42).NextBytes(payload);
        using var ms = new MemoryStream();
        await Task.Run(() => Frame.Write(ms, payload));
        ms.Position = 0;
        var read = await Task.Run(() => Frame.Read(ms));
        Assert.Equal(payload, read);
    }

    [Fact]
    public void Read_ReturnsNull_OnCleanEof()
    {
        using var ms = new MemoryStream(Array.Empty<byte>());
        Assert.Null(Frame.Read(ms));
    }

    [Fact]
    public void Read_Throws_OnTruncatedFrame()
    {
        var payload = new byte[10];
        var truncated = new byte[4 + 4]; // claims 10 bytes, has 4
        truncated[0] = 0; truncated[1] = 0; truncated[2] = 0; truncated[3] = 10;
        using var ms = new MemoryStream(truncated);
        Assert.Throws<EndOfStreamException>(() => Frame.Read(ms));
    }
}

public class StateMachineTests
{
    [Theory]
    [InlineData(DownloadState.Created, DownloadState.Analyzing, true)]
    [InlineData(DownloadState.Created, DownloadState.Completed, false)]
    [InlineData(DownloadState.Downloading, DownloadState.Pausing, true)]
    [InlineData(DownloadState.Paused, DownloadState.Downloading, true)]
    [InlineData(DownloadState.Completed, DownloadState.Downloading, true)]     // re-download
    [InlineData(DownloadState.Cancelled, DownloadState.Queued, true)]           // revive
    [InlineData(DownloadState.Failed, DownloadState.Completed, false)]
    public void CanTransition_Validates(DownloadState from, DownloadState to, bool expected)
        => Assert.Equal(expected, DownloadStateMachine.CanTransition(from, to));

    [Fact]
    public void TerminalStates_AreCorrect()
    {
        Assert.True(DownloadStateMachine.IsTerminal(DownloadState.Completed));
        Assert.True(DownloadStateMachine.IsTerminal(DownloadState.Failed));
        Assert.True(DownloadStateMachine.IsTerminal(DownloadState.Cancelled));
        Assert.False(DownloadStateMachine.IsTerminal(DownloadState.Paused));
    }
}

public class JsonEnvelopeTests
{
    [Fact]
    public void Envelope_RoundTrips()
    {
        var env = new IpcEnvelope { V = 1, Type = "task.addUrl", RequestId = 7,
            Payload = JsonSerializer.SerializeToElement(new { url = "https://example.com/file.zip" }, Json.Options) };
        var json = Json.Serialize(env);
        var back = Json.Deserialize<IpcEnvelope>(json);
        Assert.NotNull(back);
        Assert.Equal("task.addUrl", back!.Type);
        Assert.Equal(7, back.RequestId);
        Assert.Equal("https://example.com/file.zip", back.PayloadAs<AddUrlRequest>()!.Url);
    }
}

public class NativeMessagingTests
{
    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        var json = """{"type":"hello","v":1}""";
        var frame = Nova.Host.NativeMessaging.NativeMessagingProtocol.Encode(json);
        using var ms = new MemoryStream(frame);
        var decoded = Nova.Host.NativeMessaging.NativeMessagingProtocol.Read(ms);
        Assert.Equal(json, decoded);
    }
}
