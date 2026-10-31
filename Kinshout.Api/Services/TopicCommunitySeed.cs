using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

/// <summary>Public topic communities used to route imported social discussions.</summary>
public static class TopicCommunitySeed
{
    private static readonly (string Slug, string Name, string Description)[] Topics =
    [
        ("sport", "Sport & foot", "Léopards, clubs locaux et débats sportifs à Kinshasa"),
        ("culture", "Culture & loisirs", "Musique, clips, festivals et culture kinoise"),
        ("ville", "Vie de quartier", "Quotidien, circulation, marchés et communes de Kinshasa"),
        ("education", "Éducation", "EXETAT, écoles, universités et études à Kinshasa"),
    ];

    public static async Task EnsureTopicCommunitiesAsync(
        KinshoutDbContext db,
        CancellationToken ct = default)
    {
        var importUser = await ImportSeed.EnsureImportUserAsync(db, ct);
        var changed = false;

        foreach (var (slug, name, description) in Topics)
        {
            var existing = db.Communities.Local.FirstOrDefault(c => c.Slug == slug)
                ?? await db.Communities.FirstOrDefaultAsync(c => c.Slug == slug, ct);
            if (existing is not null)
                continue;

            var community = new Community
            {
                Slug = slug,
                Name = name,
                Description = description,
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
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }
}
