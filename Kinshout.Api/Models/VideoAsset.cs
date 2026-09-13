namespace Kinshout.Api.Models;

/// <summary>
/// First-class uploaded video with optional cheap poster for feed previews.
/// Original file is stored once (no cloud transcoding) and streamed with HTTP range requests.
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

    public User User { get; set; } = null!;
}
