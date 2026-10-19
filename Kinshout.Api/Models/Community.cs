namespace Kinshout.Api.Models;

/// <summary>Reddit-style local community. Public route is <c>k/{Slug}</c>.</summary>
public class Community
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Slug without the <c>k/</c> prefix (e.g. <c>community1</c>). No spaces.</summary>
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = CommunityVisibilities.Public;
    public bool IsActive { get; set; } = true;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User CreatedByUser { get; set; } = null!;
    public ICollection<Discussion> Discussions { get; set; } = [];
    public ICollection<CommunityMember> Members { get; set; } = [];
}
