namespace Kinshout.Api.Models;

public class DiscussionReply
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DiscussionId { get; set; }
    public Guid UserId { get; set; }
    public string Body { get; set; } = string.Empty;
    /// <summary>Optional image attachment URL (mutually exclusive with video/location).</summary>
    public string? ImageUrl { get; set; }
    /// <summary>Optional video attachment URL (mutually exclusive with image/location).</summary>
    public string? VideoUrl { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Optional short label for a location attachment.</summary>
    public string? PlaceName { get; set; }
    /// <summary>Optional address text for a location attachment.</summary>
    public string? Address { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Discussion Discussion { get; set; } = null!;
    public User User { get; set; } = null!;
}
