namespace Kinshout.Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
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
}
