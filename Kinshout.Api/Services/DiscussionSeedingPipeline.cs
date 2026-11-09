using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public sealed class DiscussionSeedingPipeline(
    DbContextOptions<KinshoutDbContext> options,
    IOptions<DiscussionSeedingSettings> settings,
    IDiscussionSeedCommentGenerator generator,
    IAdvertModerationService moderation,
    ILogger<DiscussionSeedingPipeline> logger,
    TimeProvider? clock = null)
{
    private DateTime Now => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    private static readonly string[] Handles = ["MasoloYaKin", "RumbaNaBiso", "MwanaMboka", "KinCulture", "NdomboloClub", "LisoloYaLelo", "MbokaVibes", "SangoCulture", "KinMelodie", "BisoEnsemble", "RumbaCorner", "MasoloMix"];
    private static readonly string[] Styles = ["réaction concise et chaleureuse", "observation sur la musique et le rythme", "question courte sur le sujet", "avis culturel nuancé"];

    /// <summary>Called once by each worker on startup after schema repair. No moderator account is needed.</summary>
    public Task<int> InitializeAsync(CancellationToken ct = default)
    {
        if (!settings.Value.Enabled) return Task.FromResult(0);
        return LockedAsync(async db =>
        {
            if (!settings.Value.Enabled) return 0;
            if (!await db.DiscussionSeedRuns.AnyAsync(r => r.StoppedAt == null, ct))
                db.DiscussionSeedRuns.Add(new DiscussionSeedRun
                {
                    Name = "Developer-enabled imported discussions", CreatedAt = Now,
                    // Guid.Empty identifies a developer/configuration action, not an application user.
                    CreatedByUserId = Guid.Empty,
                });
            await db.DiscussionSeedingControls.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.GenerationEnabled, true)
                .SetProperty(x => x.PublishingEnabled, true)
                .SetProperty(x => x.CombinedLikesEnabled, true)
                .SetProperty(x => x.UpdatedAt, (DateTime?)Now)
                .SetProperty(x => x.UpdatedByUserId, (Guid?)null), ct);
            await db.SaveChangesAsync(ct);
            return 1;
        }, ct);
    }

    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        var config = settings.Value;
        if (!config.Enabled) return 0;
        await using var read = new KinshoutDbContext(options);
        if (!await read.DiscussionSeedingControls.AnyAsync(x => x.Id == 1 && x.GenerationEnabled, ct)) return 0;
        var run = await read.DiscussionSeedRuns.AsNoTracking().Where(x => x.StoppedAt == null)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (run is null) return 0;
        var todayUtc = Now.Date;
        if (await read.DiscussionSeedJobs.CountAsync(j => j.CreatedAt >= todayUtc, ct) >= Math.Clamp(config.MaxCommentsPerDay, 1, 1000)) return 0;
        var candidates = await Eligible(read).AsNoTracking()
            .Where(d => ((d.SourceMetricsRevision == null && d.SeedProcessedRevision == null)
                    || (d.SourceMetricsRevision != null && d.SourceMetricsRevision != d.SeedProcessedRevision))
                && read.DiscussionSeedJobs.Count(j => j.SeedRunId == run.Id && j.DiscussionId == d.Id) < Math.Clamp(config.MaxCommentsPerPost, 1, 30))
            .OrderByDescending(d => d.SourceMetricsObservedAt).Take(Math.Clamp(config.MaxJobsPerTick, 1, 20)).ToListAsync(ct);
        var queued = 0;
        foreach (var candidate in candidates)
        {
            if (!settings.Value.Enabled || !await CanGenerateAsync(read, run.Id, ct)) break;
            try
            {
                var reactions = JsonSerializer.Deserialize<string[]>(candidate.SourceReactionTextsJson) ?? [];
                var revision = Revision(candidate);
                var index = (int)(BitConverter.ToUInt32(SHA256.HashData(candidate.Id.ToByteArray())) % (uint)Handles.Length);
                var priorCount = await read.DiscussionSeedJobs.CountAsync(j => j.DiscussionId == candidate.Id && j.SeedRunId == run.Id, ct);
                var voice = (index + priorCount) % Handles.Length;
                var body = await generator.GenerateAsync(new(candidate.Title, candidate.SourceRawBody ?? candidate.Body,
                    reactions, Styles[voice % Styles.Length]), ct);
                if (string.IsNullOrWhiteSpace(body) || body.Length > 400
                    || PoliticsFeedFilter.IsBlocked(null, null, body, null, null, null)
                    || !DiscussionSeedCommentGenerator.IsOriginal(body, reactions))
                {
                    await ConsumeRevisionAsync(candidate, run.Id, ct);
                    continue;
                }
                await moderation.EnsureTextAllowedAsync(body, ct);
                queued += await LockedAsync(async db =>
                {
                    if (!settings.Value.Enabled || !await CanGenerateAsync(db, run.Id, ct)) return 0;
                    var current = await Eligible(db).FirstOrDefaultAsync(x => x.Id == candidate.Id, ct);
                    if (current is null || Revision(current) != revision
                        || current.SeedProcessedRevision == revision) return 0;
                    var existingJobs = await db.DiscussionSeedJobs.Where(j => j.DiscussionId == current.Id && j.SeedRunId == run.Id).ToListAsync(ct);
                    var today = Now.Date;
                    if (existingJobs.Count >= Math.Clamp(config.MaxCommentsPerPost, 1, 30)
                        || await db.DiscussionSeedJobs.CountAsync(j => j.CreatedAt >= today, ct) >= Math.Clamp(config.MaxCommentsPerDay, 1, 1000)) return 0;
                    var replies = await db.DiscussionReplies.Where(r => r.DiscussionId == current.Id).Select(r => r.Body).ToListAsync(ct);
                    if (!DiscussionSeedCommentGenerator.IsOriginal(body, replies.Concat(existingJobs.Select(j => j.Body))))
                    { current.SeedProcessedRevision = revision; await db.SaveChangesAsync(ct); return 0; }
                    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v1:{run.Id}:{current.Id}:{revision}"))).ToLowerInvariant();
                    if (await db.DiscussionSeedJobs.AnyAsync(j => j.IdempotencyKey == key, ct)) return 0;
                    var slot = (index + existingJobs.Count) % Handles.Length;
                    var email = $"seed.{run.Id:N}.{slot}@kinoiserie.invalid";
                    var persona = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
                    if (persona is null)
                    {
                        var handle = $"{Handles[slot]}_{run.Id.ToString("N")[..6]}";
                        if (await db.Users.AnyAsync(u => u.DisplayName == handle, ct)) handle += "_" + Guid.NewGuid().ToString("N")[..4];
                        persona = new User { Email = email, DisplayName = handle, SeedRunId = run.Id };
                        db.Users.Add(persona);
                    }
                    if (persona.SeedRunId != run.Id || persona.SeedHiddenAt != null) return 0;
                    var gap = Math.Clamp(config.MinDelaySeconds, 1, 3600);
                    var max = Math.Clamp(config.MaxDelaySeconds, gap, 7200);
                    var latest = existingJobs.Select(j => j.ScheduledAt).DefaultIfEmpty(Now).Max();
                    var scheduled = (latest > Now ? latest : Now).AddSeconds(RandomNumberGenerator.GetInt32(gap, max + 1));
                    db.DiscussionSeedJobs.Add(new DiscussionSeedJob
                    {
                        SeedRunId = run.Id, DiscussionId = current.Id, PersonaUserId = persona.Id,
                        Body = body, IdempotencyKey = key, SourceRevision = revision,
                        CreatedAt = Now, ScheduledAt = scheduled,
                    });
                    current.SeedProcessedRevision = revision;
                    await db.SaveChangesAsync(ct);
                    return 1;
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AdvertModerationException) { await ConsumeRevisionAsync(candidate, run.Id, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Seeding generation failed for {DiscussionId}; no comment published", candidate.Id); }
        }
        return queued;
    }

    public async Task<int> PublishDueAsync(CancellationToken ct = default)
    {
        if (!settings.Value.Enabled) return 0;
        await using var read = new KinshoutDbContext(options);
        if (!await read.DiscussionSeedingControls.AnyAsync(x => x.Id == 1 && x.PublishingEnabled, ct)) return 0;
        var now = Now;
        var jobs = await read.DiscussionSeedJobs.AsNoTracking()
            .Where(j => j.Status == DiscussionSeedJobStatuses.Pending && j.ScheduledAt <= now)
            .OrderBy(j => j.ScheduledAt).Take(Math.Clamp(settings.Value.MaxJobsPerTick, 1, 20)).ToListAsync(ct);
        var published = 0;
        foreach (var candidate in jobs)
        {
            if (!settings.Value.Enabled || !await read.DiscussionSeedingControls.AnyAsync(x => x.Id == 1 && x.PublishingEnabled, ct)) break;
            var cutoff = Now.AddSeconds(-Math.Clamp(settings.Value.MinDelaySeconds, 1, 3600));
            if (await read.DiscussionSeedJobs.AnyAsync(j => j.DiscussionId == candidate.DiscussionId
                && j.PublishedAt > cutoff, ct)) continue;
            // The expensive check happens outside the database lock. Publication rechecks
            // the durable switches, job, run, discussion and persona under that lock.
            var allowed = true;
            try { await moderation.EnsureTextAllowedAsync(candidate.Body, ct); }
            catch (AdvertModerationException) { allowed = false; }
            published += await LockedAsync(async db =>
            {
                var job = await db.DiscussionSeedJobs.FirstOrDefaultAsync(j => j.Id == candidate.Id, ct);
                if (job is null || job.Status != DiscussionSeedJobStatuses.Pending || job.ScheduledAt > Now) return 0;
                if (!settings.Value.Enabled || !await db.DiscussionSeedingControls.AnyAsync(x => x.Id == 1 && x.PublishingEnabled, ct)) return 0;
                var thread = await Eligible(db).FirstOrDefaultAsync(d => d.Id == job.DiscussionId, ct);
                var runActive = await db.DiscussionSeedRuns.AnyAsync(r => r.Id == job.SeedRunId && r.StoppedAt == null, ct);
                var personaActive = await db.Users.AnyAsync(u => u.Id == job.PersonaUserId && u.SeedRunId == job.SeedRunId && u.SeedHiddenAt == null, ct);
                if (!allowed || !runActive || !personaActive || thread is null || job.Body != candidate.Body
                    || PoliticsFeedFilter.IsBlocked(null, null, job.Body, null, null, null))
                { job.Status = DiscussionSeedJobStatuses.Cancelled; job.CancelledAt = Now; await db.SaveChangesAsync(ct); return 0; }
                var minimumPublishedAt = Now.AddSeconds(-Math.Clamp(settings.Value.MinDelaySeconds, 1, 3600));
                if (await db.DiscussionSeedJobs.AnyAsync(j => j.DiscussionId == job.DiscussionId
                    && j.PublishedAt > minimumPublishedAt, ct)) return 0;
                // Deterministic reply ID also protects a retry after an ambiguous commit.
                if (!await db.DiscussionReplies.IgnoreQueryFilters().AnyAsync(r => r.Id == job.Id, ct))
                {
                    db.DiscussionReplies.Add(new DiscussionReply
                    {
                        Id = job.Id, DiscussionId = job.DiscussionId, UserId = job.PersonaUserId,
                        SeedRunId = job.SeedRunId, Body = job.Body, CreatedAt = Now,
                    });
                    await db.Discussions.Where(d => d.Id == job.DiscussionId).ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.ReplyCount, d => d.ReplyCount + 1), ct);
                }
                job.Status = DiscussionSeedJobStatuses.Published;
                job.PublishedAt = Now;
                await db.SaveChangesAsync(ct);
                return 1;
            }, ct);
        }
        return published;
    }

    private IQueryable<Discussion> Eligible(KinshoutDbContext db)
    {
        var organicLimit = Math.Clamp(settings.Value.StopAfterOrganicReplies, 1, 20);
        return db.Discussions.Where(d =>
        d.SourceProvider != null && d.SourceProvider != DiscussionSourceProvider.Kinshout
        && d.Visibility == CommunityVisibilities.Public && !d.PublicFeedBlocked && d.TopicSlug != "politique"
        && (d.Community == null || (d.Community.IsActive && d.Community.Visibility == CommunityVisibilities.Public))
        && db.DiscussionReplies.Count(r => r.DiscussionId == d.Id && r.SeedRunId == null
            && r.User.SeedRunId == null && !r.User.Email.EndsWith("@kinshout.system")) < organicLimit);
    }

    private static Task<bool> CanGenerateAsync(KinshoutDbContext db, Guid run, CancellationToken ct) =>
        db.DiscussionSeedingControls.AnyAsync(x => x.Id == 1 && x.GenerationEnabled
            && db.DiscussionSeedRuns.Any(r => r.Id == run && r.StoppedAt == null), ct);

    private static string Revision(Discussion source) => source.SourceMetricsRevision
        ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"initial:{source.Id}:{source.Title}:{source.SourceRawBody ?? source.Body}"))).ToLowerInvariant();

    private Task<int> ConsumeRevisionAsync(Discussion source, Guid run, CancellationToken ct) => LockedAsync(async db =>
    {
        if (!await CanGenerateAsync(db, run, ct)) return 0;
        return await db.Discussions.Where(d => d.Id == source.Id && d.SourceMetricsRevision == source.SourceMetricsRevision)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.SeedProcessedRevision, Revision(source)), ct);
    }, ct);

    private async Task<int> LockedAsync(Func<KinshoutDbContext, Task<int>> action, CancellationToken ct)
    {
        await using var strategyDb = new KinshoutDbContext(options);
        return await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = new KinshoutDbContext(options);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await DiscussionSeedingService.AcquireControlLockAsync(db, ct);
            var result = await action(db);
            await tx.CommitAsync(ct);
            return result;
        });
    }
}

public sealed class DiscussionSeedingWorker(IServiceScopeFactory scopes, IOptions<DiscussionSeedingSettings> settings,
    ILogger<DiscussionSeedingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for startup schema repair before the first tick. The deployment gate
        // is false by default; no network requests or jobs run while disabled.
        var initialized = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.Value.TickSeconds, 10, 3600)), stoppingToken);
                if (!settings.Value.Enabled) continue;
                using var scope = scopes.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<DiscussionSeedingPipeline>();
                if (!initialized)
                {
                    await pipeline.InitializeAsync(stoppingToken);
                    initialized = true;
                }
                await pipeline.GenerateAsync(stoppingToken);
                await pipeline.PublishDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Discussion seeding tick failed; retrying on next tick"); }
        }
    }
}
