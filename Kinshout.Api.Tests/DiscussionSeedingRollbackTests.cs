using System.Security.Claims;
using Kinshout.Api.Controllers;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Kinshout.Api.Tests;

/// <summary>Relational tests: the rollback relies on real transactions and ExecuteUpdate.</summary>
public class DiscussionSeedingRollbackTests
{
    [Fact]
    public async Task NewDatabase_DefaultsToDisabled_AndRepairDoesNotEnableIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(new DiscussionSeedingFlags(), await fixture.Service.GetFlagsAsync());
        await DbSchemaPatcher.ApplyAsync(fixture.Db);
        Assert.Equal(new DiscussionSeedingFlags(), await fixture.Service.GetFlagsAsync());
        await fixture.Service.SetFlagsAsync(fixture.ActorId, new(true, true, true));
        await DbSchemaPatcher.ApplyAsync(fixture.Db);
        Assert.Equal(new DiscussionSeedingFlags(true, true, true), await fixture.Service.GetFlagsAsync());
    }

    [Fact]
    public async Task StopAndHide_PreservesOrganicActivityAndHistory_AndIsIdempotent()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Service.SetFlagsAsync(f.ActorId, new(true, true, true));
        var run = await f.Service.CreateRunAsync(f.ActorId, "UAT pilot");
        var seeded = await f.SeedAsync(run.Id);
        var beforeTime = seeded.Generated.CreatedAt;

        var preview = await f.Service.PreviewRollbackAsync();
        Assert.Equal(new DiscussionSeedRollbackResult(1, 1, 1, 1), preview);
        Assert.Equal(preview, await f.Service.StopAndHideAsync(f.ActorId));

        f.Db.ChangeTracker.Clear();
        Assert.Equal(new DiscussionSeedingFlags(), await f.Service.GetFlagsAsync());
        Assert.Single(await f.Db.DiscussionReplies.ToListAsync());
        Assert.Equal(2, await f.Db.DiscussionReplies.IgnoreQueryFilters().CountAsync());
        var thread = await f.Db.Discussions.SingleAsync(x => x.Id == seeded.Thread.Id);
        Assert.Equal(1, thread.ReplyCount);
        Assert.Equal(17, thread.LikeCount);
        Assert.Equal(1400, thread.ViewCount);
        Assert.Single(await f.Db.LikedDiscussions.ToListAsync());
        Assert.Single(await f.Db.LikedReplies.ToListAsync());
        Assert.Equal(beforeTime, (await f.Db.DiscussionReplies.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == seeded.Generated.Id)).CreatedAt);
        var hiddenUser = await f.Db.Users.SingleAsync(x => x.SeedRunId == run.Id);
        Assert.NotNull(hiddenUser.SeedHiddenAt);
        Assert.True(await f.Db.Users.AnyAsync(x => x.Id == f.ActorId && x.SeedHiddenAt == null));
        var job = await f.Db.DiscussionSeedJobs.SingleAsync();
        Assert.Equal(DiscussionSeedJobStatuses.Cancelled, job.Status);
        Assert.NotNull(job.CancelledAt);
        var stopped = await f.Db.DiscussionSeedRuns.SingleAsync();
        Assert.Equal(f.ActorId, stopped.StoppedByUserId);
        var stoppedAt = stopped.StoppedAt;

        Assert.Equal(new DiscussionSeedRollbackResult(0, 0, 0, 0), await f.Service.StopAndHideAsync(f.ActorId));
        f.Db.ChangeTracker.Clear();
        Assert.Equal(stoppedAt, (await f.Db.DiscussionSeedRuns.SingleAsync()).StoppedAt);
        Assert.Equal(1, (await f.Db.Discussions.SingleAsync(x => x.Id == thread.Id)).ReplyCount);
        // Turning switches back on cannot resurrect a stopped run or hidden comments.
        await f.Service.SetFlagsAsync(f.ActorId, new(true, true, true));
        Assert.Single(await f.Db.DiscussionReplies.ToListAsync());
        Assert.Equal(DiscussionSeedJobStatuses.Cancelled, (await f.Db.DiscussionSeedJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task ScopedRollback_LeavesOtherRunVisible_ButStopsAllPublishing()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.Service.CreateRunAsync(f.ActorId, "first");
        var second = await f.Service.CreateRunAsync(f.ActorId, "second");
        await f.SeedAsync(first.Id);
        await f.SeedAsync(second.Id);
        await f.Service.SetFlagsAsync(f.ActorId, new(true, true, true));

        await f.Service.StopAndHideAsync(f.ActorId, first.Id);
        f.Db.ChangeTracker.Clear();
        Assert.True(await f.Db.DiscussionSeedRuns.AnyAsync(x => x.Id == second.Id && x.StoppedAt == null));
        Assert.True(await f.Db.DiscussionReplies.AnyAsync(x => x.SeedRunId == second.Id));
        Assert.False(await f.Db.DiscussionReplies.AnyAsync(x => x.SeedRunId == first.Id));
        Assert.True(await f.Db.DiscussionSeedJobs.AnyAsync(x => x.SeedRunId == second.Id && x.Status == DiscussionSeedJobStatuses.Pending));
        Assert.Equal(new DiscussionSeedingFlags(), await f.Service.GetFlagsAsync());
    }

    [Fact]
    public async Task UnknownRun_DoesNotChangeFlagsOrOtherRuns()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Service.SetFlagsAsync(f.ActorId, new(true, true, true));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Service.StopAndHideAsync(f.ActorId, Guid.NewGuid()));
        Assert.Equal(new DiscussionSeedingFlags(true, true, true), await f.Service.GetFlagsAsync());
    }

    [Fact]
    public async Task FailureWhileHiding_RollsBackFlagsCountersAndJobCancellation()
    {
        await using var f = await Fixture.CreateAsync();
        var run = await f.Service.CreateRunAsync(f.ActorId, "failure test");
        var seeded = await f.SeedAsync(run.Id);
        await f.Service.SetFlagsAsync(f.ActorId, new(true, true, true));
        var trigger = f.Db.Database.IsSqlServer()
            ? """
              CREATE TRIGGER FailSeedHide ON dbo.DiscussionReplies AFTER UPDATE AS
              BEGIN
                  IF UPDATE(SeedHiddenAt) THROW 51001, 'simulate database failure', 1;
              END;
              """
            : """
              CREATE TRIGGER FailSeedHide BEFORE UPDATE OF SeedHiddenAt ON DiscussionReplies
              BEGIN SELECT RAISE(ABORT, 'simulate database failure'); END;
              """;
        await f.Db.Database.ExecuteSqlRawAsync(trigger);

        await Assert.ThrowsAnyAsync<DbException>(() => f.Service.StopAndHideAsync(f.ActorId));

        f.Db.ChangeTracker.Clear();
        Assert.Equal(new DiscussionSeedingFlags(true, true, true), await f.Service.GetFlagsAsync());
        Assert.Equal(2, await f.Db.DiscussionReplies.CountAsync());
        Assert.Equal(2, (await f.Db.Discussions.SingleAsync(x => x.Id == seeded.Thread.Id)).ReplyCount);
        Assert.Null((await f.Db.DiscussionSeedRuns.SingleAsync()).StoppedAt);
        Assert.Equal(DiscussionSeedJobStatuses.Pending, (await f.Db.DiscussionSeedJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task MissingControlRow_FailsClosed()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.DiscussionSeedingControls.ExecuteDeleteAsync();
        Assert.Equal(new DiscussionSeedingFlags(), await f.Service.GetFlagsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SetFlagsAsync(f.ActorId, new(true, true, true)));
    }

    private sealed class Fixture(SeedingTestDatabase database) : IAsyncDisposable
    {
        public KinshoutDbContext Db { get; } = new(database.Options);
        public DiscussionSeedingService Service { get; } = new(database.Options);
        public Guid ActorId { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await SeedingTestDatabase.CreateAsync());
            var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(fixture.Db);
            fixture.ActorId = user.Id;
            return fixture;
        }

        public async Task<(Discussion Thread, DiscussionReply Generated)> SeedAsync(Guid runId)
        {
            var persona = new User { Email = $"{Guid.NewGuid():N}@seed.invalid", DisplayName = "Test persona", SeedRunId = runId };
            var thread = new Discussion { UserId = ActorId, Title = "Thread", Body = "Body", ReplyCount = 2, LikeCount = 17, ViewCount = 1400 };
            var generated = new DiscussionReply { Discussion = thread, User = persona, SeedRunId = runId, Body = "Generated", CreatedAt = DateTime.UtcNow.AddMinutes(-5) };
            var organic = new DiscussionReply { Discussion = thread, UserId = ActorId, Body = "Human contribution" };
            Db.Users.Add(persona);
            Db.Discussions.Add(thread);
            Db.DiscussionReplies.AddRange(generated, organic);
            Db.LikedDiscussions.Add(new LikedDiscussion { Discussion = thread, UserId = ActorId });
            Db.LikedReplies.Add(new LikedReply { Reply = organic, UserId = ActorId });
            Db.DiscussionSeedJobs.Add(new DiscussionSeedJob
            {
                SeedRunId = runId, DiscussionId = thread.Id, PersonaUserId = persona.Id,
                IdempotencyKey = Guid.NewGuid().ToString("N"), Body = "Queued", ScheduledAt = DateTime.UtcNow.AddHours(1),
            });
            await Db.SaveChangesAsync();
            return (thread, generated);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
