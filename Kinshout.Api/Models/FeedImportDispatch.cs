namespace Kinshout.Api.Models;

/// <summary>One claimed Kinshasa slot. The row is written before GitHub is called so a restart cannot start the same scrape twice.</summary>
public class FeedImportDispatch
{
    public string SlotKey { get; set; } = "";

    public string Workflow { get; set; } = "";

    public DateTime DispatchedAt { get; set; }
}
