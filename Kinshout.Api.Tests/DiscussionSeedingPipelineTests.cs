using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public sealed class DiscussionSeedingPipelineTests
{
    [Fact]
    public void SourceMetrics_ReplaceTotals_IgnoreOldSnapshots_AndPreserveOrganicCounters()
    {
        var post = new Discussion { LikeCount = 3, ViewCount = 7 };
        var now = DateTime.UtcNow;
        DiscussionSourceMetrics.Apply(post, 100, 20, ["Un beau refrain"], now);
        var revision = post.SourceMetricsRevision;
        DiscussionSourceMetrics.Apply(post, 100, 20, ["Un beau refrain"], now.AddMinutes(1));
        Assert.Equal(revision, post.SourceMetricsRevision);
        DiscussionSourceMetrics.Apply(post, 110, 18, null, now.AddMinutes(2));
        Assert.Equal(18, post.SourceLikeCount);
        Assert.NotEqual(revision, post.SourceMetricsRevision);
        DiscussionSourceMetrics.Apply(post, 900, 900, [], now);
        Assert.Equal(110, post.SourceViewCount);
        Assert.Equal(18, post.SourceLikeCount);
        Assert.Equal(3, post.LikeCount);
        Assert.Equal(7, post.ViewCount);
        Assert.Contains("refrain", post.SourceReactionTextsJson);
    }

    [Fact]
    public async Task DeveloperEnablement_InitializesOnceWithoutModeratorOrExistingFlags()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.DiscussionSeedRuns.ExecuteDeleteAsync();
        await f.Control.SetFlagsAsync(f.Actor, new());
        Assert.Equal(1, await f.Pipeline().InitializeAsync());
        Assert.Equal(1, await f.Pipeline().InitializeAsync());
        var run = await f.Db.DiscussionSeedRuns.SingleAsync();
        Assert.Equal(Guid.Empty, run.CreatedByUserId);
        Assert.Equal(new DiscussionSeedingFlags(true, true, true), await f.Control.GetFlagsAsync());
        Assert.Equal(1, await f.Pipeline().GenerateAsync());
    }

    [Fact]
    public async Task DeveloperDisablement_DoesNotInitializeAndStopsPendingPublication()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Equal(1, await f.Pipeline().GenerateAsync());
        f.Settings.Enabled = false;
        f.Clock.Utc = f.Clock.Utc.AddHours(1);
        Assert.Equal(0, await f.Pipeline().InitializeAsync());
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
        Assert.Equal(0, await f.Pipeline().PublishDueAsync());
        Assert.Empty(await f.Db.DiscussionReplies.ToListAsync());
    }

    [Fact]
    public async Task HistoricalImport_WithoutMetricsOrReactions_IsSeededOnlyOnce()
    {
        await using var f = await Fixture.CreateAsync();
        var post = await f.Db.Discussions.SingleAsync();
        post.SourceMetricsRevision = null;
        post.SourceReactionTextsJson = "[]";
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, await f.Pipeline().GenerateAsync());
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
        f.Db.ChangeTracker.Clear();
        Assert.NotNull((await f.Db.Discussions.SingleAsync()).SeedProcessedRevision);
        Assert.Single(await f.Db.DiscussionSeedJobs.ToListAsync());
    }

    [Fact]
    public async Task Generation_IsDefaultOff_AndWorksWithoutSourceReactions()
    {
        await using var f = await Fixture.CreateAsync();
        f.Settings.Enabled = false;
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
        f.Settings.Enabled = true;
        f.Db.ChangeTracker.Clear();
        var post = await f.Db.Discussions.SingleAsync();
        DiscussionSourceMetrics.Apply(post, 100, 5, [], f.Clock.Utc.AddMinutes(1));
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, await f.Pipeline().GenerateAsync());
        f.Generator.Verify(g => g.GenerateAsync(It.Is<DiscussionSeedInput>(x => x.Reactions.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_QueuesOnce_UsesPersistentPersona_AndPublishesOnlyWhenDue()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Equal(1, await f.Pipeline().GenerateAsync());
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
        Assert.Equal(0, await f.Pipeline().PublishDueAsync());
        f.Db.ChangeTracker.Clear();
        var job = await f.Db.DiscussionSeedJobs.SingleAsync();
        var persona = await f.Db.Users.SingleAsync(u => u.Id == job.PersonaUserId);
        Assert.Equal(f.Run.Id, persona.SeedRunId);
        Assert.EndsWith("@kinoiserie.invalid", persona.Email);
        Assert.Null(persona.PasswordHash);
        Assert.True(job.ScheduledAt > f.Clock.Utc);
        f.Clock.Utc = job.ScheduledAt.AddSeconds(1);
        Assert.Equal(1, await f.Pipeline().PublishDueAsync());
        Assert.Equal(0, await f.Pipeline().PublishDueAsync());
        f.Db.ChangeTracker.Clear();
        var reply = await f.Db.DiscussionReplies.SingleAsync();
        Assert.Equal(job.Id, reply.Id);
        Assert.Equal(f.Clock.Utc, reply.CreatedAt);
        Assert.Equal(1, (await f.Db.Discussions.SingleAsync()).ReplyCount);
        await f.Control.StopAndHideAsync(f.Actor);
        Assert.Empty(await f.Db.DiscussionReplies.ToListAsync());
        Assert.Single(await f.Db.DiscussionReplies.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0, await f.Pipeline().PublishDueAsync());
    }

    [Fact]
    public async Task StopDuringInference_PreventsQueueCommit()
    {
        await using var f = await Fixture.CreateAsync();
        f.Generator.Setup(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await f.Control.StopAndHideAsync(f.Actor); return Fixture.Comment; });
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
        Assert.Empty(await f.Db.DiscussionSeedJobs.ToListAsync());
        Assert.Empty(await f.Db.Users.Where(u => u.SeedRunId != null).ToListAsync());
    }

    [Fact]
    public async Task StopDuringPublicationCheck_PreventsPublicComment()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Pipeline().GenerateAsync();
        f.Clock.Utc = f.Clock.Utc.AddHours(1);
        f.Moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await f.Control.StopAndHideAsync(f.Actor); });
        Assert.Equal(0, await f.Pipeline().PublishDueAsync());
        Assert.Empty(await f.Db.DiscussionReplies.IgnoreQueryFilters().ToListAsync());
        f.Db.ChangeTracker.Clear();
        Assert.Equal(DiscussionSeedJobStatuses.Cancelled, (await f.Db.DiscussionSeedJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task BlockedDiscussionOrCopiedReaction_DoesNotGenerateVisibleActivity()
    {
        await using var f = await Fixture.CreateAsync();
        f.Generator.Setup(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Un beau refrain qui reste dans la tête");
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
        Assert.Empty(await f.Db.DiscussionSeedJobs.ToListAsync());
        f.Db.ChangeTracker.Clear();
        var post = await f.Db.Discussions.SingleAsync();
        post.PublicFeedBlocked = true;
        DiscussionSourceMetrics.Apply(post, 200, 10, ["Un autre avis"], f.Clock.Utc.AddMinutes(1));
        await f.Db.SaveChangesAsync();
        f.Generator.Invocations.Clear();
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
        f.Generator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MultiplePublishers_CannotPublishTheSameJobTwice()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Pipeline().GenerateAsync();
        f.Clock.Utc = f.Clock.Utc.AddHours(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        f.Moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { if (Interlocked.Increment(ref arrivals) == 2) gate.TrySetResult(); await gate.Task.WaitAsync(TimeSpan.FromSeconds(10)); });
        var results = await Task.WhenAll(Task.Run(() => f.Pipeline().PublishDueAsync()), Task.Run(() => f.Pipeline().PublishDueAsync()));
        Assert.Equal(1, results.Sum());
        Assert.Single(await f.Db.DiscussionReplies.ToListAsync());
        f.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await f.Db.Discussions.SingleAsync()).ReplyCount);
    }

    [Fact]
    public async Task GrowingSource_ContinuesBeyondFormerDailyAndPerPostLimits_ButNotWithoutChanges()
    {
        await using var f = await Fixture.CreateAsync();
        for (var i = 0; i < 101; i++)
        {
            f.Db.ChangeTracker.Clear();
            var post = await f.Db.Discussions.SingleAsync();
            DiscussionSourceMetrics.Apply(post, 100 + i, 5 + i, null, f.Clock.Utc.AddSeconds(i));
            await f.Db.SaveChangesAsync();
            f.Generator.Setup(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync($"Réaction culturelle {i:D3}");
            Assert.Equal(1, await f.Pipeline().GenerateAsync());
            Assert.Equal(0, await f.Pipeline().GenerateAsync());
        }
        Assert.Equal(101, await f.Db.DiscussionSeedJobs.CountAsync());
        Assert.Equal(101, await f.Db.DiscussionSeedJobs.CountAsync(j => j.CreatedAt >= f.Clock.Utc.Date));
        f.Clock.Utc = f.Clock.Utc.AddDays(1);
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
    }

    [Fact]
    public async Task HumanReply_WithStaleTrackedCount_DoesNotOverwritePublishedCommentCount()
    {
        await using var f = await Fixture.CreateAsync();
        var staleThread = await f.Db.Discussions.SingleAsync();
        Assert.Equal(0, staleThread.ReplyCount);
        await f.Pipeline().GenerateAsync();
        f.Clock.Utc = f.Clock.Utc.AddHours(1);
        await f.Pipeline().PublishDueAsync();
        var human = new DiscussionReply { DiscussionId = staleThread.Id, UserId = f.Actor, Body = "Mon avis personnel" };
        await DiscussionReplyWriter.SaveAsync(f.Db, staleThread, human, true, default);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(2, (await f.Db.Discussions.SingleAsync()).ReplyCount);
        await f.Control.StopAndHideAsync(f.Actor);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await f.Db.Discussions.SingleAsync()).ReplyCount);
        Assert.Equal(human.Id, (await f.Db.DiscussionReplies.SingleAsync()).Id);
    }

    [Fact]
    public async Task OrganicParticipation_CancelsPendingJobsAndStopsNewGeneration()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Pipeline().GenerateAsync();
        var thread = await f.Db.Discussions.SingleAsync();
        for (var i = 0; i < 3; i++)
            await DiscussionReplyWriter.SaveAsync(f.Db, thread,
                new DiscussionReply { DiscussionId = thread.Id, UserId = f.Actor, Body = $"Avis humain {i}" }, true, default);
        f.Clock.Utc = f.Clock.Utc.AddHours(1);
        Assert.Equal(0, await f.Pipeline().PublishDueAsync());
        f.Db.ChangeTracker.Clear();
        Assert.Equal(DiscussionSeedJobStatuses.Cancelled, (await f.Db.DiscussionSeedJobs.SingleAsync()).Status);
        Assert.Equal(3, await f.Db.DiscussionReplies.CountAsync());
    }

    [Fact]
    public async Task NextRevision_UsesAnotherHandle_AndLaterSchedule()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Pipeline().GenerateAsync();
        f.Db.ChangeTracker.Clear();
        var first = await f.Db.DiscussionSeedJobs.SingleAsync();
        var thread = await f.Db.Discussions.SingleAsync();
        DiscussionSourceMetrics.Apply(thread, 200, 7, null, f.Clock.Utc.AddMinutes(1));
        await f.Db.SaveChangesAsync();
        f.Generator.Setup(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Quel instrument accompagne le mieux cette chorégraphie ?");
        Assert.Equal(1, await f.Pipeline().GenerateAsync());
        f.Db.ChangeTracker.Clear();
        var second = await f.Db.DiscussionSeedJobs.SingleAsync(j => j.Id != first.Id);
        Assert.NotEqual(first.PersonaUserId, second.PersonaUserId);
        Assert.True(second.ScheduledAt > first.ScheduledAt);
    }

    [Fact]
    public async Task ModerationRejection_IsConsumedWithoutPublishingOrRepeatedInference()
    {
        await using var f = await Fixture.CreateAsync();
        f.Moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AdvertModerationException("Rejected"));
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
        Assert.Equal(0, await f.Pipeline().GenerateAsync());
        f.Generator.Verify(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(await f.Db.DiscussionSeedJobs.ToListAsync());
    }

    [Fact]
    public async Task SchemaRepair_UpgradesFoundationDatabaseWithoutResettingSwitches()
    {
        await using var f = await Fixture.CreateAsync();
        foreach (var column in DiscussionSeedingPipelineSchema.Columns)
            await SeedingTestDatabase.DropColumnAsync(f.Db, column.Table, column.Column);
        f.Db.ChangeTracker.Clear();
        await DbSchemaPatcher.ApplyAsync(f.Db);
        Assert.Equal(new DiscussionSeedingFlags(true, true, false), await f.Control.GetFlagsAsync());
        var post = await f.Db.Discussions.SingleAsync();
        Assert.Null(post.SourceLikeCount);
        Assert.Equal("[]", post.SourceReactionTextsJson);
        DiscussionSourceMetrics.Apply(post, 100, 10, ["Très belle chanson"], f.Clock.Utc);
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, await f.Pipeline().GenerateAsync());
    }

    [Fact]
    public async Task HumanAccountDeletion_PreservesHiddenSeedHistoryAndUsesVisibleCounterDelta()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Pipeline().GenerateAsync();
        f.Clock.Utc = f.Clock.Utc.AddHours(1);
        await f.Pipeline().PublishDueAsync();
        var human = new User { Email = "commenter@example.test", DisplayName = "Human commenter" };
        f.Db.Users.Add(human);
        await f.Db.SaveChangesAsync();
        var thread = await f.Db.Discussions.SingleAsync();
        await DiscussionReplyWriter.SaveAsync(f.Db, thread,
            new DiscussionReply { DiscussionId = thread.Id, UserId = human.Id, Body = "Une contribution humaine" }, true, default);
        await f.Control.StopAndHideAsync(f.Actor);
        f.Db.ChangeTracker.Clear();
        var suppression = new AccountSuppressionService(f.Db, Mock.Of<IUploadStorage>(), NullLogger<AccountSuppressionService>.Instance);
        await suppression.SuppressAsync(human.Id);
        f.Db.ChangeTracker.Clear();
        Assert.False(await f.Db.Users.AnyAsync(u => u.Id == human.Id));
        Assert.Equal(0, (await f.Db.Discussions.SingleAsync()).ReplyCount);
        Assert.Empty(await f.Db.DiscussionReplies.ToListAsync());
        Assert.Single(await f.Db.DiscussionReplies.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task OverdueBacklog_StillPublishesAtDistinctTimesAfterDowntime()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Pipeline().GenerateAsync();
        var post = await f.Db.Discussions.SingleAsync();
        DiscussionSourceMetrics.Apply(post, 200, 7, null, f.Clock.Utc.AddMinutes(1));
        await f.Db.SaveChangesAsync();
        f.Generator.Setup(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Quel instrument accompagne le mieux cette chorégraphie ?");
        await f.Pipeline().GenerateAsync();
        Assert.Equal(2, await f.Db.DiscussionSeedJobs.CountAsync());
        f.Clock.Utc = f.Clock.Utc.AddHours(2);
        Assert.Equal(1, await f.Pipeline().PublishDueAsync());
        Assert.Equal(0, await f.Pipeline().PublishDueAsync());
        f.Clock.Utc = f.Clock.Utc.AddSeconds(f.Settings.MinDelaySeconds + 1);
        Assert.Equal(1, await f.Pipeline().PublishDueAsync());
        f.Db.ChangeTracker.Clear();
        var times = await f.Db.DiscussionReplies.OrderBy(r => r.CreatedAt).Select(r => r.CreatedAt).ToListAsync();
        Assert.True((times[1] - times[0]).TotalSeconds >= f.Settings.MinDelaySeconds);
    }

    [Fact]
    public async Task SourceRefresh_RereadsPersistedObservationInsteadOfStaleImporterEntity()
    {
        await using var f = await Fixture.CreateAsync();
        var stale = await f.Db.Discussions.SingleAsync();
        var now = f.Clock.Utc;
        await DiscussionSourceMetrics.UpdateAsync(f.Db, stale.Id, 300, 30, null, now.AddMinutes(2), default);
        // The importer still tracks the original 100-view entity, but its older
        // observation must not roll the database back to 200 views.
        Assert.Equal(100, stale.SourceViewCount);
        await DiscussionSourceMetrics.UpdateAsync(f.Db, stale.Id, 200, 20, null, now.AddMinutes(1), default);
        f.Db.ChangeTracker.Clear();
        var actual = await f.Db.Discussions.SingleAsync();
        Assert.Equal(300, actual.SourceViewCount);
        Assert.Equal(30, actual.SourceLikeCount);
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTime Utc { get; set; } = DateTime.UtcNow;
        public override DateTimeOffset GetUtcNow() => new(Utc, TimeSpan.Zero);
    }

    private sealed class Fixture(SeedingTestDatabase database) : IAsyncDisposable
    {
        public const string Comment = "La mise en scène apporte une belle énergie au morceau.";
        public KinshoutDbContext Db { get; } = new(database.Options);
        public DiscussionSeedingService Control { get; } = new(database.Options);
        public DiscussionSeedingSettings Settings { get; } = new() { Enabled = true, MinDelaySeconds = 1, MaxDelaySeconds = 2 };
        public Mock<IDiscussionSeedCommentGenerator> Generator { get; } = new();
        public Mock<IAdvertModerationService> Moderation { get; } = new();
        public FixedClock Clock { get; } = new();
        public Guid Actor { get; private set; }
        public DiscussionSeedRun Run { get; private set; } = null!;
        public DiscussionSeedingPipeline Pipeline() => new(database.Options, Options.Create(Settings), Generator.Object,
            Moderation.Object, NullLogger<DiscussionSeedingPipeline>.Instance, Clock);

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(await SeedingTestDatabase.CreateAsync());
            var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(f.Db);
            f.Actor = user.Id;
            f.Run = await f.Control.CreateRunAsync(f.Actor, "pipeline tests");
            await f.Control.SetFlagsAsync(f.Actor, new(true, true, false));
            var post = new Discussion { UserId = user.Id, Title = "Nouveau clip", Body = "Une sortie musicale", SourceProvider = "youtube", SourceExternalId = "clip" };
            DiscussionSourceMetrics.Apply(post, 100, 5, ["Un beau refrain qui reste dans la tête"], f.Clock.Utc);
            f.Db.Discussions.Add(post);
            await f.Db.SaveChangesAsync();
            f.Generator.Setup(g => g.GenerateAsync(It.IsAny<DiscussionSeedInput>(), It.IsAny<CancellationToken>())).ReturnsAsync(Comment);
            f.Moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            return f;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
