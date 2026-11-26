namespace Kinshout.Api.Configuration;

public sealed class FeedImportDispatchSettings
{
    public const string SectionName = "FeedImportDispatch";

    /// <summary>The API clock starts the GitHub import workflows. It does nothing without a token.</summary>
    public bool Enabled { get; set; }

    public string Repository { get; set; } = "Olisag/kinshout-api";

    public string Ref { get; set; } = "Master";

    public string GitHubToken { get; set; } = "";

    /// <summary>A slot that is this many minutes late is left for the next one, so a late start cannot stack a second paid scrape.</summary>
    public int CatchUpMinutes { get; set; } = 90;

    public int TickSeconds { get; set; } = 60;
}
