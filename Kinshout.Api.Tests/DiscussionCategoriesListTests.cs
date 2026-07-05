using Kinshout.Api.Controllers;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace Kinshout.Api.Tests;

public class DiscussionCategoriesListTests
{
    private static DiscussionsController CreateController(
        KinshoutDbContext db,
        IMemoryCache cache,
        IOpenAiService? openAi = null,
        IDiscussionTopicBackfillScheduler? backfill = null) =>
        new(
            db,
            cache,
            openAi ?? Mock.Of<IOpenAiService>(),
            Mock.Of<IDiscussionService>(),
            Mock.Of<ILikedDiscussionService>(),
            backfill ?? Mock.Of<IDiscussionTopicBackfillScheduler>());

    [Fact]
    public async Task ListCategories_ReturnsOnlyDiscussionTopicsWithDiscussions()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var sport = new Category
        {
            Slug = "sport",
            Label = "Sport & foot",
            Icon = "⚽",
            IsAiGenerated = true,
            IsDiscussionTopic = true,
        };
        var advertCategory = new Category
        {
            Slug = "telephones",
            Label = "Téléphones",
            Icon = "📱",
            IsAiGenerated = true,
        };
        db.Categories.AddRange(sport, advertCategory);
        await db.SaveChangesAsync();

        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = sport.Id,
            TopicSlug = sport.Slug,
            Title = "Retour des Léopards",
            Body = "Comment accueillez-vous ce retour ?",
        });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        var result = await CreateController(db, cache).ListCategories();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paged = Assert.IsType<PagedResultDto<CategoryDto>>(ok.Value);
        Assert.Single(paged.Items);
        Assert.Equal("sport", paged.Items[0].Slug);
        Assert.DoesNotContain(paged.Items, c => c.Slug == "telephones");
    }

    [Fact]
    public async Task ListCategories_SchedulesBackfillWithoutBlocking()
    {
        await using var db = TestDbFactory.Create();
        var (user, legacyCategory) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        legacyCategory.Slug = Category.DiscussionSlug;
        legacyCategory.Label = "Discussions";
        legacyCategory.Icon = "💬";
        legacyCategory.IsSystem = true;
        await db.SaveChangesAsync();

        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = legacyCategory.Id,
            Title = "Résultats EXETAT 2026 Kinshasa Lukunga",
            Body = "Les conditions d'examen vous semblent-elles équitables ?",
        });
        await db.SaveChangesAsync();

        var backfill = new Mock<IDiscussionTopicBackfillScheduler>();
        var cache = TestDbFactory.CreateMemoryCache();
        var result = await CreateController(db, cache, backfill: backfill.Object).ListCategories();

        Assert.IsType<OkObjectResult>(result.Result);
        backfill.Verify(b => b.ScheduleBatchBackfill(), Times.Once);

        var discussion = await db.Discussions.SingleAsync();
        Assert.Null(discussion.TopicSlug);
    }

    [Fact]
    public async Task BackfillUncategorizedAsync_AssignsLegacyDiscussionsWithKeywords()
    {
        await using var db = TestDbFactory.Create();
        var (user, legacyCategory) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        legacyCategory.Slug = Category.DiscussionSlug;
        legacyCategory.Label = "Discussions";
        legacyCategory.Icon = "💬";
        legacyCategory.IsSystem = true;
        await db.SaveChangesAsync();

        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = legacyCategory.Id,
            Title = "Résultats EXETAT 2026 Kinshasa Lukunga",
            Body = "Les conditions d'examen vous semblent-elles équitables ?",
        });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        var processed = await AiDiscussionCategoryCatalog.BackfillUncategorizedAsync(db, cache);

        Assert.Equal(1, processed);

        var discussion = await db.Discussions.SingleAsync();
        Assert.Equal("education", discussion.TopicSlug);
    }

    [Fact]
    public async Task ListCategories_PutsAutresCategoryLastRegardlessOfPopularity()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var autres = new Category
        {
            Slug = "autres",
            Label = "Autres",
            Icon = "📦",
            IsAiGenerated = true,
            IsDiscussionTopic = true,
        };
        var sport = new Category
        {
            Slug = "sport",
            Label = "Sport & foot",
            Icon = "⚽",
            IsAiGenerated = true,
            IsDiscussionTopic = true,
        };
        db.Categories.AddRange(autres, sport);
        await db.SaveChangesAsync();

        db.Discussions.AddRange(
            new Discussion
            {
                UserId = user.Id,
                CategoryId = autres.Id,
                TopicSlug = autres.Slug,
                Title = "Misc A",
                Body = "Question A?",
            },
            new Discussion
            {
                UserId = user.Id,
                CategoryId = autres.Id,
                TopicSlug = autres.Slug,
                Title = "Misc B",
                Body = "Question B?",
            },
            new Discussion
            {
                UserId = user.Id,
                CategoryId = autres.Id,
                TopicSlug = autres.Slug,
                Title = "Misc C",
                Body = "Question C?",
            },
            new Discussion
            {
                UserId = user.Id,
                CategoryId = sport.Id,
                TopicSlug = sport.Slug,
                Title = "Léopards",
                Body = "Comment accueillez-vous le retour ?",
            });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        var result = await CreateController(db, cache).ListCategories(pageSize: 10);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paged = Assert.IsType<PagedResultDto<CategoryDto>>(ok.Value);
        Assert.Equal("sport", paged.Items[0].Slug);
        Assert.Equal("autres", paged.Items[^1].Slug);
    }

    [Fact]
    public async Task ListCategories_TranslatesOrderByOnRelationalProvider()
    {
        await using var db = await TestDbFactory.CreateSqliteAsync();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var sport = new Category
        {
            Slug = "sport",
            Label = "Sport & foot",
            Icon = "⚽",
            IsAiGenerated = true,
            IsDiscussionTopic = true,
        };
        db.Categories.Add(sport);
        await db.SaveChangesAsync();

        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = sport.Id,
            TopicSlug = sport.Slug,
            Title = "Léopards",
            Body = "Comment accueillez-vous le retour ?",
        });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        var result = await CreateController(db, cache).ListCategories();

        Assert.IsType<OkObjectResult>(result.Result);
    }
}
