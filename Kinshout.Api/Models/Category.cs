namespace Kinshout.Api.Models;

public class Category
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Icon { get; set; } = "📦";
    public bool IsSystem { get; set; }
    public bool IsAiGenerated { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Advert> Adverts { get; set; } = [];
    public ICollection<Discussion> Discussions { get; set; } = [];
}
