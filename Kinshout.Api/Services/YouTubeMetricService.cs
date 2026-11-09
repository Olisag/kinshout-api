using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IYouTubeMetricService
{
    Task<IReadOnlyList<string>> GetRefreshTargetsAsync(CancellationToken ct = default);

    Task<int> RecordAsync(IReadOnlyList<YouTubeMetricPointDto> metrics, CancellationToken ct = default);
}

public class YouTubeMetricService(KinshoutDbContext db) : IYouTubeMetricService
{
    public const int RefreshWindowHours = 72;
    public const int RefreshLimit = 40;

    public async Task<IReadOnlyList<string>> GetRefreshTargetsAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddHours(-RefreshWindowHours);
        return await db.Discussions.AsNoTracking()
            .Where(d => d.SourceProvider == "youtube"
                && d.SourceExternalId != null
                && d.CreatedAt >= since)
            .OrderByDescending(d => d.SourceEngagementScore ?? 0)
            .ThenByDescending(d => d.CreatedAt)
            .Select(d => d.SourceExternalId!)
            .Take(RefreshLimit)
            .ToListAsync(ct);
    }

    public async Task<int> RecordAsync(IReadOnlyList<YouTubeMetricPointDto> metrics, CancellationToken ct = default)
    {
        if (metrics.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var ids = metrics
            .Select(m => m.ExternalId.Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var discussions = await db.Discussions
            .Where(d => d.SourceProvider == "youtube" && d.SourceExternalId != null && ids.Contains(d.SourceExternalId))
            .ToListAsync(ct);
        var byId = discussions.ToDictionary(d => d.SourceExternalId!, StringComparer.OrdinalIgnoreCase);
        var stored = 0;

        foreach (var metric in metrics)
        {
            var externalId = metric.ExternalId.Trim();
            if (externalId.Length == 0)
                continue;

            var publishedAt = metric.PublishedAt?.ToUniversalTime();
            var score = TrendScore(metric.Views, metric.Likes ?? 0, publishedAt, now);
            db.VideoMetricSnapshots.Add(new VideoMetricSnapshot
            {
                Provider = "youtube",
                ExternalId = externalId,
                Views = Math.Max(0, metric.Views),
                Likes = Math.Max(0, metric.Likes ?? 0),
                Comments = Math.Max(0, metric.Comments),
                TrendScore = score,
                FetchedAt = now,
            });

            if (byId.TryGetValue(externalId, out var discussion))
            {
                discussion.SourceEngagementScore = score;
            }

            stored++;
        }

        await db.SaveChangesAsync(ct);
        foreach (var metric in metrics)
            if (byId.TryGetValue(metric.ExternalId.Trim(), out var discussion))
                await DiscussionSourceMetrics.UpdateAsync(db, discussion.Id, metric.Views, metric.Likes, metric.SourceReactions, now, ct);
        return stored;
    }

    /// <summary>Engagement drops toward a quarter of its weight once a clip is 72 hours old.</summary>
    public static int TrendScore(int views, int likes, DateTime? publishedAtUtc, DateTime nowUtc)
    {
        var ageHours = publishedAtUtc is null
            ? 0
            : Math.Max(0, (nowUtc - publishedAtUtc.Value).TotalHours);
        var freshness = Math.Max(0.25, 1 - (0.75 * Math.Min(ageHours, RefreshWindowHours) / RefreshWindowHours));
        var engagement = Math.Max(0, views) + (Math.Max(0, likes) * 5);
        return (int)Math.Round(engagement * freshness, MidpointRounding.AwayFromZero);
    }
}
