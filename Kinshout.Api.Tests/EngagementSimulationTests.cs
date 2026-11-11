using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public sealed class EngagementSimulationTests
{
    [Fact]
    public void Pacing_IsMonotonic_Bounded_AndHasNoExpiration()
    {
        foreach (var start in new[] { new DateTime(2026, 11, 11), new DateTime(2027, 12, 31) })
        {
            var prior = 0;
            for (var minute = 0; minute < 1440; minute++)
            {
                var current = DiscussionSeedingPipeline.DueInteractions(start.AddMinutes(minute), 1200);
                Assert.InRange(current, prior, 1200);
                prior = current;
            }
            Assert.InRange(prior, 1198, 1200);
        }
    }

    [Fact]
    public void Ranking_PrefersFreshSourceAndGenuineEngagement()
    {
        var now = DateTime.UtcNow;
        var fresh = new Discussion { CreatedAt = now, SourceLikeCount = 100, LikeCount = 4 };
        var stale = new Discussion { CreatedAt = now.AddDays(-30), SourceLikeCount = 100 };
        Assert.True(DiscussionSeedingPipeline.TrendScore(fresh, 2, now) > DiscussionSeedingPipeline.TrendScore(stale, 0, now));
    }

    [Fact]
    public async Task ProfilesAndLikes_ArePersistent_ConcurrentAndIdempotent()
    {
        await using var f = await Fixture.Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Pipeline().PublishSimulationLikesAsync()));
        Assert.Equal(4, results.Sum());
        Assert.Equal(1500, await f.Db.Users.CountAsync(u => u.SeedRunId != null));
        Assert.Equal(4, await f.Db.DiscussionSimulatedLikes.CountAsync());
        Assert.Equal(0, await f.Pipeline().PublishSimulationLikesAsync());
        var ids = await f.Db.Users.Where(u => u.SeedRunId != null).Select(u => u.Id).ToListAsync();
        await f.Pipeline().InitializeAsync();
        f.Clock.Utc = f.Clock.Utc.AddMinutes(2);
        await f.Pipeline().PublishSimulationLikesAsync();
        Assert.Equal(1500, await f.Db.Users.CountAsync(u => u.SeedRunId != null));
        Assert.Equal(ids.Order(), (await f.Db.Users.Where(u => u.SeedRunId != null).Select(u => u.Id).ToListAsync()).Order());
        Assert.Equal(7, (await f.Db.Discussions.AsNoTracking().SingleAsync()).LikeCount);
        Assert.All(await f.Db.Users.Where(u => u.SeedRunId != null).ToListAsync(), u => { Assert.Null(u.PasswordHash); Assert.DoesNotContain("Simulation", u.DisplayName); });
    }

    [Fact]
    public async Task BudgetReservation_BoundsConcurrentInference_AndRetriesLater()
    {
        await using var f = await Fixture.Create();
        f.Config.DailyCostLimitUsd = 0.02m;
        f.Generator.Setup(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Pipeline().GenerateAsync()));
        f.Generator.Verify(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>()), Times.Once);
        f.Clock.Utc = f.Clock.Utc.AddMinutes(2);
        await f.Pipeline().GenerateAsync();
        f.Clock.Utc = f.Clock.Utc.AddMinutes(2);
        await f.Pipeline().GenerateAsync();
        f.Generator.Verify(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        var day = await f.Db.DiscussionSimulationDays.AsNoTracking().SingleAsync();
        Assert.Equal(0.02m, day.ReservedCostUsd);
        Assert.Equal(2, day.GenerationAttempts);
        Assert.Empty(await f.Db.DiscussionReplies.Where(r => r.SeedRunId != null).ToListAsync());
    }

    [Fact]
    public async Task PauseStopAndRollback_AreIndependent_AndPreserveGenuineRecords()
    {
        await using var f = await Fixture.Create();
        await f.Pipeline().GenerateAsync();
        f.Clock.Utc = f.Clock.Utc.AddMinutes(20);
        await f.Pipeline().PublishDueAsync();
        await f.Pipeline().PublishSimulationLikesAsync();
        var discussionId = await f.Db.Discussions.Select(d => d.Id).SingleAsync();
        var discussions = new DiscussionService(f.Db, Mock.Of<IOpenAiService>(), f.Moderation.Object,
            Mock.Of<IUploadStorage>(), TestDbFactory.CreatePermissiveCommunityService(),
            TestDbFactory.CreatePermissiveDiscussionParticipationService(),
            TestDbFactory.CreatePermissiveVideoService(), TestDbFactory.CreateMemoryCache());
        var before = await discussions.GetByIdAsync(discussionId);
        Assert.Equal(11, before!.LikeCount);
        Assert.Equal(2, before.ReplyCount);
        await f.Control.SetFlagsAsync(Guid.Empty, new());
        await f.Pipeline().InitializeAsync();
        Assert.False((await f.Control.GetFlagsAsync()).GenerationEnabled);
        Assert.Equal(0, await f.Pipeline().PublishSimulationLikesAsync());
        Assert.Equal(4, await f.Db.DiscussionSimulatedLikes.CountAsync(l => l.HiddenAt == null));
        await f.Control.StopAsync(Guid.Empty);
        await f.Pipeline().InitializeAsync();
        Assert.Equal(0, await f.Db.DiscussionSeedRuns.CountAsync(r => r.StoppedAt == null));
        Assert.Single(await f.Db.DiscussionReplies.Where(r => r.SeedRunId != null).ToListAsync());
        await Task.WhenAll(f.Control.StopAndHideAsync(Guid.Empty), f.Pipeline().PublishSimulationLikesAsync());
        await f.Control.StopAndHideAsync(Guid.Empty);
        Assert.Empty(await f.Db.DiscussionSimulatedLikes.Where(l => l.HiddenAt == null).ToListAsync());
        Assert.Equal(4, await f.Db.DiscussionSimulatedLikes.CountAsync());
        Assert.Single(await f.Db.DiscussionReplies.ToListAsync());
        Assert.Equal(2, await f.Db.DiscussionReplies.IgnoreQueryFilters().CountAsync());
        var post = await f.Db.Discussions.AsNoTracking().SingleAsync();
        Assert.Equal(7, post.LikeCount);
        Assert.Equal(1, post.ReplyCount);
        var after = await discussions.GetByIdAsync(discussionId);
        Assert.Equal(7, after!.LikeCount);
        Assert.Equal(1, after.ReplyCount);
        Assert.Single(await f.Db.LikedDiscussions.ToListAsync());
        Assert.NotNull(await f.Db.Users.SingleOrDefaultAsync(u => u.SeedRunId == null && u.Email == "real@example.com"));
    }

    [Fact]
    public async Task PauseDuringInference_BlocksCommit_AndModerationStillRuns()
    {
        await using var f = await Fixture.Create();
        f.Generator.Setup(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await f.Control.SetFlagsAsync(Guid.Empty, new()); return "Ce refrain donne envie de découvrir le morceau complet."; });
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
        Assert.Empty(await f.Db.DiscussionSeedJobs.ToListAsync());
        f.Moderation.Verify(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SchemaRepair_CreatesSimulationTables_Idempotently()
    {
        await using var f = await Fixture.Create();
        await f.Db.Database.ExecuteSqlRawAsync("DROP TABLE DiscussionSimulatedLikes");
        await f.Db.Database.ExecuteSqlRawAsync("DROP TABLE DiscussionSimulationDays");
        await DbSchemaPatcher.ApplyAsync(f.Db);
        await DbSchemaPatcher.ApplyAsync(f.Db);
        Assert.Empty(await f.Db.DiscussionSimulationDays.ToListAsync());
        Assert.Empty(await f.Db.DiscussionSimulatedLikes.ToListAsync());
    }

    [Fact]
    public async Task ActivePublisherRacingRollback_CannotLeaveVisibleSimulation()
    {
        await using var f = await Fixture.Create();
        await Task.WhenAll(f.Pipeline().PublishSimulationLikesAsync(), f.Control.StopAndHideAsync(Guid.Empty));
        Assert.Equal(0, await f.Db.DiscussionSimulatedLikes.CountAsync(l => l.HiddenAt == null));
        Assert.Equal(0, await f.Pipeline().PublishSimulationLikesAsync());
        Assert.Single(await f.Db.LikedDiscussions.ToListAsync());
        Assert.Equal(7, (await f.Db.Discussions.AsNoTracking().SingleAsync()).LikeCount);
    }

    [Fact]
    public async Task DisabledModeration_FailsClosed_ForCommentsAndLikes()
    {
        await using var f = await Fixture.Create();
        var pipeline = new DiscussionSeedingPipeline(f.Store.Options, Options.Create(f.Config), f.Generator.Object,
            f.Moderation.Object, NullLogger<DiscussionSeedingPipeline>.Instance, f.Clock,
            Options.Create(new OpenAiSettings { ApiKey = "test", ModerationEnabled = false }));
        Assert.Equal(0, await pipeline.GenerateAsync());
        Assert.Equal(0, await pipeline.PublishDueAsync());
        Assert.Equal(0, await pipeline.PublishSimulationLikesAsync());
        f.Generator.Verify(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DayForty_ContinuesWithoutExpiration_OrAutomaticRollback()
    {
        await using var f = await Fixture.Create();
        await f.Pipeline().PublishSimulationLikesAsync();
        f.Clock.Utc = f.Clock.Utc.AddDays(40);
        Assert.Equal(4, await f.Pipeline().PublishSimulationLikesAsync());
        Assert.Equal(8, await f.Db.DiscussionSimulatedLikes.CountAsync(l => l.HiddenAt == null));
        Assert.Equal(1, await f.Db.DiscussionSeedRuns.CountAsync(r => r.StoppedAt == null));
        Assert.Equal(1500, await f.Db.Users.CountAsync(u => u.SeedRunId != null && u.SeedHiddenAt == null));
    }

    [Fact]
    public async Task CommunityContent_RespectsCapsAndArchivesOnlyUntouchedThreads()
    {
        await using var f = await Fixture.Create();
        f.Config.ProfileCount = 12;
        f.Config.DailyDiscussionPosts = 10;
        f.Config.WeeklyPublicCommunities = 2;
        await f.Pipeline().PublishSimulationLikesAsync();
        Assert.Equal(1, await f.Pipeline().PublishSimulationCommunityContentAsync());
        Assert.Equal(1, await f.Pipeline().PublishSimulationCommunityContentAsync());
        Assert.Equal(2, await f.Db.Communities.CountAsync(c => c.CreatedByUser.SeedRunId != null));
        Assert.Equal(1, await f.Pipeline().PublishSimulationCommunityContentAsync());
        Assert.Equal(1, await f.Pipeline().PublishSimulationCommunityContentAsync());
        var posts = await f.Db.Discussions.Where(d => d.User.SeedRunId != null).OrderBy(d => d.Title).ToListAsync();
        Assert.Equal(2, posts.Count);
        Assert.All(posts, p =>
        {
            Assert.DoesNotContain("Simulation", p.Title);
            Assert.DoesNotContain("Demo", p.Title);
            Assert.True(char.IsUpper(p.Title[0]));
        });
        var human = await f.Db.Users.SingleAsync(u => u.Email == "real@example.com");
        var originalTitles = posts.Select(p => p.Title).ToArray();
        posts[0].Title = "Simulation · " + originalTitles[0];
        posts[1].Title = "Démo · " + originalTitles[1];
        var genuinePost = await f.Db.Discussions.SingleAsync(d => d.UserId == human.Id);
        genuinePost.Title = "Simulation · Un sujet publié par un membre";
        await f.Db.SaveChangesAsync();
        await f.Pipeline().InitializeAsync();
        f.Db.ChangeTracker.Clear();
        for (var i = 0; i < posts.Count; i++)
            Assert.Equal(originalTitles[i], (await f.Db.Discussions.FindAsync(posts[i].Id))!.Title);
        Assert.Equal("Simulation · Un sujet publié par un membre", (await f.Db.Discussions.FindAsync(genuinePost.Id))!.Title);
        Assert.Equal(0, await DiscussionSeedingPipeline.LegacySeededDiscussionTitles(f.Db).CountAsync());

        f.Db.DiscussionReplies.Add(new() { DiscussionId = posts[0].Id, UserId = human.Id, Body = "Une contribution réelle." });
        await f.Db.SaveChangesAsync();

        var preview = await f.Control.PreviewRollbackAsync();
        Assert.Equal(1, preview.ArchivedDiscussions);
        Assert.Equal(1, preview.ArchivedCommunities);
        var rolledBack = await f.Control.StopAndHideAsync(Guid.Empty);
        Assert.Equal(preview.ArchivedDiscussions, rolledBack.ArchivedDiscussions);
        Assert.Equal(preview.ArchivedCommunities, rolledBack.ArchivedCommunities);
        Assert.True(await f.Db.Discussions.AnyAsync(d => d.Id == posts[0].Id));
        Assert.False(await f.Db.Discussions.AnyAsync(d => d.Id == posts[1].Id));
        Assert.True(await f.Db.DiscussionReplies.AnyAsync(r => r.DiscussionId == posts[0].Id && r.UserId == human.Id));
        Assert.Equal(0, await f.Pipeline().PublishSimulationCommunityContentAsync());
    }

    [Fact]
    public async Task ProfilePool_SupportsFiveThousandAndScaledLikeBatches()
    {
        await using var f = await Fixture.Create();
        f.Config.ProfileCount = 5000;
        f.Config.DailyLikes = 4000;
        f.Config.MaxLikesPerTick = 6;
        Assert.Equal(6, await f.Pipeline().PublishSimulationLikesAsync());
        Assert.Equal(5000, await f.Db.Users.CountAsync(u => u.SeedRunId != null));
        f.Clock.Utc = f.Clock.Utc.AddMinutes(2);
        Assert.Equal(6, await f.Pipeline().PublishSimulationLikesAsync());
        Assert.Equal(5000, await f.Db.Users.CountAsync(u => u.SeedRunId != null));
        Assert.Equal(12, await f.Db.DiscussionSimulatedLikes.Select(l => l.PersonaUserId).Distinct().CountAsync());
    }

    [Fact]
    public async Task StartupRenamesExistingProfilesAndCommunitiesWithoutChangingTheirLinks()
    {
        await using var f = await Fixture.Create();
        f.Config.ProfileCount = 2;
        f.Config.WeeklyPublicCommunities = 2;
        f.Config.DailyDiscussionPosts = 10;
        await f.Pipeline().PublishSimulationLikesAsync();
        var persona = await f.Db.Users.FirstAsync(u => u.SeedRunId != null);
        var genuine = await f.Db.Users.SingleAsync(u => u.Email == "real@example.com");
        genuine.DisplayName = "Simulation_Real";
        persona.DisplayName = "Simulation_MasoloYaKin_abcdef_0042";
        var slug = $"simulation-{persona.SeedRunId:N}-vie-a-kin";
        var community = new Community
        {
            Slug = slug,
            Name = "Simulation · La vie à Kinshasa",
            Description = "Les profils Simulation",
            CreatedByUserId = persona.Id,
            CreatedAt = f.Clock.Utc
        };
        f.Db.Communities.Add(community);
        await f.Db.SaveChangesAsync();

        await f.Pipeline().InitializeAsync();
        await f.Pipeline().InitializeAsync();
        f.Db.ChangeTracker.Clear();

        Assert.Equal("MasoloYaKin_0043", (await f.Db.Users.FindAsync(persona.Id))!.DisplayName);
        Assert.Equal("Simulation_Real", (await f.Db.Users.FindAsync(genuine.Id))!.DisplayName);
        var renamed = await CommunitySlugHelper.BySlug(f.Db.Communities, slug).SingleAsync();
        Assert.StartsWith("vie-a-kin-", renamed.Slug);
        Assert.Equal(community.Id, renamed.Id);
        Assert.Equal("La vie à Kinshasa", renamed.Name);
        Assert.Contains("automatisées", renamed.Description);

        Assert.Equal(1, await f.Pipeline().PublishSimulationCommunityContentAsync());
        var newCommunity = await f.Db.Communities.SingleAsync(c => c.CreatedByUser.SeedRunId != null && c.Id != community.Id);
        Assert.Equal("Congolais de la diaspora", newCommunity.Name);
        Assert.StartsWith("diaspora-", newCommunity.Slug);
        Assert.Equal(1, await f.Pipeline().PublishSimulationCommunityContentAsync());
        var rolledBack = await f.Control.StopAndHideAsync(Guid.Empty);
        Assert.Equal(2, rolledBack.ArchivedCommunities);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTime Utc = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(Utc);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SeedingTestDatabase Store;
        public required KinshoutDbContext Db;
        public readonly Clock Clock = new();
        public readonly DiscussionSeedingSettings Config = new() { Enabled = true, SimulationEnabled = true };
        public readonly Mock<IDiscussionSeedCommentGenerator> Generator = new();
        public readonly Mock<IAdvertModerationService> Moderation = new();
        public DiscussionSeedingService Control => new(Store.Options);
        public DiscussionSeedingPipeline Pipeline() => new(Store.Options, Options.Create(Config), Generator.Object, Moderation.Object, NullLogger<DiscussionSeedingPipeline>.Instance, Clock);
        public static async Task<Fixture> Create()
        {
            var store = await SeedingTestDatabase.CreateAsync();
            var f = new Fixture { Store = store, Db = new(store.Options) };
            var user = new User { Email = "real@example.com", DisplayName = "Real" };
            var post = new Discussion { User = user, Title = "Une répétition de rumba", Body = "Le groupe joue un refrain acoustique.", SourceProvider = "youtube", CreatedAt = f.Clock.Utc, ReplyCount = 1, LikeCount = 7 };
            f.Db.Discussions.Add(post);
            f.Db.DiscussionReplies.Add(new() { Discussion = post, User = user, Body = "Belle chanson", CreatedAt = f.Clock.Utc });
            f.Db.LikedDiscussions.Add(new() { Discussion = post, User = user });
            await f.Db.SaveChangesAsync();
            await f.Pipeline().InitializeAsync();
            f.Generator.Setup(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>())).ReturnsAsync("Ce refrain donne envie de découvrir le morceau complet.");
            return f;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Store.DisposeAsync(); }
    }
}
