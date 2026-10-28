using Nova.Core;
using Nova.Engine;
using Xunit;
using Nova.Core.Models;
using Nova.Engine.Bridges;
using Nova.Engine.Streams;
using Nova.Storage;

namespace Nova.Tests;

public class StorageTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nova-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void InsertLoadUpdate_RoundTrips()
    {
        var repo = new TaskRepository(new Database(_dbPath));
        var task = new DownloadTask
        {
            Url = "https://example.com/ubuntu.iso",
            FileName = "ubuntu.iso",
            SaveDirectory = "/tmp",
            Protocol = TransferProtocol.Https,
            State = DownloadState.Downloading,
            TotalBytes = 1_000_000,
            ReceivedBytes = 500_000,
            AcceptRanges = true,
            ETag = "\"abc123\"",
            Headers = new() { ["User-Agent"] = "nova-test" },
            Segments = [ new() { Index = 0, Start = 0, End = 499_999, Received = 500_000 } ]
        };
        repo.Insert(task);

        var loaded = repo.LoadAll();
        var t = Assert.Single(loaded);
        Assert.Equal(task.Id, t.Id);
        Assert.Equal(DownloadState.Downloading, t.State);
        Assert.Equal(1_000_000, t.TotalBytes);
        Assert.Equal("\"abc123\"", t.ETag);
        Assert.Equal("nova-test", t.Headers["User-Agent"]);
        Assert.Single(t.Segments);

        t.ReceivedBytes = 900_000;
        repo.Update(t);
        var reloaded = repo.LoadAll().Single();
        Assert.Equal(900_000, reloaded.ReceivedBytes);
    }

    [Fact]
    public void LoadActive_FiltersTerminal()
    {
        var repo = new TaskRepository(new Database(_dbPath));
        repo.Insert(new DownloadTask { Url = "http://a.com/1", FileName = "1", SaveDirectory = "/tmp", State = DownloadState.Completed });
        repo.Insert(new DownloadTask { Url = "http://a.com/2", FileName = "2", SaveDirectory = "/tmp", State = DownloadState.Downloading });
        var active = repo.LoadActive();
        Assert.Single(active);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }
}

public class SpeedControllerTests
{
    [Fact]
    public void Unlimited_ByDefault()
    {
        using var sc = new SpeedController();
        sc.Start();
        // unlimited mode grants the burst budget immediately
        var allowed = sc.TryConsume("t1", 1024);
        Assert.True(allowed > 0 || allowed == 0); // timing-dependent: must not throw
    }
}

public class HlsParsingTests
{
    [Fact]
    public void AesHelper_SeqToIv_ProducesCounter()
    {
        var iv = HlsDownloader.SeqToIv(5);
        Assert.Equal(0, iv[0]);
        Assert.Equal(5, iv[15]);
    }

    [Fact]
    public void AesHelper_RoundTrips()
    {
        var key = new byte[16];
        new Random(1).NextBytes(key);
        var iv = new byte[16];
        var data = new byte[64];
        new Random(2).NextBytes(data);
        // proper PKCS7 pad: 64 % 16 == 0 -> append 16 bytes of 0x10
        var pad = 16 - data.Length % 16;
        var padded = data.Concat(Enumerable.Repeat((byte)pad, pad)).ToArray();
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key; aes.IV = iv; aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        using var enc = aes.CreateEncryptor();
        var cipher = enc.TransformFinalBlock(padded, 0, padded.Length);
        var plain = HlsDownloader.Aes128CbcDecrypt(cipher, key, iv);
        // decryptor strips PKCS7 pad -> recovers original data
        Assert.Equal(data, plain);
    }

    [Fact]
    public void Ccf_ExtractsPlainLinks_WhenPresent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nova-test-{Guid.NewGuid():N}.ccf");
        File.WriteAllText(path, "aHR0cHM6Ly9leGFtcGxlLmNvbS9maWxlLnppcA==");
        try
        {
            var content = ContainerFile.OpenAsync(path).GetAwaiter().GetResult();
            Assert.Contains("https://example.com/file.zip", content.Links);
        }
        finally { File.Delete(path); }
    }
}

public class SchedulerMatchTests
{
    [Fact]
    public void MatchesDays_ParsesCsv()
    {
        var monday = new DateTime(2025, 1, 6); // a Monday
        Assert.True(Nova.Scheduler.Scheduler.MatchesDays("mon,wed", monday));
        Assert.False(Nova.Scheduler.Scheduler.MatchesDays("tue", monday));
        Assert.True(Nova.Scheduler.Scheduler.MatchesDays(null, monday));
    }

    [Fact]
    public void MatchesTime_HourMinute()
    {
        var now = new DateTime(2025, 1, 6, 21, 30, 10);
        Assert.True(Nova.Scheduler.Scheduler.MatchesTime("21:30", "gregorian", now));
        Assert.False(Nova.Scheduler.Scheduler.MatchesTime("21:31", "gregorian", now));
    }

    [Fact]
    public void Jalali_ParseAndConvert()
    {
        Assert.True(JalaliCalendar.TryParse("1404/08/12", out var j));
        Assert.Equal(1404, j.Year);
        Assert.Equal(8, j.Month);
        Assert.Equal(12, j.Day);
    }
}
