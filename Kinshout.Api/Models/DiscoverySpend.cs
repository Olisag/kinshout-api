namespace Kinshout.Api.Models;

/// <summary>Results asked of a provider during one Kinshasa day or week. Import caps are counted from posts.</summary>
public class DiscoverySpend
{
    public string PeriodKey { get; set; } = "";
    public int ResultsRequested { get; set; }
}
