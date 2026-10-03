using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public static class CommunityDefaults
{
    public const string GeneralSlug = "general";
    public const string GeneralName = "Général";
    public const string GeneralDescription = "Discussions générales Kinshasa";
}

public static class CommunitySeed
{
    public static async Task<Community> EnsureGeneralCommunityAsync(
        KinshoutDbContext db,
        CancellationToken ct = default)
    {
        var community = await StageGeneralCommunityAsync(db, ct);
        if (db.Entry(community).State is EntityState.Added or EntityState.Modified)
            await db.SaveChangesAsync(ct);
        return community;
    }

    /// <summary>
    /// Finds k/general, or adds it (and the system import user) to the change tracker without saving.
    /// </summary>
    public static async Task<Community> StageGeneralCommunityAsync(
        KinshoutDbContext db,
        CancellationToken ct = default)
    {
        var existing = db.Communities.Local.FirstOrDefault(c => c.Slug == CommunityDefaults.GeneralSlug)
            ?? await db.Communities.FirstOrDefaultAsync(c => c.Slug == CommunityDefaults.GeneralSlug, ct);
        if (existing is not null)
        {
            // Posting to k/general must always work.
            existing.IsActive = true;
            existing.Visibility = CommunityVisibilities.Public;
            return existing;
        }

        var importUser = await ImportSeed.StageImportUserAsync(db, ct);
        var community = new Community
        {
            Slug = CommunityDefaults.GeneralSlug,
            Name = CommunityDefaults.GeneralName,
            Description = CommunityDefaults.GeneralDescription,
            Visibility = CommunityVisibilities.Public,
            IsActive = true,
            CreatedByUserId = importUser.Id,
        };
        db.Communities.Add(community);
        db.CommunityMembers.Add(new CommunityMember
        {
            Community = community,
            UserId = importUser.Id,
            Role = CommunityMemberRoles.Creator,
            Status = CommunityMemberStatuses.Approved,
        });
        return community;
    }

    /// <summary>Everyone belongs to k/general: makes the user an approved member and returns its id.</summary>
    public static async Task<Guid> JoinGeneralCommunityAsync(
        KinshoutDbContext db,
        Guid userId,
        CancellationToken ct = default)
    {
        var general = await EnsureGeneralCommunityAsync(db, ct);
        var membership = await db.CommunityMembers
            .FirstOrDefaultAsync(m => m.CommunityId == general.Id && m.UserId == userId, ct);

        if (membership is null)
        {
            db.CommunityMembers.Add(new CommunityMember
            {
                CommunityId = general.Id,
                UserId = userId,
                Role = CommunityMemberRoles.Member,
                Status = CommunityMemberStatuses.Approved,
                ReviewedAt = DateTime.UtcNow,
            });
        }
        else if (membership.Status != CommunityMemberStatuses.Approved)
        {
            membership.Status = CommunityMemberStatuses.Approved;
            membership.ReviewedAt = DateTime.UtcNow;
        }
        else
        {
            return general.Id;
        }

        await db.SaveChangesAsync(ct);
        return general.Id;
    }

    /// <summary>Makes every existing account an approved member of k/general.</summary>
    public static async Task BackfillGeneralMembershipsAsync(
        KinshoutDbContext db,
        CancellationToken ct = default)
    {
        var general = await EnsureGeneralCommunityAsync(db, ct);

        var notApproved = await db.CommunityMembers
            .Where(m => m.CommunityId == general.Id && m.Status != CommunityMemberStatuses.Approved)
            .ToListAsync(ct);
        foreach (var membership in notApproved)
        {
            membership.Status = CommunityMemberStatuses.Approved;
            membership.ReviewedAt = DateTime.UtcNow;
        }

        if (notApproved.Count > 0)
            await db.SaveChangesAsync(ct);

        var missingUserIds = await db.Users
            .Where(u => !db.CommunityMembers.Any(m => m.CommunityId == general.Id && m.UserId == u.Id))
            .Select(u => u.Id)
            .ToListAsync(ct);

        foreach (var batch in missingUserIds.Chunk(500))
        {
            db.CommunityMembers.AddRange(batch.Select(userId => new CommunityMember
            {
                CommunityId = general.Id,
                UserId = userId,
                Role = CommunityMemberRoles.Member,
                Status = CommunityMemberStatuses.Approved,
                ReviewedAt = DateTime.UtcNow,
            }));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    public static async Task BackfillMissingDiscussionCommunitiesAsync(
        KinshoutDbContext db,
        CancellationToken ct = default)
    {
        if (!await db.Discussions.AnyAsync(d => d.CommunityId == null, ct))
            return;

        var general = await EnsureGeneralCommunityAsync(db, ct);
        await db.Discussions
            .Where(d => d.CommunityId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.CommunityId, general.Id), ct);
    }
}
