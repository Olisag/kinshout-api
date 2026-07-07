using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceDiscussionsKinshasaTests
{
    [Fact]
    public async Task SearchAsync_DiscussionsTab_Kinshasa_UsesSqlFilterAndSkipsOpenAi()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var kinshasaThread = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Traffic update",
            Body = "Heavy traffic on boulevard du 30 juin in Kinshasa today.",
            ViewCount = 50,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var unrelatedThread = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Lubumbashi mining news",
            Body = "Copper exports rose this quarter.",
            ViewCount = 100,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Discussions.AddRange(kinshasaThread, unrelatedThread);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());

        var result = await service.SearchAsync(new SearchRequestDto("Kinshasa", "discussions"));

        Assert.Single(result.Discussions);
        Assert.Equal(kinshasaThread.Id, result.Discussions[0].Id);
        Assert.Empty(result.Adverts);
        openAi.Verify(
            x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
