using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Kinshout.Api.Tests;

public class ImportedDiscussionImageModerationBackfillTests
{
    [Fact]
    public async Task ScheduleBackfill_HidesExistingPoliticalImagePosts()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        const string imageUrl = "https://cdn.example.test/media/kayikwamba-oif.jpg";
        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Vous allez la quitter de gré ou de force",
            Body = "Blague entre amis.",
            SourceRawBody = "#RDC: Vous allez la quitter de gré ou de force",
            ImageUrlsJson = DiscussionMediaHelper.SerializeUrlList([imageUrl]),
            SourceProvider = DiscussionSourceProvider.Twitter,
            SourceExternalId = "legacy-political-img",
            SourceExternalUrl = "https://x.com/example/status/1",
            PublicFeedBlocked = false,
            PublicFeedMediaRuleVersion = 0,
        });
        await db.SaveChangesAsync();

        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.CheckImageAllowedAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdvertModerationCheckResult(
                false,
                "Contenu politique détecté dans l'image.",
                ["political_content"]));

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IAdvertModerationService>(moderation.Object);
        services.AddSingleton<IHttpClientFactory>(
            new StaticImageClientFactory(imageUrl, [0xFF, 0xD8, 0xFF, 0x00]));
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var scheduler = new ImportedDiscussionImageModerationBackfillScheduler(
            scopeFactory,
            NullLogger<ImportedDiscussionImageModerationBackfillScheduler>.Instance);
        scheduler.ScheduleBackfill();

        // Wait for the fire-and-forget backfill to finish.
        for (var i = 0; i < 50; i++)
        {
            await db.Entry(await db.Discussions.SingleAsync()).ReloadAsync();
            var row = await db.Discussions.SingleAsync();
            if (row.PublicFeedMediaRuleVersion >= ImportedDiscussionImageModeration.RuleVersion)
                break;
            await Task.Delay(50);
        }

        var discussion = await db.Discussions.SingleAsync();
        Assert.True(discussion.PublicFeedBlocked);
        Assert.Equal(ImportedDiscussionImageModeration.RuleVersion, discussion.PublicFeedMediaRuleVersion);
        moderation.Verify(
            m => m.CheckImageAllowedAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private sealed class StaticImageClientFactory(string imageUrl, byte[] bytes) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StaticImageHandler(imageUrl, bytes), disposeHandler: true);
    }

    private sealed class StaticImageHandler(string imageUrl, byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(request.RequestUri?.ToString(), imageUrl, StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
                {
                    Headers =
                    {
                        ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg"),
                    },
                },
            });
        }
    }
}
