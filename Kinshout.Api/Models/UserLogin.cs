namespace Kinshout.Api.Models;

public class UserLogin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AuthProvider Provider { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
