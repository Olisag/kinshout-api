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
        if (db.Entry(community).State == EntityState.Added)
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
            return existing;

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
