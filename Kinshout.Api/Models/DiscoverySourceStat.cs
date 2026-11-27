namespace Kinshout.Api.Models;

/// <summary>What a verified source has produced, so the next run can prefer it without ignoring the quiet ones.</summary>
public class DiscoverySourceStat
{
    public string Key { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Handle { get; set; } = "";
    public int Checks { get; set; }
    public int Kept { get; set; }
    public int Imports { get; set; }
    public DateTime LastCheckedAt { get; set; } = DateTime.UtcNow;
}
