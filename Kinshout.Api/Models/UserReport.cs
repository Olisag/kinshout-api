namespace Kinshout.Api.Models;

/// <summary>Report of a user within a community, for moderator review.</summary>
public class UserReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid ReporterUserId { get; set; }
    public Guid ReportedUserId { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = UserReportStatuses.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }

    public Community Community { get; set; } = null!;
    public User ReporterUser { get; set; } = null!;
    public User ReportedUser { get; set; } = null!;
    public User? ResolvedByUser { get; set; }
}

public static class UserReportStatuses
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Dismissed = "dismissed";
}
