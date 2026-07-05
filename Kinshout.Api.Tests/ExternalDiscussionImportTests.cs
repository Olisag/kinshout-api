using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Tests;

public class ExternalDiscussionImportTests
{
    [Fact]
    public async Task ImportAsync_CreatesTransformedDiscussionAndStoresRawBody()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);

        const string raw = "Kinshasa traffic update on boulevard du 30 juin today.";
        var dto = SampleImport("fb-post-1", raw);
        var first = await service.ImportAsync([dto]);
        Assert.Equal(1, first.Created);

        var discussion = await db.Discussions.SingleAsync();
        Assert.Equal(DiscussionSourceProvider.Facebook, discussion.SourceProvider);
        Assert.Equal("fb-post-1", discussion.SourceExternalId);
        Assert.True(discussion.IsExternal);
        Assert.Equal(raw, discussion.SourceRawBody);
        Assert.DoesNotContain("?", discussion.Title);
        Assert.True(discussion.Body.TrimEnd().EndsWith('?'));
        Assert.Equal(0, discussion.ViewCount);
        Assert.Equal(120, discussion.SourceEngagementScore);
        Assert.Equal("Test Page", discussion.SourceOriginalAuthor);
    }

    [Fact]
    public async Task ImportAsync_UpdatesWhenRawBodyChanges()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);

        var dto = SampleImport("fb-post-2", "First raw Kinshasa topic about power cuts.");
        await service.ImportAsync([dto]);

        var updated = await service.ImportAsync([dto with { Body = "Updated raw Kinshasa topic about water shortages in Gombe." }]);
        Assert.Equal(1, updated.Updated);

        var discussion = await db.Discussions.SingleAsync();
        Assert.Contains("water shortages", discussion.SourceRawBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportAsync_DeletesRemovedDiscussionFromDatabase()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);
        var dto = SampleImport("rm-1", "Temporary topic about Kinshasa weather this week.");

        await service.ImportAsync([dto]);
        var removed = await service.ImportAsync([dto with { Status = "removed" }]);

        Assert.Equal(1, removed.Updated);
        Assert.Empty(await db.Discussions.ToListAsync());
    }

    [Fact]
    public async Task GetKnownDiscussionKeysAsync_ReturnsImportedKeys()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);
        await service.ImportAsync([SampleImport("x-99", "Tweet topic about Kinshasa nightlife.") with
        {
            Source = SampleImport("x-99", "Tweet topic about Kinshasa nightlife.").Source with { Provider = "twitter" },
        }]);

        var keys = await service.GetKnownDiscussionKeysAsync();

        Assert.Single(keys);
        Assert.Equal("twitter", keys[0].Provider);
        Assert.Equal("x-99", keys[0].ExternalId);
    }

    [Fact]
    public async Task RecordDiscussionImportRunAsync_StoresAndReturnsProviderWatermark()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);
        var runAt = new DateTime(2026, 7, 1, 3, 0, 0, DateTimeKind.Utc);

        await service.RecordDiscussionImportRunAsync(DiscussionSourceProvider.Facebook, runAt);

        var state = await service.GetDiscussionImportStateAsync();
        Assert.Single(state);
        Assert.Equal(DiscussionSourceProvider.Facebook, state[0].Provider);
        Assert.Equal(runAt, state[0].LastRunAtUtc);

        var later = runAt.AddDays(7);
        await service.RecordDiscussionImportRunAsync(DiscussionSourceProvider.Facebook, later);
        state = await service.GetDiscussionImportStateAsync();
        Assert.Equal(later, state.Single().LastRunAtUtc);
    }

    [Fact]
    public async Task RetransformAllAsync_FormatsExistingRawDiscussions()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "🚨 FLASH 🔥 Trois Léopards à Kinshasa",
            Body = "🚨 FLASH 🔥 Trois Léopards sont aperçus à Kinshasa après l'élimination. #LeopardsRDC",
            SourceProvider = DiscussionSourceProvider.Facebook,
            SourceExternalId = "legacy-1",
            SourceExternalUrl = "https://facebook.com/posts/legacy-1",
            SourceEngagementScore = 50,
            SourceOriginalAuthor = "Sports Page",
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.RetransformAllAsync(force: true);

        Assert.Equal(1, result.Transformed);
        var discussion = await db.Discussions.SingleAsync();
        Assert.NotNull(discussion.SourceRawBody);
        Assert.DoesNotContain("#", discussion.Title);
        Assert.True(discussion.Body.TrimEnd().EndsWith('?'));
        Assert.Equal("Sports Page", discussion.SourceOriginalAuthor);
        Assert.Equal(50, discussion.SourceEngagementScore);
    }

    private static ExternalDiscussionImportService CreateService(KinshoutDbContext db) =>
        new(
            db,
            new ExternalDiscussionTransformService(
                new TestHttpClientFactory(),
                Options.Create(new OpenAiSettings { ApiKey = "", Model = "gpt-4o-mini" }),
                NullLogger<ExternalDiscussionTransformService>.Instance));

    private static ImportExternalDiscussionDto SampleImport(string externalId, string rawBody) =>
        new(
            Source: new ImportExternalDiscussionSourceDto(
                Provider: DiscussionSourceProvider.Facebook,
                ProviderName: "Facebook",
                ExternalId: externalId,
                ExternalUrl: $"https://facebook.com/posts/{externalId}",
                ImportedAt: DateTime.UtcNow,
                LastSeenAt: DateTime.UtcNow,
                FirstSeenAt: DateTime.UtcNow),
            Title: "ignored scraper title",
            Body: rawBody,
            OriginalAuthor: "Test Page",
            EngagementScore: 120,
            Status: "active",
            PublishedAt: DateTime.UtcNow.AddDays(-1));

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
