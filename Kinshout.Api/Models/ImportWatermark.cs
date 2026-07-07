namespace Kinshout.Api.Models;

public class ImportWatermark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ImportKind { get; set; } = "";
    public string Provider { get; set; } = "";
    public DateTime LastRunAtUtc { get; set; }
}
