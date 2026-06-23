namespace Kinshout.Api.Models;

public class SavedAdvert
{
    public Guid UserId { get; set; }
    public Guid AdvertId { get; set; }
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public Advert Advert { get; set; } = null!;
}
