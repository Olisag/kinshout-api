using System.Data;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

/// <summary>Reply mutation and counter delta commit together, without writing a stale tracked count.</summary>
internal static class DiscussionReplyWriter
{
    internal static async Task SaveAsync(KinshoutDbContext db, Discussion discussion, DiscussionReply reply, bool adding, CancellationToken ct)
    {
        if (adding) db.DiscussionReplies.Add(reply); else db.DiscussionReplies.Remove(reply);
        if (!db.Database.IsRelational())
        {
            discussion.ReplyCount = Math.Max(0, discussion.ReplyCount + (adding ? 1 : -1));
            await db.SaveChangesAsync(ct);
            return;
        }
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await DiscussionSeedingService.AcquireControlLockAsync(db, ct);
            var exists = await db.DiscussionReplies.AsNoTracking().AnyAsync(r => r.Id == reply.Id, ct);
            // Stable reply IDs make an ambiguous-commit retry idempotent.
            if (exists != adding)
            {
                await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct);
                await db.Discussions.Where(d => d.Id == discussion.Id).ExecuteUpdateAsync(s => s.SetProperty(
                    d => d.ReplyCount, d => adding ? d.ReplyCount + 1 : (d.ReplyCount > 0 ? d.ReplyCount - 1 : 0)), ct);
            }
            await tx.CommitAsync(ct);
        });
        db.ChangeTracker.AcceptAllChanges();
    }
}
