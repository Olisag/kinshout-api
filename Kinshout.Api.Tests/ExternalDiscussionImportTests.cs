using System.Net;
using System.Text;
using System.Text.Json;
using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class ExternalDiscussionImportTests
{
    [Fact]
    public async Task ImportAsync_BlocksDiscussionWhenImportedImageIsPolitical()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);

        const string imageUrl = "https://cdn.example.test/media/political-screenshot.jpg";
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.CheckImageAllowedAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdvertModerationCheckResult(
                false,
                "Contenu politique détecté dans l'image.",
                ["political_content"]));

        var service = CreateService(
            db,
            new StaticImageClientFactory(imageUrl, [0xFF, 0xD8, 0xFF, 0x00]),
            moderation.Object);

        var dto = SampleImport("x-political-img", "Blague entre amis à Kinshasa ce soir.") with
        {
            ImageUrls = [imageUrl],
        };
        var result = await service.ImportAsync([dto]);

        Assert.Equal(1, result.Created);
        var discussion = await db.Discussions.SingleAsync();
        Assert.True(discussion.PublicFeedBlocked);
        Assert.Equal(PoliticsFeedFilter.RuleVersion, discussion.PublicFeedRuleVersion);
        Assert.Equal(ImportedDiscussionImageModeration.RuleVersion, discussion.PublicFeedMediaRuleVersion);
        moderation.Verify(
            m => m.CheckImageAllowedAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

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
        Assert.False(discussion.Body.TrimEnd().EndsWith('?'));
        Assert.Equal(0, discussion.ViewCount);
        Assert.Equal(120, discussion.SourceEngagementScore);
        Assert.Equal("Test Page", discussion.SourceOriginalAuthor);
        Assert.Equal("societe", discussion.TopicSlug);
        Assert.NotNull(discussion.CommunityId);
        Assert.Equal(CommunityDefaults.GeneralSlug, (await db.Communities.SingleAsync(c => c.Id == discussion.CommunityId)).Slug);
        Assert.Contains("· Animateur", (await db.Users.SingleAsync(u => u.Id == discussion.UserId)).DisplayName);
        Assert.Equal(0, discussion.ReplyCount);
        Assert.Empty(await db.DiscussionReplies.Where(r => r.DiscussionId == discussion.Id).ToListAsync());
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
        Assert.False(discussion.Body.TrimEnd().EndsWith('?'));
        Assert.Equal("Sports Page", discussion.SourceOriginalAuthor);
        Assert.Equal(50, discussion.SourceEngagementScore);
    }

    [Fact]
    public async Task ImportAsync_KeepsEachVideoTitleWhenTheDescriptionIsTheSame()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);
        const string body = "Devenez partenaire de Moise Mbiye Ministries. Contactez le secretariat pour les details.";

        await service.ImportAsync([
            YouTubeImport("yt-soir", "6jAedCwFNu0", "CULTE DES MIRACLES & DELIVRANCE/MERCREDI 04 NOV 2026/SESSION DU SOIR.", body),
            YouTubeImport("yt-matin", "AH5w4QlDeY4", "CULTE DE DELIVRANCE ET MIRACLE/MERCREDI 04.11.2026/VACATION MATIN.", body),
        ]);

        var titles = await db.Discussions.Select(d => d.Title).ToListAsync();
        Assert.Equal(2, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(titles, title => title.Contains("miracles", StringComparison.OrdinalIgnoreCase) && title.Contains("soir", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, title => title.Contains("04.11.2026", StringComparison.Ordinal) && title.Contains("matin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, title => title.Contains("partenariat", StringComparison.OrdinalIgnoreCase));
        Assert.All(await db.Discussions.ToListAsync(), discussion => Assert.Equal(body, discussion.SourceRawBody));
    }

    [Fact]
    public async Task RepairDuplicateTitlesAsync_RewritesCopiedHeadlinesFromYouTube()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        const string copied = "Partenariat avec Moïse Mbiye Ministries";
        const string body = "Devenez partenaire de Moise Mbiye Ministries. Contactez le secretariat.";
        db.Discussions.AddRange(
            StoredYouTube(user.Id, category.Id, "6jAedCwFNu0", copied, body),
            StoredYouTube(user.Id, category.Id, "AH5w4QlDeY4", copied, body));
        await db.SaveChangesAsync();

        var service = CreateService(db, new YouTubeTitleClientFactory(new YouTubeTitleHandler()));
        var result = await service.RepairDuplicateTitlesAsync();

        Assert.Equal(2, result.Rewritten);
        var titles = await db.Discussions.Select(d => d.Title).ToListAsync();
        Assert.Equal(2, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(titles, title => title.Contains("partenariat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportAsync_SkipsARepeatedCaptionAndLeavesTheEarlierPost()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);
        const string caption = "Une maman que certains Congolais croyaient deja decedee a ete retrouvee en train de se balader dans les rues de Kinshasa ce matin";

        var first = await service.ImportAsync([SampleImport("tt-first", caption)]);
        var second = await service.ImportAsync([SampleImport("tt-second", caption) with { EngagementScore = 80 }]);

        Assert.Equal(1, first.Created);
        Assert.Equal(1, second.Skipped);
        var saved = await db.Discussions.SingleAsync();
        Assert.Equal("tt-first", saved.SourceExternalId);
    }

    [Fact]
    public async Task ImportAsync_KeepsAnotherTikTokUntilTheStoryWeShowHasAFile()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);
        const string caption = "Une maman que certains Congolais croyaient deja decedee a ete retrouvee en train de se balader dans les rues de Kinshasa ce matin";

        var first = await service.ImportAsync([TikTokImport("7710000000000000001", caption)]);
        var second = await service.ImportAsync([
            TikTokImport("7710000000000000002", caption, "/uploads/videos/import/clip.mp4"),
        ]);

        Assert.Equal(1, first.Created);
        Assert.Equal(1, second.Created);
        Assert.Equal(2, await db.Discussions.CountAsync());
    }

    [Fact]
    public async Task ImportAsync_DropsAnotherTikTokOnceTheStoryWeShowHasAFile()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);
        const string caption = "Une maman que certains Congolais croyaient deja decedee a ete retrouvee en train de se balader dans les rues de Kinshasa ce matin";

        var first = await service.ImportAsync([
            TikTokImport("7710000000000000011", caption, "/uploads/videos/import/clip.mp4"),
        ]);
        var second = await service.ImportAsync([TikTokImport("7710000000000000012", caption)]);

        Assert.Equal(1, first.Created);
        Assert.Equal(1, second.Skipped);
        var saved = await db.Discussions.SingleAsync();
        Assert.Equal("7710000000000000011", saved.SourceExternalId);
        Assert.Contains("/uploads/videos/", saved.VideoUrlsJson);
    }

    [Fact]
    public async Task ImportAsync_KeepsThePostPeopleAreReactingToWhenTheWordingDiffers()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var transform = new Mock<IExternalDiscussionTransformService>();
        transform.Setup(t => t.TransformAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync((string raw, string? _, string? _, CancellationToken _, IReadOnlyList<string>? recent) =>
                new ExternalDiscussionTransformResult("Sujet Kinshasa", raw, false, recent is { Count: > 0 }));
        var service = CreateService(db, new TestHttpClientFactory(), transform: transform.Object);

        const string walking = "Une maman que certains Congolais croyaient deja decedee a ete retrouvee en train de se balader dans les rues de Kinshasa.";
        const string filmed = "Une video troublante au Congo montre une dame revenue a la vie trois ans apres son deces presume. La foule tente de la filmer pendant qu'elle continue son chemin.";
        var quiet = SampleImport("tt-quiet", filmed) with
        {
            Title = "Video troublante au Congo",
            EngagementScore = 40,
        };
        var loud = SampleImport("tt-loud", walking) with
        {
            Title = "Une maman retrouvee dans les rues de Kinshasa",
            EngagementScore = 400,
        };

        var result = await service.ImportAsync([quiet, loud]);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Skipped);
        var saved = await db.Discussions.SingleAsync();
        Assert.Equal("tt-loud", saved.SourceExternalId);
    }

    [Fact]
    public async Task ImportAsync_PublishesWhenTheRewriteCannotTellTheStoriesApart()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var transform = new Mock<IExternalDiscussionTransformService>();
        transform.Setup(t => t.TransformAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync((string raw, string? _, string? _, CancellationToken _, IReadOnlyList<string>? _) =>
                new ExternalDiscussionTransformResult("Sujet Kinshasa", raw, true, false));
        var service = CreateService(db, new TestHttpClientFactory(), transform: transform.Object);

        var result = await service.ImportAsync([
            SampleImport("tt-walk", "Une maman retrouvee en train de marcher a Kinshasa apres une rumeur de deces.") with
            {
                Title = "Maman retrouvee a Kinshasa",
            },
            SampleImport("tt-speaks", "La dame filmee a Kinshasa prend la parole et dement etre la personne que l'on disait morte.") with
            {
                Title = "Elle dement la rumeur",
                EngagementScore = 30,
            },
        ]);

        Assert.Equal(2, result.Created);
        Assert.Equal(2, await db.Discussions.CountAsync());
    }

    private static ExternalDiscussionImportService CreateService(KinshoutDbContext db) =>
        CreateService(db, new TestHttpClientFactory());

    private static ExternalDiscussionImportService CreateService(
        KinshoutDbContext db,
        IHttpClientFactory httpClientFactory,
        IAdvertModerationService? moderation = null,
        IExternalDiscussionTransformService? transform = null)
    {
        var openAi = new Mock<IOpenAiService>();
        openAi.Setup(o => o.AnalyzeDiscussionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Category>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestDbFactory.SampleDiscussionAnalysis());

        var communities = new Mock<ICommunityService>();
        communities.Setup(c => c.SuggestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuggestCommunityResponseDto(null, 0, "none", "none"));

        moderation ??= PassThroughImageModeration().Object;
        var creator = db.Users.FirstOrDefault();
        if (creator is not null && !db.Communities.Any(c => c.Slug == CommunityDefaults.GeneralSlug))
        {
            db.Communities.Add(new Community
            {
                Slug = CommunityDefaults.GeneralSlug,
                Name = "Général",
                Description = "Discussions générales",
                Visibility = CommunityVisibilities.Public,
                IsActive = true,
                CreatedByUserId = creator.Id,
            });
            db.SaveChanges();
        }

        return new ExternalDiscussionImportService(
            db,
            transform ?? new ExternalDiscussionTransformService(
                httpClientFactory,
                Options.Create(new OpenAiSettings { ApiKey = "", Model = "gpt-4o-mini" }),
                NullLogger<ExternalDiscussionTransformService>.Instance),
            openAi.Object,
            new MemoryCache(new MemoryCacheOptions()),
            communities.Object,
            httpClientFactory,
            moderation,
            NullLogger<ExternalDiscussionImportService>.Instance);
    }

    private static Mock<IAdvertModerationService> PassThroughImageModeration()
    {
        var mock = new Mock<IAdvertModerationService>();
        mock.Setup(m => m.CheckImageAllowedAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdvertModerationCheckResult(true, null, []));
        return mock;
    }

    [Fact]
    public async Task ImportAsync_KeepsHostedTikTokFileWhenTheWatchPageComesBack()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);
        var watch = "https://www.tiktok.com/@worldactuallite0/video/7710601313859521813";
        var dto = SampleImport("7710601313859521813", "Une maman que certains Congolais croyaient déjà décédée.") with
        {
            VideoUrls = [watch],
            Source = SampleImport("7710601313859521813", "caption").Source with
            {
                Provider = DiscussionSourceProvider.TikTok,
                ProviderName = "TikTok",
                ExternalUrl = watch,
            },
        };

        Assert.Equal(1, (await service.ImportAsync([dto])).Created);
        var discussion = await db.Discussions.SingleAsync();
        var arrived = discussion.SourceImportedAt;
        discussion.VideoUrlsJson = "[\"/uploads/videos/import/clip.mp4\"]";
        await db.SaveChangesAsync();

        var second = await service.ImportAsync([dto with
        {
            Body = dto.Body + " Elle marchait à Kinshasa.",
            VideoUrls = [watch],
        }]);
        Assert.Equal(1, second.Updated + second.Unchanged);
        await db.Entry(discussion).ReloadAsync();
        Assert.Contains("/uploads/videos/", discussion.VideoUrlsJson);
        Assert.Equal(arrived, discussion.SourceImportedAt);
    }

    [Fact]
    public async Task ImportAsync_StopsAPersonalityAtTwoPostsInADay()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);

        var first = await service.ImportAsync([SampleImport("cap-1", "Kinshasa traffic update on boulevard du 30 juin today.")]);
        var second = await service.ImportAsync([SampleImport("cap-2", "Another Kinshasa evening on the same boulevard with the same page.")]);
        var third = await service.ImportAsync([SampleImport("cap-3", "A third Kinshasa note from the same page the same day.")]);

        Assert.Equal(1, first.Created);
        Assert.Equal(1, second.Created);
        Assert.Equal(1, third.Skipped);
        Assert.Equal(2, await db.Discussions.CountAsync());
    }

    [Fact]
    public async Task ImportAsync_StopsFacebookAtTheWeeklyAllowance()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);

        for (var index = 0; index < DiscoveryImportGuard.FacebookWeeklyImports; index++)
        {
            var created = await service.ImportAsync([
                SampleImport($"week-{index}", $"Kintambo{index} vendeur{index} papaye{index} ngaliema{index} fleuve{index}.") with
                {
                    OriginalAuthor = $"Page {index}",
                },
            ]);
            Assert.Equal(1, created.Created);
        }

        var over = await service.ImportAsync([
            SampleImport("week-over", "Limete soir tambour unique apres le plafond hebdomadaire.") with
            {
                OriginalAuthor = "Page overflow",
            },
        ]);

        Assert.Equal(1, over.Skipped);
        Assert.Equal(DiscoveryImportGuard.FacebookWeeklyImports, await db.Discussions.CountAsync());
    }

    private static ImportExternalDiscussionDto YouTubeImport(string externalId, string videoId, string title, string rawBody)
    {
        var watchUrl = $"https://www.youtube.com/watch?v={videoId}";
        return SampleImport(externalId, rawBody) with
        {
            Title = title,
            VideoUrls = [watchUrl],
            Source = SampleImport(externalId, rawBody).Source with
            {
                Provider = DiscussionSourceProvider.YouTube,
                ProviderName = "YouTube",
                ExternalUrl = watchUrl,
            },
        };
    }

    private static Discussion StoredYouTube(Guid userId, Guid categoryId, string videoId, string title, string body) =>
        new()
        {
            UserId = userId,
            CategoryId = categoryId,
            Title = title,
            Body = body,
            SourceRawBody = body,
            SourceProvider = DiscussionSourceProvider.YouTube,
            SourceProviderName = "YouTube",
            SourceExternalId = videoId,
            SourceExternalUrl = $"https://www.youtube.com/watch?v={videoId}",
            SourceOriginalAuthor = "Moise Mbiye Officiel",
        };

    private static ImportExternalDiscussionDto TikTokImport(string externalId, string rawBody, string? videoUrl = null)
    {
        var watch = $"https://www.tiktok.com/@a/video/{externalId}";
        return SampleImport(externalId, rawBody) with
        {
            VideoUrls = [videoUrl ?? watch],
            Source = SampleImport(externalId, rawBody).Source with
            {
                Provider = DiscussionSourceProvider.TikTok,
                ProviderName = "TikTok",
                ExternalUrl = watch,
            },
        };
    }

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
            Title: rawBody,
            Body: rawBody,
            OriginalAuthor: "Test Page",
            EngagementScore: 120,
            Status: "active",
            PublishedAt: DateTime.UtcNow.AddDays(-1));

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class YouTubeTitleHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            var title = url.Contains("6jAedCwFNu0", StringComparison.Ordinal)
                ? "CULTE DES MIRACLES & DELIVRANCE/MERCREDI 04 NOV 2026/SESSION DU SOIR."
                : "CULTE DE DELIVRANCE ET MIRACLE/MERCREDI 04.11.2026/VACATION MATIN.";
            var json = JsonSerializer.Serialize(new { title });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class YouTubeTitleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
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
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") },
                },
            });
        }
    }
}
