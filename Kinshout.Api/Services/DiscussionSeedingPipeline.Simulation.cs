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

    internal static double TrendScore(Discussion post, int genuineReplies, DateTime utc)
    {
        var hours = Math.Max(0, (utc - (post.ExternalPublishedAt ?? post.CreatedAt)).TotalHours);
        var source = Math.Log(1 + Math.Max(0, post.SourceLikeCount ?? 0))
            + 0.25 * Math.Log(1 + Math.Max(0, post.SourceViewCount ?? 0))
            + Math.Log(1 + Math.Max(0, post.SourceEngagementScore ?? 0));
        return (1 + source + 2 * Math.Log(1 + Math.Max(0, post.LikeCount))
            + 3 * Math.Log(1 + genuineReplies)) / Math.Pow(1 + hours / 12, 1.3);
    }

    private async Task<DiscussionSimulationDay> DayAsync(KinshoutDbContext db, CancellationToken ct)
    {
        var day = await db.DiscussionSimulationDays.SingleOrDefaultAsync(x => x.Day == Now.Date, ct);
        if (day is null) db.DiscussionSimulationDays.Add(day = new() { Day = Now.Date });
        day.HeartbeatAt = Now;
        return day;
    }

    private async Task<List<User>> ProfilesAsync(KinshoutDbContext db, Guid run, CancellationToken ct)
    {
        var profiles = await db.Users.Where(u => u.SeedRunId == run && u.SeedHiddenAt == null).OrderBy(u => u.Email).ToListAsync(ct);
        foreach (var profile in profiles)
            if (!profile.DisplayName.StartsWith("Simulation_", StringComparison.Ordinal))
                profile.DisplayName = "Simulation_" + profile.DisplayName;
        var count = Math.Clamp(settings.Value.ProfileCount, 1, 1500);
        for (var slot = profiles.Count; slot < count; slot++)
        {
            var user = new User
            {
                Email = $"simulation.{run:N}.{slot:D4}@kinoiserie.invalid",
                DisplayName = $"Simulation_{Handles[slot % Handles.Length]}_{run.ToString("N")[..6]}_{slot:D4}",
                SeedRunId = run,
            };
            db.Users.Add(user);
            profiles.Add(user);
        }
        return profiles;
    }

    private async Task<Discussion?> TrendingAsync(KinshoutDbContext db, CancellationToken ct)
    {
        // Bound the scan and don't let our own activity feed back into ranking.
        var posts = await Eligible(db).OrderByDescending(d => d.ExternalPublishedAt ?? d.CreatedAt)
            .Take(500).ToListAsync(ct);
        posts = posts.Where(p => !DiscussionSeedCommentGenerator.HasPoliticalContent(p.Title)
            && !DiscussionSeedCommentGenerator.HasPoliticalContent(p.SourceRawBody ?? p.Body)
            && !PoliticsFeedFilter.IsBlocked(null, null, p.Title + " " + (p.SourceRawBody ?? p.Body), null, null, null)).ToList();
        if (posts.Count == 0) return null;
        var ids = posts.Select(d => d.Id).ToArray();
        var genuine = await db.DiscussionReplies.Where(r => ids.Contains(r.DiscussionId) && r.SeedRunId == null
            && r.User.SeedRunId == null && !r.User.Email.EndsWith("@kinshout.system"))
            .GroupBy(r => r.DiscussionId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Id, g => g.Count, ct);
        var ranked = posts.Select(p => (Post: p, Score: TrendScore(p, genuine.GetValueOrDefault(p.Id), Now)))
            .OrderByDescending(x => x.Score).Take(100).ToList();
        var needle = RandomNumberGenerator.GetInt32(1_000_000) / 1_000_000d * ranked.Sum(x => x.Score);
        foreach (var item in ranked)
        {
            needle -= item.Score;
            if (needle <= 0) return item.Post;
        }
        return ranked[^1].Post;
    }

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
            var profiles = await ProfilesAsync(db, run.Id, ct);
            var queued = await db.DiscussionSeedJobs.CountAsync(j => j.CreatedAt >= Now.Date
                && (j.Status == DiscussionSeedJobStatuses.Pending || j.Status == DiscussionSeedJobStatuses.Published), ct);
            var config = settings.Value;
            var cost = Math.Max(0.000001m, config.EstimatedCostPerAttemptUsd);
            if (queued >= DueInteractions(Now, config.DailyComments)
                || day.LastAttemptAt > Now.AddSeconds(-Math.Clamp(config.MinDelaySeconds, 1, 3600))
                || day.GenerationAttempts >= Math.Clamp(config.MaxDailyGenerationAttempts, 1, 1000)
                || day.ReservedCostUsd + cost > config.DailyCostLimitUsd)
            { await db.SaveChangesAsync(ct); return 0; }
            source = await TrendingAsync(db, ct);
            if (source is null) { await db.SaveChangesAsync(ct); return 0; }
            // Budget reservations persist before external inference, including failed attempts.
            day.GenerationAttempts++;
            day.ReservedCostUsd += cost;
            day.LastAttemptAt = Now;
            runId = run.Id;
            personaId = profiles[RandomNumberGenerator.GetInt32(profiles.Count)].Id;
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
            var profiles = await ProfilesAsync(db, run.Id, ct);
            var count = await db.DiscussionSimulatedLikes.CountAsync(x => x.CreatedAt >= Now.Date, ct);
            var needed = Math.Clamp(DueInteractions(Now, settings.Value.DailyLikes) - count, 0, 4);
            var post = needed > 0 ? await TrendingAsync(db, ct) : null;
            var added = 0;
            if (post is not null)
            {
                var used = await db.DiscussionSimulatedLikes.Where(x => x.DiscussionId == post.Id).Select(x => x.PersonaUserId).ToListAsync(ct);
                var available = profiles.Where(p => !used.Contains(p.Id)).ToList();
                while (added < needed && available.Count > 0)
                {
                    var index = RandomNumberGenerator.GetInt32(available.Count);
                    db.DiscussionSimulatedLikes.Add(new() { SeedRunId = run.Id, DiscussionId = post.Id,
                        PersonaUserId = available[index].Id, CreatedAt = Now });
                    available.RemoveAt(index);
                    added++;
                }
            }
            day.LastLikeAt = Now;
            await db.SaveChangesAsync(ct);
            return added;
        }, ct);
    }
}
