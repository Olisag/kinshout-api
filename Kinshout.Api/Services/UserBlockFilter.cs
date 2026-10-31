using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

/// <summary>
/// Mutual content hiding for personal blocks: if A blocks B (or vice versa),
/// neither sees the other's discussions/adverts.
/// </summary>
public static class UserBlockFilter
{
    public static IQueryable<Discussion> WhereAuthorVisibleTo(
        IQueryable<Discussion> query,
        KinshoutDbContext db,
        Guid? viewerUserId)
    {
        if (viewerUserId is null)
            return query;

        return query.Where(d =>
            !db.UserBlocks.Any(b =>
                (b.BlockerUserId == viewerUserId && b.BlockedUserId == d.UserId)
                || (b.BlockerUserId == d.UserId && b.BlockedUserId == viewerUserId)));
    }

    public static IQueryable<Advert> WhereAuthorVisibleTo(
        IQueryable<Advert> query,
        KinshoutDbContext db,
        Guid? viewerUserId)
    {
        if (viewerUserId is null)
            return query;

        return query.Where(a =>
            !db.UserBlocks.Any(b =>
                (b.BlockerUserId == viewerUserId && b.BlockedUserId == a.UserId)
                || (b.BlockerUserId == a.UserId && b.BlockedUserId == viewerUserId)));
    }

    public static Task<bool> AreMutuallyBlockedAsync(
        KinshoutDbContext db,
        Guid userA,
        Guid userB,
        CancellationToken ct = default)
    {
        if (userA == userB)
            return Task.FromResult(false);

        return db.UserBlocks.AnyAsync(
            b => (b.BlockerUserId == userA && b.BlockedUserId == userB)
                || (b.BlockerUserId == userB && b.BlockedUserId == userA),
            ct);
    }
}
