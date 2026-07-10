using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchAccentInsensitivityTests
{
    [Fact]
    public async Task SearchAsync_Felix_FindsDiscussionWithAccentedName()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Les initiatives diplomatiques de Félix Tshisekedi",
            Body = "Analyse des récentes actions du président sur la scène internationale.",
            ViewCount = 42,
        });
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([], [], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());

        var result = await service.SearchAsync(new SearchRequestDto("Felix", "all"));

        Assert.NotNull(result.Items);
        Assert.Contains(
            result.Items,
            item => item.Discussion?.Title.Contains("Félix Tshisekedi", StringComparison.Ordinal) == true);
    }
}
