namespace Kinshout.Api.Models;

public class LikedDiscussion
{
    public Guid UserId { get; set; }
    public Guid DiscussionId { get; set; }
    public DateTime LikedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public Discussion Discussion { get; set; } = null!;
}
