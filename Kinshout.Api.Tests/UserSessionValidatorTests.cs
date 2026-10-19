using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Tests;

public class UserSessionValidatorTests
{
    [Fact]
    public async Task IsActiveAsync_AcceptsEveryTokenUntilSessionsAreRevoked()
    {
        await using var db = TestDbFactory.Create();
        var user = new User { DisplayName = "Active", Email = "active@kinoiserie.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var validator = new UserSessionValidator(db, new MemoryCache(new MemoryCacheOptions()));

        Assert.True(await validator.IsActiveAsync(user.Id, DateTime.MinValue));
        Assert.True(await validator.IsActiveAsync(user.Id, DateTime.UtcNow.AddDays(-6)));
    }

    [Fact]
    public async Task IsActiveAsync_RefusesTokensIssuedBeforeTheRevocation()
    {
        await using var db = TestDbFactory.Create();
        var revokedAt = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        var user = new User
        {
            DisplayName = "Reset",
            Email = "reset@kinoiserie.test",
            SessionsValidAfter = revokedAt,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var validator = new UserSessionValidator(db, new MemoryCache(new MemoryCacheOptions()));

        Assert.False(await validator.IsActiveAsync(user.Id, DateTime.MinValue));
        Assert.False(await validator.IsActiveAsync(user.Id, revokedAt.AddSeconds(-1)));
        Assert.True(await validator.IsActiveAsync(user.Id, revokedAt));
        Assert.True(await validator.IsActiveAsync(user.Id, revokedAt.AddMinutes(5)));
    }
}
