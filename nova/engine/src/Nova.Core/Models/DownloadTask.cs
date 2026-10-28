using System.Text.Json.Serialization;

namespace Nova.Core.Models;

/// <summary>Transport protocol of the task.</summary>
public enum TransferProtocol { Http, Https, Ftp, Ftps, Sftp, Hls, Dash, Magnet, Metalink, YtDlp }

/// <summary>A single byte-range segment being downloaded by one worker.</summary>
public sealed class DownloadSegment
{
    public long Index { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public long Received { get; set; }
    public SegmentState State { get; set; } = SegmentState.Idle;

    public long Length => End - Start + 1;
    public bool IsComplete => End > 0 && Received >= Length;
}

public enum SegmentState { Idle, Connecting, Downloading, Done, Error }

/// <summary>Credentials entry (encrypted at rest via DPAPI on Windows).</summary>
public sealed class CredentialRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Realm { get; set; } = "";       // host[:port] or realm string
    public string? Username { get; set; }
    public string? Secret { get; set; }           // password or token
    public string AuthType { get; set; } = "basic"; // basic | digest | ntlm | kerberos | bearer | client-cert | debrid-apikey
    public string? ClientCertPath { get; set; }   // mTLS PEM/PFX
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Proxy configuration (per task or global fallback).</summary>
public sealed class ProxyConfig
{
    public static readonly ProxyConfig Direct = new() { ProxyType = ProxyType.Direct };

    public ProxyType ProxyType { get; set; } = ProxyType.Direct;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Secret { get; set; }
    /// <summary>PAC script URL — engine evaluates the script itself.</summary>
    public string? PacUrl { get; set; }
    public bool UseForTask { get; set; } = true;
}

public enum ProxyType { Direct, Http, Socks4, Socks4a, Socks5, Pac }

/// <summary>Video/audio track selection offered by the quality panel.</summary>
public sealed class MediaTrack
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "video"; // video | audio | subtitle
    public string? Container { get; set; }      // mp4 | webm | ts
    public string? Codec { get; set; }          // h264 | h265 | vp9 | av1 | aac | opus
    public long? Height { get; set; }           // 2160 1440 1080 ...
    public string? Fps { get; set; }            // "30", "60"
    public long? Bitrate { get; set; }
    public string Label { get; set; } = "";     // "1080p60" etc.
    public long? SizeBytes { get; set; }
    public bool IsDefault { get; set; }
    public string? Language { get; set; }
}

/// <summary>A detected video candidate surfaced by the browser sniffer.</summary>
public sealed class VideoCandidate
{
    public string PageUrl { get; set; } = "";
    public string MediaUrl { get; set; } = "";
    public TransferProtocol Protocol { get; set; }
    public string MimeType { get; set; } = "";
    public string Title { get; set; } = "";
    public long? ContentLength { get; set; }
    public List<MediaTrack> Tracks { get; set; } = new();
    public bool IsLive { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(); // Referer, cookies, UA
    public string? DetectMethod { get; set; }  // hls-manifest | dash-manifest | media-sniff | dom-video
    public string TabId { get; set; } = "";
}

/// <summary>
/// The aggregate root: one download task. Persisted in SQLite; this is the
/// single authoritative state (crash recovery rebuilds everything from it).
/// </summary>
public sealed class DownloadTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string? ReferringUrl { get; set; }
    public string? FinalUrl { get; set; }         // after redirects
    public string FileName { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public string? TempFilePath { get; set; }     // .novapart while in flight

    public TransferProtocol Protocol { get; set; } = TransferProtocol.Http;
    public DownloadState State { get; set; } = DownloadState.Created;
    public FailureReason Failure { get; set; } = FailureReason.None;
    public string? ErrorMessage { get; set; }

    public long TotalBytes { get; set; } = -1;
    public long ReceivedBytes { get; set; }
    public long SpeedBytesPerSec { get; set; }     // rolling average
    public long SpeedPeakBytesPerSec { get; set; }
    public int ActiveSegments { get; set; }
    public bool AcceptRanges { get; set; }

    public List<DownloadSegment> Segments { get; set; } = new();
    public string Category { get; set; } = "general";
    public string QueueName { get; set; } = "main";

    public string? ETag { get; set; }             // resume validation
    public string? LastModified { get; set; }
    public string? ExpectedHash { get; set; }     // "sha256:ab…" / "md5:…" / "crc32:…"
    public string? VerifiedHash { get; set; }

    public string? Username { get; set; }
    public string? Secret { get; set; }           // never logged
    public string AuthType { get; set; } = "none";
    public ProxyConfig? Proxy { get; set; }

    public Dictionary<string, string> Headers { get; set; } = new(); // UA, Referer, cookies
    public Dictionary<string, string> Metadata { get; set; } = new(); // engine-specific

    // Video / stream task extras
    public VideoCandidate? Video { get; set; }
    public string? SelectedVideoTrackId { get; set; }
    public string? SelectedAudioTrackId { get; set; }
    public bool IsSequentialPreview { get; set; }  // download first segments first for preview

    // Torrent / metalink extras
    public string? TorrentInfoHash { get; set; }
    public string? MetalinkFile { get; set; }
    public string? YtDlpFormat { get; set; }      // format selector for bridge

    // Scheduling / automation
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 10;
    public DateTime? ScheduledStart { get; set; }
    public bool ShutdownWhenComplete { get; set; }
    public int Priority { get; set; }             // higher runs first inside queue

    // Site Grabber (recursive website download)
    public int Depth { get; set; }                // 0 = single file
    public string? SiteGrabberFilter { get; set; } // glob like *.jpg;*.css

    [JsonIgnore]
    public long RemainingBytes => TotalBytes < 0 ? -1 : Math.Max(0, TotalBytes - ReceivedBytes);
    [JsonIgnore]
    public string FullPath => Path.IsPathRooted(FileName) ? FileName : Path.Combine(SaveDirectory, FileName);
    [JsonIgnore]
    public double Progress => TotalBytes <= 0
        ? (State == DownloadState.Completed ? 1.0 : 0)
        : Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1);
}

/// <summary>Live progress delta pushed to UI at 4 Hz instead of full snapshots.</summary>
public sealed class TaskProgress
{
    public string TaskId { get; set; } = "";
    public DownloadState State { get; set; }
    public long ReceivedBytes { get; set; }
    public long TotalBytes { get; set; }
    public long SpeedBytesPerSec { get; set; }
    public int ActiveSegments { get; set; }
    public long? EtaSeconds { get; set; }
    public string? Error { get; set; }
    public FailureReason Failure { get; set; }
    public long UnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
