namespace Kinshout.Api.Models;

public class LikedReply
{
    public Guid UserId { get; set; }
    public Guid ReplyId { get; set; }
    public DateTime LikedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public DiscussionReply Reply { get; set; } = null!;
}
