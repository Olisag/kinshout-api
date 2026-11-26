using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

/// <summary>
/// Labeled discussion hosts for imported social posts.
/// Display names include "· Animateur" so the post is not mistaken for a member's.
/// Imported discussions do not receive fabricated replies.
/// </summary>
public static class ImportHostSeed
{
    public sealed record HostPersona(string Email, string DisplayName, string[] Keywords);

    public static readonly HostPersona[] Hosts =
    [
        new(
            "host.musique@kinshout.system",
            "Kino Musique · Animateur",
            [
                "fally", "ipupa", "ferre", "wenge", "jb mpiana", "werra", "werras", "koffi", "olomide",
                "innoss", "rumba", "ndombolo", "concert", "clip", "musique", "chanson", "artiste",
            ]),
        new(
            "host.mode@kinshout.system",
            "Kino Mode · Animateur",
            [
                "american in paris", "sapologie", "sapeur", "la sape", "mode", "fashion", "look",
                "défilé", "defile", "style",
            ]),
        new(
            "host.sport@kinshout.system",
            "Kino Sport · Animateur",
            ["léopard", "leopard", "football", "foot", "sport", "match", "coupe du monde", "tp mazembe", "vclub"]),
        new(
            "host.foi@kinshout.system",
            "Kino Foi · Animateur",
            [
                "pasteur", "évêque", "eveque", "église", "eglise", "réveil", "reveil", "culte", "gospel",
                "chorale", "témoignage", "temoignage", "prophète", "prophete",
            ]),
        new(
            "host.humour@kinshout.system",
            "Kino Humour · Animateur",
            ["humoriste", "comédien", "comedien", "sketch", "stand up", "stand-up", "blague", "rire"]),
        new(
            "host.influence@kinshout.system",
            "Kino Influence · Animateur",
            [
                "influenceur", "influenceuse", "tiktok", "tiktoker", "créateur de contenu",
                "createur de contenu", "instagrammeur", "instagrammeuse", "viral", "buzz",
            ]),
        new(
            "host.culture@kinshout.system",
            "Kino Culture · Animateur",
            ["film", "festival", "cinéma", "cinema", "spectacle", "culture", "insolite"]),
        new(
            "host.ville@kinshout.system",
            "Kinshasa Quotidien · Animateur",
            ["quartier", "circulation", "embouteillage", "pluie", "marché", "transport", "taxi", "commune", "avenue"]),
        new(
            "host.education@kinshout.system",
            "Scola Kin · Animateur",
            ["exetat", "école", "ecole", "université", "universite", "étudiant", "etudiant", "examen", "résultat", "resultat"]),
    ];

    public static async Task<IReadOnlyList<User>> EnsureHostsAsync(
        KinshoutDbContext db,
        CancellationToken ct = default)
    {
        var users = new List<User>(Hosts.Length);
        foreach (var persona in Hosts)
        {
            var user = db.Users.Local.FirstOrDefault(u => u.Email == persona.Email)
                ?? await db.Users.FirstOrDefaultAsync(u => u.Email == persona.Email, ct);

            if (user is null)
            {
                user = new User
                {
                    Email = persona.Email,
                    DisplayName = persona.DisplayName,
                };
                db.Users.Add(user);
            }
            else if (!string.Equals(user.DisplayName, persona.DisplayName, StringComparison.Ordinal))
            {
                user.DisplayName = persona.DisplayName;
            }

            users.Add(user);
        }

        if (users.Any(u => db.Entry(u).State == EntityState.Added || db.Entry(u).State == EntityState.Modified))
            await db.SaveChangesAsync(ct);

        return users;
    }

    public static User PickHost(IReadOnlyList<User> hosts, string title, string body)
    {
        if (hosts.Count == 0)
            throw new InvalidOperationException("Aucun animateur d'import disponible.");

        var blob = $"{title} {body}".ToLowerInvariant();
        for (var i = 0; i < Hosts.Length && i < hosts.Count; i++)
        {
            if (Hosts[i].Keywords.Any(k => blob.Contains(k, StringComparison.Ordinal)))
                return hosts[i];
        }

        var hash = Math.Abs(string.GetHashCode(blob, StringComparison.Ordinal));
        return hosts[hash % hosts.Count];
    }

    public static IReadOnlyList<(User Host, string Body)> BuildStarterComments(
        IReadOnlyList<User> hosts,
        User author,
        string title,
        string body)
    {
        return [];
    }
}
