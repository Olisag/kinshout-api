namespace Kinshout.Api.Configuration;

public sealed class DiscussionSeedingSettings
{
    // Developer-owned global switch. The worker initializes its internal run automatically when enabled.
    public bool Enabled { get; set; }
    public bool SimulationEnabled { get; set; }
    public int ProfileCount { get; set; } = 1500;
    public int DailyComments { get; set; } = 150;
    public bool ArchiveSeededCommunityContent { get; set; }
    public int DailyDiscussionPosts { get; set; }
    public int WeeklyPublicCommunities { get; set; }
    public int MaxLikesPerTick { get; set; } = 4;
    public int DailyLikes { get; set; } = 1200;
    // Conservative reservation per inference attempt, not a provider invoice.
    public decimal EstimatedCostPerAttemptUsd { get; set; } = 0.01m;
    public decimal DailyCostLimitUsd { get; set; } = 3m;
    public int MaxDailyGenerationAttempts { get; set; } = 300;
    public int TickSeconds { get; set; } = 60;
    public int StopAfterOrganicReplies { get; set; } = 3;
    public int MaxJobsPerTick { get; set; } = 4;
    public int MinDelaySeconds { get; set; } = 60;
    public int MaxDelaySeconds { get; set; } = 600;
}
