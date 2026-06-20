namespace Kinshout.Api.Models;

public class SearchQueryStat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string NormalizedQuery { get; set; } = string.Empty;
    public string DisplayQuery { get; set; } = string.Empty;
    public int SearchCount { get; set; } = 1;
    public DateTime LastSearchedAt { get; set; } = DateTime.UtcNow;
}
