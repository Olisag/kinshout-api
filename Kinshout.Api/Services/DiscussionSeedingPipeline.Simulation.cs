using System.Security.Cryptography;
using System.Text.Json;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public sealed partial class DiscussionSeedingPipeline
{
    // Smooth UTC pacing peaking around 18:00 Kinshasa. No expiry or missed-day backfill.
    internal static int DueInteractions(DateTime utc, int daily)
    {
        var t = utc.TimeOfDay.TotalDays;
        var phase = 2 * Math.PI * 17 / 24;
        var cumulative = t + 0.55 / (2 * Math.PI) * (Math.Sin(2 * Math.PI * t - phase) + Math.Sin(phase));
        return (int)Math.Floor(Math.Clamp(cumulative, 0, 1) * Math.Clamp(daily, 0, 10000));
    }

    private async Task<DiscussionSimulationDay> DayAsync(KinshoutDbContext db, CancellationToken ct)
    {
        var day = await db.DiscussionSimulationDays.SingleOrDefaultAsync(x => x.Day == Now.Date, ct);
        if (day is null) db.DiscussionSimulationDays.Add(day = new() { Day = Now.Date });
        day.HeartbeatAt = Now;
        return day;
    }

    private async Task<List<Guid>> ProfileIdsAsync(KinshoutDbContext db, Guid run, CancellationToken ct)
    {
        var active = db.Users.Where(u => u.SeedRunId == run && u.SeedHiddenAt == null);
        var profileIds = await active.Select(u => u.Id).ToListAsync(ct);
        var count = Math.Clamp(settings.Value.ProfileCount, 1, 5000);
        if (profileIds.Count >= count) return profileIds;

        // Include hidden slots when choosing new addresses. Hidden personas are never revived.
        var usedEmails = (await db.Users.Where(u => u.SeedRunId == run).Select(u => u.Email).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextSlot = usedEmails.Count;
        while (profileIds.Count < count)
        {
            var slot = nextSlot++;
            var email = $"simulation.{run:N}.{slot:D4}@kinoiserie.invalid";
            if (!usedEmails.Add(email)) continue;
            var user = new User
            {
                Email = email,
                DisplayName = $"{Handles[slot % Handles.Length]}_{slot + 1:D4}",
                SeedRunId = run,
            };
            db.Users.Add(user);
            profileIds.Add(user.Id);
        }
        return profileIds;
    }

    internal async Task<List<Discussion>> PopularCandidatesAsync(KinshoutDbContext db, CancellationToken ct)
    {
        var posts = await DiscussionSourceMapper.OrderByPopular(Eligible(db).AsNoTracking())
            .Take(100).ToListAsync(ct);
        return posts.Where(p => !DiscussionSeedCommentGenerator.HasPoliticalContent(p.Title)
            && !DiscussionSeedCommentGenerator.HasPoliticalContent(p.SourceRawBody ?? p.Body)
            && !PoliticsFeedFilter.IsBlocked(null, null, p.Title + " " + (p.SourceRawBody ?? p.Body), null, null, null)).ToList();
    }

    // Rank one receives twice the selection weight of rank two, and so on.
    // Use the same allocation for likes and comments without feeding seeded totals into ranking.
    internal static int PickPopularRank(int candidateCount, double fraction)
    {
        var total = 0d;
        for (var rank = 1; rank <= candidateCount; rank++)
            total += 1d / rank;
        var remaining = total * fraction;
        for (var rank = 1; rank <= candidateCount; rank++)
        {
            remaining -= 1d / rank;
            if (remaining < 0) return rank - 1;
        }
        return candidateCount - 1;
    }

    private static int PickPopularRank(int candidateCount) =>
        PickPopularRank(candidateCount, RandomNumberGenerator.GetInt32(1_000_000) / 1_000_000d);

    private async Task<int> GenerateSimulationAsync(CancellationToken ct)
    {
        Discussion? source = null;
        Guid runId = default, personaId = default;
        string key = "";
        var reserved = await LockedAsync(async db =>
        {
            var run = await db.DiscussionSeedRuns.Where(r => r.StoppedAt == null).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
            if (run is null || !await CanGenerateAsync(db, run.Id, ct)) return 0;
            var day = await DayAsync(db, ct);
            var profiles = await ProfileIdsAsync(db, run.Id, ct);
            var queued = await db.DiscussionSeedJobs.CountAsync(j => j.CreatedAt >= Now.Date
                && (j.Status == DiscussionSeedJobStatuses.Pending || j.Status == DiscussionSeedJobStatuses.Published), ct);
            var config = settings.Value;
            var cost = Math.Max(0.000001m, config.EstimatedCostPerAttemptUsd);
            if (queued >= DueInteractions(Now, config.DailyComments)
                || day.LastAttemptAt > Now.AddSeconds(-Math.Clamp(config.MinDelaySeconds, 1, 3600))
                || day.GenerationAttempts >= Math.Clamp(config.MaxDailyGenerationAttempts, 1, 1000)
                || day.ReservedCostUsd + cost > config.DailyCostLimitUsd)
            { await db.SaveChangesAsync(ct); return 0; }
            var candidates = await PopularCandidatesAsync(db, ct);
            if (candidates.Count == 0) { await db.SaveChangesAsync(ct); return 0; }
            source = candidates[PickPopularRank(candidates.Count)];
            // Budget reservations persist before external inference, including failed attempts.
            day.GenerationAttempts++;
            day.ReservedCostUsd += cost;
            day.LastAttemptAt = Now;
            runId = run.Id;
            personaId = profiles[RandomNumberGenerator.GetInt32(profiles.Count)];
            key = $"simulation:{Now:yyyyMMdd}:{day.GenerationAttempts}";
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Simulation budget {Day}: attempts={Attempts} reservedEstimateUsd={Cost} limitUsd={Limit}",
                day.Day, day.GenerationAttempts, day.ReservedCostUsd, config.DailyCostLimitUsd);
            return 1;
        }, ct);
        if (reserved == 0 || source is null) return 0;
        try
        {
            var reactions = JsonSerializer.Deserialize<string[]>(source.SourceReactionTextsJson) ?? [];
            var body = await generator.GenerateAsync(new(source.Title, source.SourceRawBody ?? source.Body,
                reactions, Styles[RandomNumberGenerator.GetInt32(Styles.Length)]), ct);
            if (string.IsNullOrWhiteSpace(body) || body.Length > 400
                || PoliticsFeedFilter.IsBlocked(null, null, body, null, null, null)
                || !DiscussionSeedCommentGenerator.IsOriginal(body, reactions)) return 0;
            await moderation.EnsureTextAllowedAsync(body, ct);
            return await LockedAsync(async db =>
            {
                if (!settings.Value.Enabled || !await CanGenerateAsync(db, runId, ct)
                    || !await Eligible(db).AnyAsync(d => d.Id == source.Id, ct)
                    || !await db.Users.AnyAsync(u => u.Id == personaId && u.SeedRunId == runId && u.SeedHiddenAt == null, ct)
                    || await db.DiscussionSeedJobs.AnyAsync(j => j.IdempotencyKey == key, ct)) return 0;
                var prior = await db.DiscussionSeedJobs.Where(j => j.DiscussionId == source.Id).Select(j => j.Body).ToListAsync(ct);
                prior.AddRange(await db.DiscussionReplies.Where(r => r.DiscussionId == source.Id).Select(r => r.Body).ToListAsync(ct));
                if (!DiscussionSeedCommentGenerator.IsOriginal(body, prior)) return 0;
                // A midnight-crossing inference belongs to the reservation day; don't create a next-day burst.
                if (!key.StartsWith($"simulation:{Now:yyyyMMdd}:", StringComparison.Ordinal)) return 0;
                var min = Math.Clamp(settings.Value.MinDelaySeconds, 1, 3600);
                var max = Math.Clamp(settings.Value.MaxDelaySeconds, min, 7200);
                db.DiscussionSeedJobs.Add(new()
                {
                    SeedRunId = runId, DiscussionId = source.Id, PersonaUserId = personaId,
                    IdempotencyKey = key, Body = body, SourceRevision = source.SourceMetricsRevision,
                    CreatedAt = Now, ScheduledAt = Now.AddSeconds(RandomNumberGenerator.GetInt32(min, max + 1)),
                });
                await db.SaveChangesAsync(ct);
                return 1;
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AdvertModerationException) { logger.LogInformation("Simulation generation rejected by moderation for {Post}", source.Id); return 0; }
        catch (Exception ex) { logger.LogWarning(ex, "Simulation generation failed for {Post}; bounded retry on later tick", source.Id); return 0; }
    }

    public Task<int> PublishSimulationLikesAsync(CancellationToken ct = default)
    {
        if (!settings.Value.Enabled || !settings.Value.SimulationEnabled || !ModerationReady) return Task.FromResult(0);
        return LockedAsync(async db =>
        {
            var run = await db.DiscussionSeedRuns.Where(r => r.StoppedAt == null).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
            if (run is null || !await db.DiscussionSeedingControls.AnyAsync(c => c.Id == 1 && c.PublishingEnabled, ct)) return 0;
            var day = await DayAsync(db, ct);
            if (day.LastLikeAt > Now.AddSeconds(-Math.Clamp(settings.Value.TickSeconds, 10, 3600))) return 0;
            var count = await db.DiscussionSimulatedLikes.CountAsync(x => x.CreatedAt >= Now.Date, ct);
            var needed = Math.Clamp(DueInteractions(Now, settings.Value.DailyLikes) - count, 0, Math.Clamp(settings.Value.MaxLikesPerTick, 1, 10));
            var added = 0;
            if (needed > 0)
            {
                var candidates = await PopularCandidatesAsync(db, ct);
                if (candidates.Count > 0)
                {
                    var profiles = await ProfileIdsAsync(db, run.Id, ct);
                    // A saturated discussion cannot take another like from this pool. Try
                    // the next draw so it does not consume the entire tick's allowance.
                    while (added < needed && candidates.Count > 0)
                    {
                        var rank = PickPopularRank(candidates.Count);
                        var post = candidates[rank];
                        candidates.RemoveAt(rank);
                        var usedIds = (await db.DiscussionSimulatedLikes.Where(x => x.DiscussionId == post.Id)
                            .Select(x => x.PersonaUserId).ToListAsync(ct)).ToHashSet();
                        var available = profiles.Where(p => !usedIds.Contains(p)).ToList();
                        while (added < needed && available.Count > 0)
                        {
                            var index = RandomNumberGenerator.GetInt32(available.Count);
                            db.DiscussionSimulatedLikes.Add(new() { SeedRunId = run.Id, DiscussionId = post.Id,
                                PersonaUserId = available[index], CreatedAt = Now });
                            available.RemoveAt(index);
                            added++;
                        }
                    }
                }
            }
            day.LastLikeAt = Now;
            await db.SaveChangesAsync(ct);
            return added;
        }, ct);
    }
}
