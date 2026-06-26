using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceFilterTests
{
    [Fact]
    public async Task SearchAsync_FiltersAdvertsByIntentOffre()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var offre = CreateAdvert(user, category, "Offre advert", AdvertIntent.Offre);
        var demande = CreateAdvert(user, category, "Demande advert", AdvertIntent.Demande);
        db.Adverts.AddRange(offre, demande);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([offre.Id, demande.Id], [], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache());
        var result = await service.SearchAsync(new SearchRequestDto("appartement", "all", Intent: SearchIntentHelper.Offre));

        Assert.Single(result.Adverts);
        Assert.Equal("Offre advert", result.Adverts[0].Title);
        Assert.Empty(result.Discussions);
    }

    [Fact]
    public async Task SearchAsync_FiltersAdvertsByIntentDemande()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var offre = CreateAdvert(user, category, "Offre advert", AdvertIntent.Offre);
        var demande = CreateAdvert(user, category, "Demande advert", AdvertIntent.Demande);
        db.Adverts.AddRange(offre, demande);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([offre.Id, demande.Id], [], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache());
        var result = await service.SearchAsync(new SearchRequestDto("appartement", "all", Intent: SearchIntentHelper.Demande));

        Assert.Single(result.Adverts);
        Assert.Equal("Demande advert", result.Adverts[0].Title);
        Assert.Empty(result.Discussions);
    }

    [Fact]
    public async Task SearchAsync_IntentDiscussionKeepsDiscussionsAndDiscussionAdverts()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var offre = CreateAdvert(user, category, "Offre advert", AdvertIntent.Offre);
        var discussionAdvert = CreateAdvert(user, category, "Discussion advert", AdvertIntent.Discussion);
        var discussion = CreateDiscussion(user, category, "Forum thread");
        db.Adverts.AddRange(offre, discussionAdvert);
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([offre.Id, discussionAdvert.Id], [discussion.Id], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache());
        var result = await service.SearchAsync(new SearchRequestDto("quartier", "all", Intent: SearchIntentHelper.Discussion));

        Assert.Single(result.Adverts);
        Assert.Equal("Discussion advert", result.Adverts[0].Title);
        Assert.Single(result.Discussions);
        Assert.Equal("Forum thread", result.Discussions[0].Title);
    }

    private static Advert CreateAdvert(
        User user,
        Category category,
        string title,
        AdvertIntent intent)
    {
        return new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = title,
            Description = "Description",
            Location = "Gombe",
            Intent = intent,
            IsPublished = true,
        };
    }

    private static Discussion CreateDiscussion(User user, Category category, string title) =>
        new()
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = title,
            Body = "Body",
        };
}
