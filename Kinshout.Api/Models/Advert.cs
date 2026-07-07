namespace Kinshout.Api.Models;

public class Advert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid CategoryId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Price { get; set; }
    public string? Location { get; set; }
    public AdvertIntent Intent { get; set; } = AdvertIntent.Demande;
    public string? ImageUrlsJson { get; set; }
    public string? ResumeUrl { get; set; }
    public string? TagsJson { get; set; }
    public double AiConfidence { get; set; }
    public string? AiSummary { get; set; }
    public bool IsPublished { get; set; } = true;
    public int ViewCount { get; set; }
    public int LikeCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null or kinshout = native listing. Other values = imported external source.</summary>
    public string? SourceProvider { get; set; }
    public string? SourceProviderName { get; set; }
    public string? SourceExternalId { get; set; }
    public string? SourceExternalUrl { get; set; }
    public DateTime? SourceImportedAt { get; set; }
    public DateTime? SourceLastSeenAt { get; set; }
    public DateTime? SourceFirstSeenAt { get; set; }
    public string? SubcategorySlug { get; set; }
    public string? DetailsJson { get; set; }
    public string? ContactJson { get; set; }
    public string? DuplicateGroupId { get; set; }
    public DateTime? ExternalPublishedAt { get; set; }

    public bool IsExternal => !string.IsNullOrWhiteSpace(SourceProvider)
        && !SourceProvider.Equals(AdvertSourceProvider.Kinshout, StringComparison.OrdinalIgnoreCase);

    public User User { get; set; } = null!;
    public Category Category { get; set; } = null!;
}
