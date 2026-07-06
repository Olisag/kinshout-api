using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceOrderingTests
{
    [Fact]
    public async Task SearchAsync_OrdersAdvertsByViewsThenRecency()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var popularOld = CreateAdvert(user, category, "Popular old", viewCount: 50, createdAt: DateTime.UtcNow.AddDays(-10));
        var popularNew = CreateAdvert(user, category, "Popular new", viewCount: 50, createdAt: DateTime.UtcNow.AddDays(-1));
        var recent = CreateAdvert(user, category, "Recent quiet", viewCount: 1, createdAt: DateTime.UtcNow);
        db.Adverts.AddRange(popularOld, popularNew, recent);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([recent.Id, popularOld.Id, popularNew.Id], [], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());
        var result = await service.SearchAsync(new SearchRequestDto("appartement", "annonces", PageSize: 10, Sort: ListSortHelper.Popular));

        Assert.Equal(["Popular new", "Popular old", "Recent quiet"], result.Adverts.Select(a => a.Title).ToArray());
    }

    [Fact]
    public async Task SearchAsync_OrdersAdvertsByRecencyWhenSortRecent()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var popularOld = CreateAdvert(user, category, "Popular old", viewCount: 50, createdAt: DateTime.UtcNow.AddDays(-10));
        var popularNew = CreateAdvert(user, category, "Popular new", viewCount: 50, createdAt: DateTime.UtcNow.AddDays(-1));
        var recent = CreateAdvert(user, category, "Recent quiet", viewCount: 1, createdAt: DateTime.UtcNow);
        db.Adverts.AddRange(popularOld, popularNew, recent);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([recent.Id, popularOld.Id, popularNew.Id], [], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());
        var result = await service.SearchAsync(new SearchRequestDto("appartement", "annonces", PageSize: 10, Sort: ListSortHelper.Recent));

        Assert.Equal(["Recent quiet", "Popular new", "Popular old"], result.Adverts.Select(a => a.Title).ToArray());
    }

    [Fact]
    public async Task SearchAsync_OrdersDiscussionsByViewsThenRecency()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var activeOld = CreateDiscussion(user, category, "Active old", viewCount: 8, createdAt: DateTime.UtcNow.AddDays(-5));
        var activeNew = CreateDiscussion(user, category, "Active new", viewCount: 8, createdAt: DateTime.UtcNow.AddDays(-1));
        var quiet = CreateDiscussion(user, category, "Quiet recent", viewCount: 1, createdAt: DateTime.UtcNow);
        db.Discussions.AddRange(activeOld, activeNew, quiet);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([], [quiet.Id, activeOld.Id, activeNew.Id], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());
        var result = await service.SearchAsync(new SearchRequestDto("quartier", "discussions", PageSize: 10, Sort: ListSortHelper.Popular));

        Assert.Equal(["Active new", "Active old", "Quiet recent"], result.Discussions.Select(d => d.Title).ToArray());
    }

    private static Advert CreateAdvert(
        User user,
        Category category,
        string title,
        int viewCount,
        DateTime createdAt)
    {
        return new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = title,
            Description = "Description",
            Location = "Gombe",
            ViewCount = viewCount,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            IsPublished = true,
        };
    }

    private static Discussion CreateDiscussion(
        User user,
        Category category,
        string title,
        int viewCount,
        DateTime createdAt)
    {
        return new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = title,
            Body = "Body",
            ViewCount = viewCount,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }
}
