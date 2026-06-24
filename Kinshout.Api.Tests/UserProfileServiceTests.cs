using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class UserProfileServiceTests
{
    [Fact]
    public async Task GetPublicProfileAsync_PrivateUser_ReturnsNull()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        user.IsProfilePublic = false;
        await db.SaveChangesAsync();

        var service = new UserProfileService(db);
        var profile = await service.GetPublicProfileAsync(user.Id);

        Assert.Null(profile);
    }

    [Fact]
    public async Task GetPublicProfileAsync_PublicUser_ReturnsProfile()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        user.IsProfilePublic = true;
        db.Adverts.Add(new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Studio",
            Description = "Desc",
            IsPublished = true,
            Category = category,
            User = user,
        });
        await db.SaveChangesAsync();

        var service = new UserProfileService(db);
        var profile = await service.GetPublicProfileAsync(user.Id);

        Assert.NotNull(profile);
        Assert.Equal(user.DisplayName, profile!.DisplayName);
        Assert.Equal(1, profile.PublishedAdvertCount);
    }

    [Fact]
    public async Task ListPublicAdvertsAsync_PrivateUser_Throws()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        user.IsProfilePublic = false;
        await db.SaveChangesAsync();

        var service = new UserProfileService(db);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ListPublicAdvertsAsync(user.Id));
    }
}
