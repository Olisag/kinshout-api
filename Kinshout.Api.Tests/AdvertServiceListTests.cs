using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace Kinshout.Api.Tests;

public class AdvertServiceListTests
{
    [Fact]
    public async Task ListAsync_OrdersByPopularThenRecent()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Adverts.AddRange(
            CreateAdvert(user, category, "Old popular", viewCount: 20, createdAt: DateTime.UtcNow.AddDays(-5)),
            CreateAdvert(user, category, "New popular", viewCount: 20, createdAt: DateTime.UtcNow.AddDays(-1)),
            CreateAdvert(user, category, "Recent quiet", viewCount: 1, createdAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var results = await service.ListAsync(sort: ListSortHelper.Popular, pageSize: 10);

        Assert.Equal(3, results.TotalCount);
        Assert.Equal(["New popular", "Old popular", "Recent quiet"], results.Items.Select(a => a.Title).ToArray());
    }

    [Fact]
    public async Task ListAsync_PaginatesResults()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        for (var i = 0; i < 5; i++)
        {
            db.Adverts.Add(CreateAdvert(user, category, $"Advert {i}", viewCount: i, createdAt: DateTime.UtcNow.AddDays(-i)));
        }

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var page1 = await service.ListAsync(page: 1, pageSize: 2, sort: ListSortHelper.Recent);
        var page2 = await service.ListAsync(page: 2, pageSize: 2, sort: ListSortHelper.Recent);

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.True(page1.HasMore);
        Assert.Equal(2, page2.Items.Count);
        Assert.True(page2.HasMore);
    }

    [Fact]
    public async Task ListAsync_FiltersByIntent()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Adverts.AddRange(
            CreateAdvert(user, category, "Offre immo", viewCount: 1, createdAt: DateTime.UtcNow, intent: AdvertIntent.Offre),
            CreateAdvert(user, category, "Demande immo", viewCount: 1, createdAt: DateTime.UtcNow, intent: AdvertIntent.Demande));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var offres = await service.ListAsync(intent: "offre");
        var demandes = await service.ListAsync(intent: "demande");

        Assert.Single(offres.Items);
        Assert.Equal("Offre immo", offres.Items[0].Title);
        Assert.Equal("offre", offres.Items[0].Intent);
        Assert.Single(demandes.Items);
        Assert.Equal("Demande immo", demandes.Items[0].Title);
        Assert.Equal("demande", demandes.Items[0].Intent);
    }

    private static AdvertService CreateService(KinshoutDbContext db)
    {
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        moderation.Setup(m => m.EnsureImageAllowedAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var root = Path.Combine(Path.GetTempPath(), "kinshout-advert-list-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(Path.Combine(root, "wwwroot"));
        env.Setup(e => e.ContentRootPath).Returns(root);
        var storage = new LocalUploadStorage(env.Object, Mock.Of<ILogger<LocalUploadStorage>>());

        return new AdvertService(db, Mock.Of<IOpenAiService>(), moderation.Object, storage);
    }

    private static Advert CreateAdvert(
        User user,
        Category category,
        string title,
        int viewCount,
        DateTime createdAt,
        AdvertIntent intent = AdvertIntent.Demande) =>
        new()
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = title,
            Description = "Description",
            Category = category,
            User = user,
            ViewCount = viewCount,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            IsPublished = true,
            Intent = intent,
        };
}
