namespace Kinshout.Api.Models;

/// <summary>
/// First-class uploaded video with a poster for feed previews.
/// Direct uploads land as a source file that the video worker trims to at most two minutes and
/// re-encodes to 720p; legacy uploads are stored as-is and streamed with HTTP range requests.
/// </summary>
public class VideoAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    /// <summary>Relative storage path, e.g. /uploads/videos/{userId}/{file}.mp4</summary>
    public string VideoUrl { get; set; } = "";
    /// <summary>Optional small WebP/JPEG poster for feed preview without loading the video.</summary>
    public string? PosterUrl { get; set; }
    public string ContentType { get; set; } = "video/mp4";
    public long ByteSize { get; set; }
    public string? OriginalFileName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    /// <summary>One of <see cref="VideoAssetStatus"/>; only ready videos can be played.</summary>
    public string Status { get; set; } = VideoAssetStatus.Ready;
    /// <summary>Uploaded original awaiting processing; deleted once the processed file exists.</summary>
    public string? SourceUrl { get; set; }
    public double? TrimStartSeconds { get; set; }
    public double? TrimEndSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? ProcessingError { get; set; }

    public User User { get; set; } = null!;
}

public static class VideoAssetStatus
{
    public const string Uploading = "uploading";
    public const string Processing = "processing";
    public const string Ready = "ready";
    public const string Failed = "failed";
}
