using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceMixedFeedTests
{
    [Fact]
    public async Task SearchAsync_AllTab_ReturnsMixedFeedSortedByPopularity()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var hotDiscussion = CreateDiscussion(user, category, "Hot thread", viewCount: 100, createdAt: DateTime.UtcNow.AddDays(-2));
        var quietAdvert = CreateAdvert(user, category, "Quiet advert", viewCount: 5, createdAt: DateTime.UtcNow);
        var warmAdvert = CreateAdvert(user, category, "Warm advert", viewCount: 40, createdAt: DateTime.UtcNow.AddDays(-1));
        db.Adverts.AddRange(quietAdvert, warmAdvert);
        db.Discussions.Add(hotDiscussion);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis(
                [quietAdvert.Id, warmAdvert.Id],
                [hotDiscussion.Id],
                ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache());
        var result = await service.SearchAsync(new SearchRequestDto("kinshasa", "all", PageSize: 10, Sort: ListSortHelper.Popular));

        Assert.NotNull(result.Items);
        Assert.Empty(result.Adverts);
        Assert.Empty(result.Discussions);
        Assert.Equal(["Hot thread", "Warm advert", "Quiet advert"], result.Items!.Select(i => i.Advert?.Title ?? i.Discussion?.Title).ToArray());
        Assert.Equal(3, result.Pagination.TotalItems);
        Assert.False(result.Pagination.HasMore);
    }

    [Fact]
    public async Task SearchAsync_AllTab_PaginatesMixedFeed()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var advert = CreateAdvert(user, category, "Advert", viewCount: 1, createdAt: DateTime.UtcNow.AddDays(-1));
        var discussion = CreateDiscussion(user, category, "Discussion", viewCount: 1, createdAt: DateTime.UtcNow);
        db.Adverts.Add(advert);
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([advert.Id], [discussion.Id], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache());

        var page1 = await service.SearchAsync(new SearchRequestDto("kinshasa", "all", Page: 1, PageSize: 1));
        Assert.Single(page1.Items);
        Assert.Equal("Discussion", page1.Items![0].Discussion?.Title);
        Assert.True(page1.Pagination.HasMore);
        Assert.Equal(2, page1.Pagination.TotalItems);

        var page2 = await service.SearchAsync(new SearchRequestDto("kinshasa", "all", Page: 2, PageSize: 1));
        Assert.Single(page2.Items);
        Assert.Equal("Advert", page2.Items![0].Advert?.Title);
        Assert.False(page2.Pagination.HasMore);
    }

    private static Advert CreateAdvert(
        User user,
        Category category,
        string title,
        int viewCount,
        DateTime createdAt) =>
        new()
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

    private static Discussion CreateDiscussion(
        User user,
        Category category,
        string title,
        int viewCount,
        DateTime createdAt) =>
        new()
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
