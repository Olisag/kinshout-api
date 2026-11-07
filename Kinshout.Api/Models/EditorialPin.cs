namespace Kinshout.Api.Models;

public class EditorialPin
{
    public string Key { get; set; } = "gros-plan";
    public Guid? DiscussionId { get; set; }
    public string? Window { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
