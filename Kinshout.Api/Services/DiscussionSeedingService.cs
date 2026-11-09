using System.Data;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public sealed record DiscussionSeedingFlags(
    bool GenerationEnabled = false,
    bool PublishingEnabled = false,
    bool CombinedLikesEnabled = false);

public sealed record DiscussionSeedRollbackResult(
    int Runs, int PendingJobs, int VisibleComments, int VisibleProfiles);

public sealed record DiscussionSeedJobReview(Guid Id, Guid RunId, Guid DiscussionId, string Persona,
    string Body, string Status, DateTime ScheduledAt, DateTime? PublishedAt);

public interface IDiscussionSeedingService
{
    Task<IReadOnlyList<DiscussionSeedJobReview>> ListJobsAsync(int limit = 50, CancellationToken ct = default);
    Task<DiscussionSeedingFlags> GetFlagsAsync(CancellationToken ct = default);
    Task<DiscussionSeedingFlags> SetFlagsAsync(Guid actorId, DiscussionSeedingFlags flags, CancellationToken ct = default);
    Task<DiscussionSeedRun> CreateRunAsync(Guid actorId, string name, CancellationToken ct = default);
    Task<DiscussionSeedRollbackResult> PreviewRollbackAsync(Guid? runId = null, CancellationToken ct = default);
    Task<DiscussionSeedRollbackResult> StopAndHideAsync(Guid actorId, Guid? runId = null, CancellationToken ct = default);
}

/// <summary>
/// Operations and rollback controls. Generation and publication use the separate default-off pipeline.
/// Future queue/publish operations MUST take the same control-row write lock, then recheck
/// switches and StoppedAt inside their transaction before committing any generated records.
/// </summary>
public sealed class DiscussionSeedingService(DbContextOptions<KinshoutDbContext> options) : IDiscussionSeedingService
{
    public async Task<IReadOnlyList<DiscussionSeedJobReview>> ListJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var db = new KinshoutDbContext(options);
        return await db.DiscussionSeedJobs.AsNoTracking().Join(db.Users, j => j.PersonaUserId, u => u.Id,
            (j, u) => new { Job = j, u.DisplayName }).OrderByDescending(x => x.Job.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(x => new DiscussionSeedJobReview(x.Job.Id, x.Job.SeedRunId, x.Job.DiscussionId,
                x.DisplayName, x.Job.Body, x.Job.Status, x.Job.ScheduledAt, x.Job.PublishedAt)).ToListAsync(ct);
    }

    public async Task<DiscussionSeedingFlags> GetFlagsAsync(CancellationToken ct = default)
    {
        await using var db = new KinshoutDbContext(options);
        return await db.DiscussionSeedingControls.AsNoTracking()
            .Where(x => x.Id == 1)
            .Select(x => new DiscussionSeedingFlags(x.GenerationEnabled, x.PublishingEnabled, x.CombinedLikesEnabled))
            .SingleOrDefaultAsync(ct) ?? new DiscussionSeedingFlags();
    }

    public Task<DiscussionSeedingFlags> SetFlagsAsync(
        Guid actorId, DiscussionSeedingFlags flags, CancellationToken ct = default) =>
        ExecuteLockedAsync(async db =>
        {
            await db.DiscussionSeedingControls.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.GenerationEnabled, flags.GenerationEnabled)
                .SetProperty(x => x.PublishingEnabled, flags.PublishingEnabled)
                .SetProperty(x => x.CombinedLikesEnabled, flags.CombinedLikesEnabled)
                .SetProperty(x => x.UpdatedAt, (DateTime?)DateTime.UtcNow)
                .SetProperty(x => x.UpdatedByUserId, (Guid?)actorId), ct);
            return flags;
        }, ct);

    public Task<DiscussionSeedRun> CreateRunAsync(Guid actorId, string name, CancellationToken ct = default)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 120)
            throw new ArgumentException("Le nom doit contenir entre 1 et 120 caractères.", nameof(name));

        var runId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;
        return ExecuteLockedAsync(async db =>
        {
            // If a transient failure obscured a successful commit, reuse that run
            // rather than creating a second campaign when the execution strategy retries.
            var existing = await db.DiscussionSeedRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, ct);
            if (existing is not null)
                return existing;
            var run = new DiscussionSeedRun { Id = runId, Name = trimmed, CreatedByUserId = actorId, CreatedAt = createdAt };
            db.DiscussionSeedRuns.Add(run);
            await db.SaveChangesAsync(ct);
            return run;
        }, ct);
    }

    public async Task<DiscussionSeedRollbackResult> PreviewRollbackAsync(Guid? runId = null, CancellationToken ct = default)
    {
        await using var db = new KinshoutDbContext(options);
        await EnsureRunExistsAsync(db, runId, ct);
        return await CountAffectedAsync(db, runId, ct);
    }

    /// <summary>
    /// Stops the whole pipeline even when hiding only one run. Does not delete data, change
    /// human likes, rewrite timestamps or restore an old snapshot over new organic activity.
    /// A stopped run is terminal; re-enabling flags cannot resurrect its jobs or comments.
    /// </summary>
    public Task<DiscussionSeedRollbackResult> StopAndHideAsync(
        Guid actorId, Guid? runId = null, CancellationToken ct = default) =>
        ExecuteLockedAsync(async db =>
        {
            await EnsureRunExistsAsync(db, runId, ct);
            var affected = await CountAffectedAsync(db, runId, ct);
            var now = DateTime.UtcNow;

            await db.DiscussionSeedingControls.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.GenerationEnabled, false)
                .SetProperty(x => x.PublishingEnabled, false)
                .SetProperty(x => x.CombinedLikesEnabled, false)
                .SetProperty(x => x.UpdatedAt, (DateTime?)now)
                .SetProperty(x => x.UpdatedByUserId, (Guid?)actorId), ct);

            await db.DiscussionSeedRuns.Where(x => (runId == null || x.Id == runId) && x.StoppedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.StoppedAt, (DateTime?)now)
                    .SetProperty(x => x.StoppedByUserId, (Guid?)actorId), ct);

            await db.DiscussionSeedJobs
                .Where(x => (runId == null || x.SeedRunId == runId) && x.Status == DiscussionSeedJobStatuses.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, DiscussionSeedJobStatuses.Cancelled)
                    .SetProperty(x => x.CancelledAt, (DateTime?)now), ct);

            // Subtract only comments newly hidden by this call, atomically. Do not replace
            // counters from a prior snapshot: organic replies may have arrived since seeding.
            var comments = db.DiscussionReplies.IgnoreQueryFilters()
                .Where(x => x.SeedRunId != null && (runId == null || x.SeedRunId == runId) && x.SeedHiddenAt == null);
            var counts = await comments.GroupBy(x => x.DiscussionId)
                .Select(g => new { DiscussionId = g.Key, Count = g.Count() }).ToListAsync(ct);
            foreach (var group in counts)
            {
                await db.Discussions.Where(x => x.Id == group.DiscussionId).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.ReplyCount, x => x.ReplyCount >= group.Count ? x.ReplyCount - group.Count : 0), ct);
            }
            await comments.ExecuteUpdateAsync(s => s.SetProperty(x => x.SeedHiddenAt, (DateTime?)now), ct);

            await db.Users.Where(x => x.SeedRunId != null && (runId == null || x.SeedRunId == runId) && x.SeedHiddenAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeedHiddenAt, (DateTime?)now), ct);

            return affected;
        }, ct);

    private static async Task EnsureRunExistsAsync(KinshoutDbContext db, Guid? runId, CancellationToken ct)
    {
        if (runId is Guid id && !await db.DiscussionSeedRuns.AnyAsync(x => x.Id == id, ct))
            throw new KeyNotFoundException("Campagne introuvable.");
    }

    private static async Task<DiscussionSeedRollbackResult> CountAffectedAsync(KinshoutDbContext db, Guid? runId, CancellationToken ct) =>
        new(
            await db.DiscussionSeedRuns.CountAsync(x => (runId == null || x.Id == runId) && x.StoppedAt == null, ct),
            await db.DiscussionSeedJobs.CountAsync(x => (runId == null || x.SeedRunId == runId) && x.Status == DiscussionSeedJobStatuses.Pending, ct),
            await db.DiscussionReplies.IgnoreQueryFilters().CountAsync(x => x.SeedRunId != null && (runId == null || x.SeedRunId == runId) && x.SeedHiddenAt == null, ct),
            await db.Users.CountAsync(x => x.SeedRunId != null && (runId == null || x.SeedRunId == runId) && x.SeedHiddenAt == null, ct));

    private async Task<T> ExecuteLockedAsync<T>(Func<KinshoutDbContext, Task<T>> action, CancellationToken ct)
    {
        await using var strategyDb = new KinshoutDbContext(options);
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A fresh context on each retry prevents stale tracked data from leaking into a retry.
            await using var db = new KinshoutDbContext(options);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await AcquireControlLockAsync(db, ct);
            var result = await action(db);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    internal static async Task AcquireControlLockAsync(KinshoutDbContext db, CancellationToken ct)
    {
        // UPDATE obtains a database write lock, also on SQLite. All future publishers
        // must use this same lock, not an in-process semaphore that misses other instances.
        var updated = await db.DiscussionSeedingControls.Where(x => x.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.GenerationEnabled, x => x.GenerationEnabled), ct);
        if (updated != 1)
            throw new InvalidOperationException("Discussion seeding schema is not initialized; apply the migration first.");
    }
}
