namespace Kinshout.Api.Models;

public class Category
{
    public const string DiscussionSlug = "discussion";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Icon { get; set; } = "📦";
    public bool IsSystem { get; set; }
    public bool IsAiGenerated { get; set; }
    /// <summary>Topic bucket for discussions (sport, politique, etc.). Distinct from advert browse categories.</summary>
    public bool IsDiscussionTopic { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Advert> Adverts { get; set; } = [];
    public ICollection<Discussion> Discussions { get; set; } = [];
}
