using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Services;

/// <summary>Refuses user JWTs issued before the user's sessions were revoked (see <see cref="Models.User.SessionsValidAfter"/>).</summary>
public interface IUserSessionValidator
{
    /// <remarks><paramref name="issuedAtUtc"/> is the token's <c>iat</c>; <see cref="DateTime.MinValue"/> for tokens issued without one.</remarks>
    Task<bool> IsActiveAsync(Guid userId, DateTime issuedAtUtc, CancellationToken ct = default);
}

public class UserSessionValidator(KinshoutDbContext db, IMemoryCache cache) : IUserSessionValidator
{
    /// <summary>How long another API instance may keep honouring a token after a reset.</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    public async Task<bool> IsActiveAsync(Guid userId, DateTime issuedAtUtc, CancellationToken ct = default)
    {
        var validAfter = await cache.GetOrCreateAsync(
            $"user-sessions-valid-after:{userId:N}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;
                return await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => u.SessionsValidAfter)
                    .FirstOrDefaultAsync(ct);
            });

        return validAfter is not { } cutoff || issuedAtUtc >= cutoff;
    }
}
