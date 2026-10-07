namespace Kinshout.Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    /// <summary>Password hash for email/password (Local) auth. Null for OAuth-only users.</summary>
    public string? PasswordHash { get; set; }
    /// <summary>
    /// When the user proved they own <see cref="Email"/>. Social providers count as proof; e-mail sign-ups
    /// stay null (and cannot sign in) until they open the confirmation link.
    /// </summary>
    public DateTime? EmailConfirmedAt { get; set; }
    /// <summary>SHA-256 (hex) of the pending confirmation token; the token itself is only in the e-mail.</summary>
    public string? EmailConfirmationTokenHash { get; set; }
    public DateTime? EmailConfirmationSentAt { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string? WhatsAppNumber { get; set; }
    public string DisplayPreference { get; set; } = DisplayPreferenceMode.Clair;
    public bool IsProfilePublic { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    public ICollection<UserLogin> Logins { get; set; } = [];
    public ICollection<Advert> Adverts { get; set; } = [];
    public ICollection<Discussion> Discussions { get; set; } = [];
    public ICollection<DiscussionReply> Replies { get; set; } = [];
    public ICollection<SavedAdvert> SavedAdverts { get; set; } = [];
    public ICollection<LikedDiscussion> LikedDiscussions { get; set; } = [];
    public ICollection<LikedReply> LikedReplies { get; set; } = [];
    public ICollection<Community> CreatedCommunities { get; set; } = [];
    public ICollection<CommunityMember> CommunityMemberships { get; set; } = [];
}
