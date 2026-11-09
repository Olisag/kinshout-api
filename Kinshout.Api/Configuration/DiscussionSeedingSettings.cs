namespace Kinshout.Api.Configuration;

public sealed class DiscussionSeedingSettings
{
    // Deployment gate in addition to the database switches. Never enabled by a migration.
    public bool Enabled { get; set; }
    public int TickSeconds { get; set; } = 60;
    public int MaxCommentsPerPost { get; set; } = 8;
    public int MaxCommentsPerDay { get; set; } = 100;
    public int StopAfterOrganicReplies { get; set; } = 3;
    public int MaxJobsPerTick { get; set; } = 4;
    public int MinDelaySeconds { get; set; } = 60;
    public int MaxDelaySeconds { get; set; } = 600;
}
