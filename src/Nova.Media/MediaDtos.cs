namespace Nova.Media;

public sealed class MediaProbeRequest
{
    public string Url { get; set; } = string.Empty;
    public string? Referer { get; set; }
}

public sealed class MediaCandidatesResponse
{
    public string PageUrl { get; set; } = string.Empty;
    public List<MediaCandidate> Candidates { get; set; } = new();
    public bool DrmDetected { get; set; }
}

public sealed class MediaCandidate
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Kind { get; set; } = "file";          // file | hls | dash
    public string? Container { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? SizeBytes { get; set; }
    public bool SizeIsEstimate { get; set; }
    public string? Bitrate { get; set; }
    public string? Fps { get; set; }
    public bool HasAudio { get; set; } = true;
    public bool HasVideo { get; set; } = true;
    public string? Codecs { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool Downloadable { get; set; } = true;
    public string? Note { get; set; }
}
