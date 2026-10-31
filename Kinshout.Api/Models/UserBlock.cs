namespace Kinshout.Api.Models;

/// <summary>
/// Personal block: either direction hides each user's content from the other.
/// </summary>
public class UserBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BlockerUserId { get; set; }
    public Guid BlockedUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User BlockerUser { get; set; } = null!;
    public User BlockedUser { get; set; } = null!;
}
