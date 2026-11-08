namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>
/// Files a Congolese social post by what it is about.
/// A joke or a piece of gossip stays in humour even when a name is mentioned.
/// Everyday talk that names no topic goes to the general room.
/// </summary>
internal static class CongoleseSocialTopic
{
    public static string Slug(string? title, string? body)
    {
        var blob = $"{title} {body}".ToLowerInvariant();

        if (Contains(blob, "clip officiel", "nouveau single", "nouvel album", "audio officiel"))
            return "musique";

        if (Contains(blob,
                "sketch", "blague", "mdr", "ptdr", "humour", "humor", "comedie", "comédie",
                "stand-up", "standup", "potin", "rumeur", "cancan", "buzz", "tendance", "insolite"))
            return "humour";

        if (Contains(blob,
                "embouteillage", "circulation", "marché", "marche ", "quartier", "commune",
                "gombe", "limete", "limeté", "ngaliema", "masina", "kinois", "kinoise", "pluie", "transport"))
            return "ville";

        if (Contains(blob, "sapologie", "sapeur", "la sape", "mode kin", "look du jour"))
            return "mode";

        if (Contains(blob, "football", "léopard", "leopard", "match ", "tp mazembe", "as vita"))
            return "sport";

        if (Contains(blob,
                "fally", "ipupa", "ferre gola", "wenge", "ndombolo", "rumba congolaise"))
            return "musique";

        return "general";
    }

    private static bool Contains(string blob, params string[] needles) =>
        needles.Any(needle => blob.Contains(needle, StringComparison.Ordinal));
}
