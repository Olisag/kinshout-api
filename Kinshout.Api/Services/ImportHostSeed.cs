using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

/// <summary>
/// Labeled discussion hosts for imported social posts.
/// Display names intentionally include "· Animateur" so they are not mistaken for civilian sockpuppets.
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
            "host.culture@kinshout.system",
            "Kino Culture · Animateur",
            ["film", "humour", "festival", "cinéma", "cinema", "spectacle", "culture"]),
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
        var others = hosts.Where(h => h.Id != author.Id).ToList();
        if (others.Count == 0)
            return [];

        var seed = Math.Abs(string.GetHashCode($"{title}|{body}", StringComparison.Ordinal));
        var first = others[seed % others.Count];
        var second = others.Count > 1 ? others[(seed + 1) % others.Count] : null;

        var fr = StarterFr[seed % StarterFr.Length];
        var ln = StarterLn[(seed / 3) % StarterLn.Length];
        var en = StarterEn[(seed / 7) % StarterEn.Length];

        var comments = new List<(User, string)> { (first, seed % 2 == 0 ? fr : ln) };
        if (second is not null && seed % 3 != 0)
            comments.Add((second, seed % 2 == 0 ? en : fr));

        return comments;
    }

    private static readonly string[] StarterFr =
    [
        "Merci pour le partage — ça parle à beaucoup de Kinois.",
        "Intéressant. Les retours du quartier vont être utiles ici.",
        "Sujet clair. Hâte de lire vos expériences concrètes.",
        "Bonne base de discussion. Qu'est-ce qui vous marque le plus ?",
    ];

    private static readonly string[] StarterLn =
    [
        "Matondi mingi mpo na sango oyo.",
        "Tala boye — tozali koyoka makanisi ya ba quartier.",
        "Sango ya ntina. Tolobela yango malamu awa.",
    ];

    private static readonly string[] StarterEn =
    [
        "Solid share — curious what folks around Kin think.",
        "Clear topic. Real neighbourhood takes welcome.",
        "Good discussion starter for Kinshasa voices.",
    ];
}
