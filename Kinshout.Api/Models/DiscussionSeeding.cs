namespace Kinshout.Api.Models;

/// <summary>Database-backed switches shared by all API/worker instances. Missing means disabled.</summary>
public class DiscussionSeedingControl
{
    public int Id { get; set; } = 1;
    public bool GenerationEnabled { get; set; }
    public bool PublishingEnabled { get; set; }
    public bool CombinedLikesEnabled { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

public class DiscussionSeedRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public DateTime? StoppedAt { get; set; }
    public Guid? StoppedByUserId { get; set; }
}

public static class DiscussionSeedJobStatuses
{
    public const string Pending = "pending";
    public const string Published = "published";
    public const string Cancelled = "cancelled";
}

/// <summary>A durable reservation for one generated contribution; not a public comment.</summary>
public class DiscussionSeedJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SeedRunId { get; set; }
    public Guid DiscussionId { get; set; }
    public Guid PersonaUserId { get; set; }
    /// <summary>Stable hash of post, source snapshot, persona and prompt version.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Status { get; set; } = DiscussionSeedJobStatuses.Pending;
    public DateTime ScheduledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? SourceRevision { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
}
