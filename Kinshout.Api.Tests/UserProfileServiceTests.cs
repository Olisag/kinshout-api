using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class UserProfileServiceTests
{
    private const string ApiBase = "https://api.test";

    private static UploadUrlResolver CreateUploadUrlResolver() =>
        new(
            Options.Create(new UploadStorageSettings { PublicBaseUrl = ApiBase }),
            Mock.Of<IHttpContextAccessor>());
    [Fact]
    public async Task GetPublicProfileAsync_PrivateUser_ReturnsNull()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        user.IsProfilePublic = false;
        await db.SaveChangesAsync();

        var service = new UserProfileService(db, CreateUploadUrlResolver());
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

        var service = new UserProfileService(db, CreateUploadUrlResolver());
        var profile = await service.GetPublicProfileAsync(user.Id);

        Assert.NotNull(profile);
        Assert.Equal(user.DisplayName, profile!.DisplayName);
        Assert.Equal(1, profile.PublishedAdvertCount);
    }

    [Fact]
    public async Task GetPublicProfileAsync_ReturnsAbsoluteAvatarUrl()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        user.IsProfilePublic = true;
        user.AvatarUrl = $"/uploads/avatars/{user.Id:N}/face.png";
        await db.SaveChangesAsync();

        var service = new UserProfileService(db, CreateUploadUrlResolver());
        var profile = await service.GetPublicProfileAsync(user.Id);

        Assert.NotNull(profile);
        Assert.Equal($"{ApiBase}/uploads/avatars/{user.Id:N}/face.png", profile!.AvatarUrl);
    }

    [Fact]
    public async Task ListPublicAdvertsAsync_PrivateUser_Throws()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        user.IsProfilePublic = false;
        await db.SaveChangesAsync();

        var service = new UserProfileService(db, CreateUploadUrlResolver());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ListPublicAdvertsAsync(user.Id));
    }
}
