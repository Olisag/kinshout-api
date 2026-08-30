namespace Kinshout.Api.Models;

public class CommunityMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = CommunityMemberRoles.Member;
    public string Status { get; set; } = CommunityMemberStatuses.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }

    public Community Community { get; set; } = null!;
    public User User { get; set; } = null!;
    public User? ReviewedByUser { get; set; }
}

public static class CommunityMemberRoles
{
    public const string Creator = "creator";
    public const string Moderator = "moderator";
    public const string Member = "member";

    public const int MaxModerators = 4;
}

public static class CommunityMemberStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

public static class CommunityVisibilities
{
    public const string Public = "public";
    public const string Private = "private";
}
