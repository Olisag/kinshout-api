namespace Kinshout.Api.Models;

public class Discussion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? CategoryId { get; set; }
    /// <summary>AI-assigned discussion topic slug (sport, politique, etc.).</summary>
    public string? TopicSlug { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int ReplyCount { get; set; }
    public int LikeCount { get; set; }
    public int ViewCount { get; set; }

    /// <summary>Null or kinshout = native topic. Other values = imported external post.</summary>
    public string? SourceProvider { get; set; }
    public string? SourceProviderName { get; set; }
    public string? SourceExternalId { get; set; }
    public string? SourceExternalUrl { get; set; }
    public DateTime? SourceImportedAt { get; set; }
    public DateTime? SourceLastSeenAt { get; set; }
    public DateTime? SourceFirstSeenAt { get; set; }
    public string? SourceOriginalAuthor { get; set; }
    public int? SourceEngagementScore { get; set; }
    public DateTime? ExternalPublishedAt { get; set; }
    /// <summary>Original post text before AI discussion transform (audit).</summary>
    public string? SourceRawBody { get; set; }

    public bool IsExternal => !string.IsNullOrWhiteSpace(SourceProvider)
        && !SourceProvider.Equals(DiscussionSourceProvider.Kinshout, StringComparison.OrdinalIgnoreCase);

    public User User { get; set; } = null!;
    public Category? Category { get; set; }
    public ICollection<DiscussionReply> Replies { get; set; } = [];
}
