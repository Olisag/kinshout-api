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
        ("musique", "Musique congolaise",
            "Fally Ipupa, Ferre Gola, Wenge Musica, JB Mpiana, Werrason, Koffi Olomidé, Innoss'B, rumba, clips, concerts, interviews, gossip artistique"),
        ("mode", "Mode & sapologie",
            "Mode congolaise, sapologie, sape, American in Paris, fashion Kinshasa, looks et tendances actuelles"),
        ("culture", "Culture & divertissement", "Cinéma, humour, festivals et culture kinoise hors musique"),
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

            if (existing is null)
            {
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
                continue;
            }

            if (!string.Equals(existing.Name, name, StringComparison.Ordinal)
                || !string.Equals(existing.Description, description, StringComparison.Ordinal))
            {
                existing.Name = name;
                existing.Description = description;
                existing.IsActive = true;
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>Keyword-first routing so music/fashion gossip lands in the right community.</summary>
    public static string? InferSlug(string title, string body)
    {
        var blob = $"{title} {body}".ToLowerInvariant();

        if (ContainsAny(blob,
                "fally", "ipupa", "ferre gola", "ferrê", "wenge", "jb mpiana", "werras", "werra",
                "koffi", "olomide", "olomidé", "innoss", "innos b", "rumba", "ndombolo",
                "concert", "clip officiel", "nouveau single", "feat ", "featuring"))
            return "musique";

        if (ContainsAny(blob,
                "american in paris", "americain a paris", "américain à paris",
                "sapologie", "sapeur", "la sape", "mode kin", "fashion", "défilé", "defile",
                "look du jour", "style congo", "habillement"))
            return "mode";

        if (ContainsAny(blob, "léopard", "leopard", "football", "foot ", "match", "tp mazembe", "vclub", "as vita"))
            return "sport";

        if (ContainsAny(blob, "exetat", "école", "ecole", "université", "universite", "étudiant", "etudiant", "examen"))
            return "education";

        if (ContainsAny(blob, "quartier", "embouteillage", "circulation", "marché", "commune", "pluie"))
            return "ville";

        if (ContainsAny(blob, "film", "humour", "festival", "cinéma", "cinema", "spectacle"))
            return "culture";

        return null;
    }

    private static bool ContainsAny(string blob, params string[] needles) =>
        needles.Any(n => blob.Contains(n, StringComparison.Ordinal));
}
