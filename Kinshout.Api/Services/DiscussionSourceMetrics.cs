using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kinshout.Api.Models;
using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public static class DiscussionSourceMetrics
{
    public static async Task UpdateAsync(KinshoutDbContext sourceDb, Guid discussionId, long? views, long? likes,
        IReadOnlyList<string>? reactions, DateTime observedAt, CancellationToken ct)
    {
        if (views is null && likes is null && reactions is null) return;
        if (!sourceDb.Database.IsRelational())
        {
            var tracked = await sourceDb.Discussions.FirstOrDefaultAsync(d => d.Id == discussionId, ct);
            if (tracked is null) return;
            Apply(tracked, views, likes, reactions, observedAt);
            await sourceDb.SaveChangesAsync(ct);
            return;
        }
        var options = sourceDb.ContextOptions;
        await using var strategyDb = new KinshoutDbContext(options);
        await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = new KinshoutDbContext(options);
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            await DiscussionSeedingService.AcquireControlLockAsync(db, ct);
            // Reread inside the lock: an older refresh cannot overwrite a newer
            // snapshot that committed after the original importer loaded the post.
            var current = await db.Discussions.FirstOrDefaultAsync(d => d.Id == discussionId, ct);
            if (current is not null)
            {
                Apply(current, views, likes, reactions, observedAt);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        });
    }

    public static void Apply(Discussion discussion, long? views, long? likes,
        IReadOnlyList<string>? reactions, DateTime observedAt)
    {
        if (views is null && likes is null && reactions is null) return;
        if (discussion.SourceMetricsObservedAt is { } previous && observedAt < previous) return;
        if (views is not null) discussion.SourceViewCount = Math.Max(0, views.Value);
        if (likes is not null) discussion.SourceLikeCount = Math.Max(0, likes.Value);
        if (reactions is not null)
            discussion.SourceReactionTextsJson = JsonSerializer.Serialize(reactions
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()[..Math.Min(x.Trim().Length, 1000)])
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Take(20));
        discussion.SourceMetricsObservedAt = observedAt;
        var payload = JsonSerializer.Serialize(new { discussion.SourceViewCount, discussion.SourceLikeCount, discussion.SourceReactionTextsJson });
        discussion.SourceMetricsRevision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
