namespace Kinshout.Api.Configuration;

public sealed class DiscussionSeedingSettings
{
    // Developer-owned global switch. The worker initializes its internal run automatically when enabled.
    public bool Enabled { get; set; }
    public int TickSeconds { get; set; } = 60;
    public int StopAfterOrganicReplies { get; set; } = 3;
    public int MaxJobsPerTick { get; set; } = 4;
    public int MinDelaySeconds { get; set; } = 60;
    public int MaxDelaySeconds { get; set; } = 600;
}
