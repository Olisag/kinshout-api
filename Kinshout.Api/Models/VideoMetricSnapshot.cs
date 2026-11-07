namespace Kinshout.Api.Models;

/// <summary>Hourly YouTube view/like/comment sample used for freshness × engagement.</summary>
public class VideoMetricSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = "youtube";
    public string ExternalId { get; set; } = "";
    public int Views { get; set; }
    public int Likes { get; set; }
    public int Comments { get; set; }
    public int TrendScore { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}
