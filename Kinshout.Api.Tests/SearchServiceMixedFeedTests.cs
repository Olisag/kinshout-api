using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceMixedFeedTests
{
    [Fact]
    public async Task SearchAsync_AllTab_ReturnsDiscussionsOnly()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var hotDiscussion = CreateDiscussion(user, category, "Hot thread Kinshasa", viewCount: 100, createdAt: DateTime.UtcNow.AddDays(-2));
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

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());
        var result = await service.SearchAsync(new SearchRequestDto("kinshasa", "all", PageSize: 10, Sort: ListSortHelper.Popular));

        Assert.Empty(result.Adverts);
        Assert.Single(result.Discussions);
        Assert.Equal("Hot thread Kinshasa", result.Discussions[0].Title);
        Assert.True(result.Items is null || result.Items.Count == 0);
    }

    [Fact]
    public async Task SearchAsync_AllTab_PaginatesDiscussionsOnly()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var advert = CreateAdvert(user, category, "Advert", viewCount: 1, createdAt: DateTime.UtcNow.AddDays(-1));
        var discussion = CreateDiscussion(user, category, "Discussion Kinshasa", viewCount: 1, createdAt: DateTime.UtcNow);
        var older = CreateDiscussion(user, category, "Older Kinshasa thread", viewCount: 1, createdAt: DateTime.UtcNow.AddHours(-2));
        db.Adverts.Add(advert);
        db.Discussions.AddRange(discussion, older);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([advert.Id], [discussion.Id, older.Id], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());

        var page1 = await service.SearchAsync(new SearchRequestDto("kinshasa", "all", Page: 1, PageSize: 1));
        Assert.Empty(page1.Adverts);
        Assert.Single(page1.Discussions);
        Assert.True(page1.Pagination.HasMoreDiscussions);
        Assert.Equal(2, page1.Pagination.TotalDiscussions);

        var page2 = await service.SearchAsync(new SearchRequestDto("kinshasa", "all", Page: 2, PageSize: 1));
        Assert.Empty(page2.Adverts);
        Assert.Single(page2.Discussions);
        Assert.False(page2.Pagination.HasMoreDiscussions);
        Assert.DoesNotContain(page2.Discussions, d => d.Id == page1.Discussions[0].Id);
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
            Location = "Gombe, Kinshasa",
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
            Body = title.Contains("Kinshasa", StringComparison.OrdinalIgnoreCase)
                ? "Discussion communautaire à Kinshasa."
                : "Body",
            ViewCount = viewCount,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
}
