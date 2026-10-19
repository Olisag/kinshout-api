using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceDiscussionsOnlyTests
{
    [Fact]
    public async Task SearchAsync_NeverReturnsAdvertsEvenWhenTabIsAll()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var advert = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Appartement Gombe",
            Description = "Bel appartement à Kinshasa",
            Location = "Gombe, Kinshasa",
            IsPublished = true,
        };
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Discussion appartement Gombe",
            Body = "Qui cherche un appartement à Kinshasa ?",
        };
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

        var service = new SearchService(
            db,
            openAi.Object,
            TestDbFactory.CreateMemoryCache(),
            TestDbFactory.CreateAdvertDtoMapper());

        var result = await service.SearchAsync(new SearchRequestDto("appartement", "all"));

        Assert.Empty(result.Adverts);
        Assert.Single(result.Discussions);
        Assert.Equal(discussion.Id, result.Discussions[0].Id);
        Assert.True(result.Items is null || result.Items.Count == 0 || result.Items.All(i => i.Advert is null));
    }

    [Fact]
    public async Task GetRecentSearchesAsync_OrdersByLastSearchedAt()
    {
        await using var db = TestDbFactory.Create();
        db.SearchQueryStats.AddRange(
            new SearchQueryStat
            {
                NormalizedQuery = "old query",
                DisplayQuery = "Old query",
                SearchCount = 99,
                LastSearchedAt = DateTime.UtcNow.AddDays(-2),
            },
            new SearchQueryStat
            {
                NormalizedQuery = "new query",
                DisplayQuery = "New query",
                SearchCount = 1,
                LastSearchedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var service = new SearchService(
            db,
            Mock.Of<IOpenAiService>(),
            TestDbFactory.CreateMemoryCache(),
            TestDbFactory.CreateAdvertDtoMapper());

        var recent = await service.GetRecentSearchesAsync(1, 10);

        Assert.Equal(2, recent.Items.Count);
        Assert.Equal("New Query", recent.Items[0].Query);
        Assert.Equal("Old Query", recent.Items[1].Query);
    }
}
