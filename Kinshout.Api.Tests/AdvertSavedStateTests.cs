using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class AdvertSavedStateTests
{
    [Fact]
    public async Task ListAsync_SetsIsSavedForSignedInViewer()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var other = new User
        {
            Email = "other@example.com",
            DisplayName = "Other",
            WhatsAppNumber = "+243900000002",
        };
        db.Users.Add(other);
        await db.SaveChangesAsync();

        var savedAdvert = CreateAdvert(user, category, "Saved one");
        var otherAdvert = CreateAdvert(user, category, "Other one");
        db.Adverts.AddRange(savedAdvert, otherAdvert);
        db.SavedAdverts.Add(new SavedAdvert { UserId = other.Id, AdvertId = savedAdvert.Id });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var results = await service.ListAsync(viewerUserId: other.Id, pageSize: 10);

        Assert.Equal(2, results.TotalCount);
        Assert.True(results.Items.Single(a => a.Id == savedAdvert.Id).IsSaved);
        Assert.False(results.Items.Single(a => a.Id == otherAdvert.Id).IsSaved);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsIsSavedFalseWhenAnonymous()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var advert = CreateAdvert(user, category, "Test");
        db.Adverts.Add(advert);
        db.SavedAdverts.Add(new SavedAdvert { UserId = user.Id, AdvertId = advert.Id });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var dto = await service.GetByIdAsync(advert.Id);

        Assert.NotNull(dto);
        Assert.False(dto!.IsSaved);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsIsSavedForViewer()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var advert = CreateAdvert(user, category, "Test");
        db.Adverts.Add(advert);
        db.SavedAdverts.Add(new SavedAdvert { UserId = user.Id, AdvertId = advert.Id });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var dto = await service.GetByIdAsync(advert.Id, viewerUserId: user.Id);

        Assert.NotNull(dto);
        Assert.True(dto!.IsSaved);
    }

    [Fact]
    public async Task ListSavedAsync_AlwaysMarksItemsAsSaved()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var advert = CreateAdvert(user, category, "Saved");
        db.Adverts.Add(advert);
        await db.SaveChangesAsync();

        var savedService = new SavedAdvertService(db);
        await savedService.SaveAsync(user.Id, advert.Id);

        var saved = await savedService.ListSavedAsync(user.Id);
        Assert.Single(saved.Items);
        Assert.True(saved.Items[0].IsSaved);
    }

    private static AdvertService CreateService(KinshoutDbContext db)
    {
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new AdvertService(db, Mock.Of<IOpenAiService>(), moderation.Object, Mock.Of<IUploadStorage>());
    }

    private static Advert CreateAdvert(User user, Category category, string title) =>
        new()
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = title,
            Description = title,
            Location = "Gombe",
            IsPublished = true,
            Category = category,
            User = user,
        };
}
