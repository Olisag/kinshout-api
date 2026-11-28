namespace Kinshout.Api.Services.WhatsApp;

public sealed record AudiencePrompt(
    string Id,
    DayOfWeek Day,
    string Kind,
    string Text,
    string[] Options);

/// <summary>Fixed, checked prompts. The engine does not invent a new question for each slot.</summary>
public static class AudienceInteractionCatalog
{
    public static IReadOnlyList<AudiencePrompt> All { get; } =
    [
        new("q-week-route", DayOfWeek.Monday, "poll",
            "Question de la semaine\n\nQuel axe vous fait perdre le plus de temps à Kinshasa ?",
            ["Boulevard Lumumba", "Route de Matadi", "By-Pass", "Avenue de la Libération", "Autre"]),
        new("q-week-quartier", DayOfWeek.Monday, "question",
            "Question de la semaine\n\nMbote la famille. Quel quartier de Kinshasa recommandez-vous pour une sortie en famille ?",
            []),
        new("quiz-mbote", DayOfWeek.Tuesday, "quiz",
            "Quiz congolais\n\nEn lingala, « mbote » veut dire :",
            ["Bonjour", "Merci", "Au revoir"]),
        new("quiz-fleuve", DayOfWeek.Tuesday, "quiz",
            "Quiz congolais\n\nQuel fleuve longe Kinshasa ?",
            ["Le fleuve Congo", "Le Nil", "Le Niger"]),
        new("poll-music", DayOfWeek.Wednesday, "poll",
            "Sondage Kinoiserie\n\nQuel style de musique congolaise écoutez-vous le plus ?",
            ["Rumba", "Ndombolo", "Gospel", "Rap", "Autre"]),
        new("poll-weekend-day", DayOfWeek.Wednesday, "poll",
            "Sondage Kinoiserie\n\nVous sortez plutôt quel jour ?",
            ["Vendredi", "Samedi", "Dimanche"]),
        new("q-thursday", DayOfWeek.Thursday, "question",
            "À vous la parole\n\nQuel plat kinois vous manque le plus quand vous êtes loin de la maison ?",
            []),
        new("poll-friday", DayOfWeek.Friday, "poll",
            "Le week-end approche\n\nVous faites quoi en premier ?",
            ["Musique", "Famille", "Sport", "Repos"]),
        new("quiz-saturday", DayOfWeek.Saturday, "quiz",
            "Quiz musique\n\nLa rumba congolaise est un style né :",
            ["Au Congo", "Au Brésil", "En Jamaïque"]),
        new("q-sunday", DayOfWeek.Sunday, "question",
            "La semaine se termine\n\nQu'est-ce qui vous a fait sourire cette semaine ?",
            []),
    ];

    public static AudiencePrompt? Next(DayOfWeek day, IReadOnlyCollection<string> usedIds)
    {
        return All.FirstOrDefault(p => p.Day == day && !usedIds.Contains(p.Id));
    }

    public static string TextFallback(AudiencePrompt prompt)
    {
        var lines = new List<string> { prompt.Text, "" };
        if (prompt.Options.Length > 0)
        {
            foreach (var option in prompt.Options)
                lines.Add($"• {option}");
            lines.Add("");
            lines.Add("Réagissez avec l'emoji qui vous correspond.");
        }
        return string.Join("\n", lines).TrimEnd();
    }
}
