namespace Kinshout.Api.Services.WhatsApp;

public sealed record EditorialStory(
    Guid Id,
    string Title,
    string Body,
    string? Author,
    string? TopicSlug,
    DateTime PublishedAtUtc,
    int? SourceEngagement);

public sealed record KinshasaEvent(
    string Name,
    DateTime LocalStart,
    string Venue,
    string? Price,
    string Url);

public static class EditorialComposer
{
    public static string? Essentiel(EditorialStory story, string url) =>
        Post("📰 L'ESSENTIEL | Kinoiserie", story, url, "Découvrez les détails");

    public static string? Buzz(EditorialStory story, string url)
    {
        var post = Post("🔥 ÇA FAIT LE BUZZ !", story, url, "Voir sur Kinoiserie");
        if (post is null)
            return null;
        return post.Replace("\n\n👉", "\n\nEt vous, qu'en pensez-vous ? 👀\n\n👉", StringComparison.Ordinal);
    }

    public static string? Insolite(EditorialStory story, string url)
    {
        var post = Post("😂 KIN INSOLITE", story, url, "Regardez la suite");
        return post is null ? null : post + "\n\nKitoko !";
    }

    public static string? Musique(EditorialStory story, string url)
    {
        var (emoji, title) = story.TopicSlug switch
        {
            "sport" => ("⚽", "KIN SPORT"),
            "culture" => ("🎭", "KIN CULTURE"),
            _ => ("🎵", "KIN MUSIQUE"),
        };
        return Post($"{emoji} {title}", story, url, "À découvrir sur Kinoiserie");
    }

    public static string? Fierte(EditorialStory story, string url)
    {
        var post = Post("🇨🇩 FIERTÉ CONGOLAISE", story, url, null);
        if (post is null)
            return null;
        return post.Replace("\n\n👉", "\n\nUne belle initiative à découvrir et à partager ! ❤️\n\n👉", StringComparison.Ordinal);
    }

    public static string? Highlights(IReadOnlyList<(string Title, string Url)> items)
    {
        var kept = items.Where(i => !string.IsNullOrWhiteSpace(i.Title) && !string.IsNullOrWhiteSpace(i.Url)).Take(5).ToList();
        if (kept.Count < 3)
            return null;
        var lines = new List<string>
        {
            "🇨🇩 LA SEMAINE EN BREF | Kinoiserie",
            "",
            "Vous avez manqué l'essentiel cette semaine ?",
            "",
        };
        foreach (var item in kept)
        {
            lines.Add($"🔥 {item.Title.Trim()}");
            lines.Add($"👉 {item.Url}");
            lines.Add("");
        }
        lines.Add("Merci de faire partie de la famille Kinoiserie ! ❤️");
        return string.Join("\n", lines);
    }

    public static string? Events(string headline, IReadOnlyList<KinshasaEvent> events, DateTime localNow)
    {
        var upcoming = events
            .Where(e => e.LocalStart >= localNow.Date && !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.Venue) && !string.IsNullOrWhiteSpace(e.Url))
            .OrderBy(e => e.LocalStart)
            .Take(4)
            .ToList();
        if (upcoming.Count == 0)
            return null;
        var lines = new List<string>
        {
            $"📍 {headline}",
            "",
            upcoming.Count == 1 ? "Voici une sortie vérifiée à Kinshasa." : "Voici quelques idées de sorties à Kinshasa !",
            "",
        };
        foreach (var item in upcoming)
        {
            lines.Add($"🎵 *{item.Name.Trim()}*");
            lines.Add($"📅 {WeatherBulletinComposer.FrenchDate(item.LocalStart)} · {item.LocalStart:HH\\hmm}");
            lines.Add($"📍 {item.Venue.Trim()}");
            if (!string.IsNullOrWhiteSpace(item.Price))
                lines.Add($"💵 {item.Price.Trim()}");
            lines.Add($"👉 {item.Url}");
            lines.Add("");
        }
        lines.Add("Bon week-end, les Kinois ! 🇨🇩");
        lines.Add("");
        lines.Add("🌐 https://kinoiserie.com");
        return string.Join("\n", lines);
    }

    public static string PublicationUrl(string webBaseUrl, Guid id, string campaign)
    {
        var root = string.IsNullOrWhiteSpace(webBaseUrl) ? "https://kinoiserie.com" : webBaseUrl.TrimEnd('/');
        return $"{root}/movie/details/{id}?utm_source=whatsapp&utm_medium=channel&utm_campaign={Uri.EscapeDataString(campaign)}";
    }

    private static string? Post(string heading, EditorialStory story, string url, string? action)
    {
        if (string.IsNullOrWhiteSpace(story.Title) || string.IsNullOrWhiteSpace(url))
            return null;
        var sentence = FirstSentences(story.Body);
        var lines = new List<string> { $"*{heading}*", "", story.Title.Trim() };
        if (!string.IsNullOrWhiteSpace(sentence))
        {
            lines.Add("");
            lines.Add(sentence);
        }
        if (!string.IsNullOrWhiteSpace(story.Author))
        {
            lines.Add("");
            lines.Add($"Par {story.Author.Trim()}");
        }
        lines.Add("");
        lines.Add(action is null ? $"👉 {url}" : $"👉 {action} :\n{url}");
        return string.Join("\n", lines);
    }

    public static string FirstSentences(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        var flat = string.Join(' ', body.Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= 180)
            return flat;
        var cut = flat[..180];
        var stop = cut.LastIndexOf('.');
        return (stop > 40 ? cut[..(stop + 1)] : cut.TrimEnd() + "…").Trim();
    }
}
