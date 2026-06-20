namespace Kinshout.Api.Models;

public class DiscussionReply
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DiscussionId { get; set; }
    public Guid UserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Discussion Discussion { get; set; } = null!;
    public User User { get; set; } = null!;
}
