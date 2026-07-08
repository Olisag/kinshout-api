using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceMarketplaceQueryTests
{
    [Fact]
    public async Task SearchAsync_KidsClothesSaleQuery_ExcludesUnrelatedDiscussions()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var mode = new Category { Slug = "mode", Label = "Mode", Icon = "👗" };
        var discussions = new Category { Slug = Category.DiscussionSlug, Label = "Discussions", Icon = "💬", IsSystem = true };
        db.Categories.AddRange(mode, discussions);
        await db.SaveChangesAsync();

        db.Adverts.Add(new Advert
        {
            UserId = user.Id,
            CategoryId = mode.Id,
            Title = "Vente de vêtements pour enfants",
            Description = "Tailles 4-8 ans",
            Location = "Kinshasa",
            IsPublished = true,
        });
        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = discussions.Id,
            Title = "Assassinat d'une femme de RDC à Brazzaville",
            Body = "Une femme congolaise a été tuée. Les autorités enquêtent.",
            ViewCount = 50_000,
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

        var result = await service.SearchAsync(new SearchRequestDto("Vente vetement pour enfant", "all"));

        Assert.Empty(result.Discussions);
        if (result.Items is not null)
        {
            Assert.DoesNotContain(
                result.Items,
                item => item.Discussion?.Title.Contains("Assassinat", StringComparison.OrdinalIgnoreCase) == true);
        }
        openAi.Verify(
            x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.Is<IReadOnlyList<Discussion>>(discussions => discussions.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Parse_VenteWithoutDe_ExtractsOfferSubject()
    {
        var parsed = SearchQueryParser.Parse("Vente vetement pour enfant");

        Assert.True(parsed.MatchedPattern);
        Assert.Equal(SearchIntentHelper.Offre, parsed.IntentHint);
        Assert.Contains("vetement", parsed.SubjectText, StringComparison.Ordinal);
        Assert.Contains("enfant", parsed.SubjectText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Vente vetement pour enfant", false)]
    [InlineData("discussion assassinat brazzaville", true)]
    [InlineData("quartier gombe avis", true)]
    public void ShouldSearchDiscussions_RespectsMarketplaceVsTopicQueries(string query, bool expected)
    {
        var hints = SearchQueryResolver.ParseHints(query);
        var include = SearchDiscussionScope.ShouldSearchDiscussions(
            new SearchRequestDto(query, "all"),
            hints,
            query);

        Assert.Equal(expected, include);
    }
}
