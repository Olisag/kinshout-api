using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Tests;

public class ExternalDiscussionImportTests
{
    [Fact]
    public async Task ImportAsync_CreatesAndUpdatesExternalDiscussion()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = new ExternalDiscussionImportService(db);

        var dto = SampleImport("fb-post-1", "Kinshasa traffic update");
        var first = await service.ImportAsync([dto]);
        Assert.Equal(1, first.Created);

        var discussion = await db.Discussions.SingleAsync();
        Assert.Equal(DiscussionSourceProvider.Facebook, discussion.SourceProvider);
        Assert.Equal("fb-post-1", discussion.SourceExternalId);
        Assert.True(discussion.IsExternal);
        Assert.Equal("Kinshasa traffic update", discussion.Title);
        Assert.Equal(120, discussion.ViewCount);

        var updated = await service.ImportAsync([dto with { Title = "Updated Kinshasa topic" }]);
        Assert.Equal(1, updated.Updated);
        Assert.Equal("Updated Kinshasa topic", (await db.Discussions.SingleAsync()).Title);
    }

    [Fact]
    public async Task ImportAsync_DeletesRemovedDiscussionFromDatabase()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = new ExternalDiscussionImportService(db);
        var dto = SampleImport("rm-1", "Temporary topic");

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
        var service = new ExternalDiscussionImportService(db);
        await service.ImportAsync([SampleImport("x-99", "Tweet topic") with
        {
            Source = SampleImport("x-99", "Tweet topic").Source with { Provider = "twitter" },
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
        var service = new ExternalDiscussionImportService(db);
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

    private static ImportExternalDiscussionDto SampleImport(string externalId, string title) =>
        new(
            Source: new ImportExternalDiscussionSourceDto(
                Provider: DiscussionSourceProvider.Facebook,
                ProviderName: "Facebook",
                ExternalId: externalId,
                ExternalUrl: $"https://facebook.com/posts/{externalId}",
                ImportedAt: DateTime.UtcNow,
                LastSeenAt: DateTime.UtcNow,
                FirstSeenAt: DateTime.UtcNow),
            Title: title,
            Body: title,
            OriginalAuthor: "Test Page",
            EngagementScore: 120,
            Status: "active",
            PublishedAt: DateTime.UtcNow.AddDays(-1));
}
