namespace Kinshout.Api.Models;

public class DiscussionParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DiscussionId { get; set; }
    public Guid UserId { get; set; }
    public string Status { get; set; } = CommunityMemberStatuses.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }

    public Discussion Discussion { get; set; } = null!;
    public User User { get; set; } = null!;
    public User? ReviewedByUser { get; set; }
}
