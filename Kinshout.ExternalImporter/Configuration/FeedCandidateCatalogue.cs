using System.Text.Json;

namespace Kinshout.ExternalImporter.Configuration;

/// <summary>
/// Appendix A names, one row per platform. A run searches the names listed
/// for that platform. A blank handle is not invented; a verified registry
/// handle is left as it is.
/// </summary>
internal static class FeedCandidateCatalogue
{
    public static void Report()
    {
        var candidates = Load();
        if (candidates.Count == 0)
        {
            Console.WriteLine("Feed catalogue: no candidate file loaded.");
            return;
        }

        Console.WriteLine(
            $"Feed catalogue: {candidates.Count} names included on their own platforms " +
            $"(TikTok {Count(candidates, "tiktok")}, YouTube {Count(candidates, "youtube")}, " +
            $"Facebook {Count(candidates, "facebook")}, X {Count(candidates, "x")}).");
    }

    public static IReadOnlyList<CatalogueSubject> Subjects(string platform, IEnumerable<SourceRegistryEntry> registry)
    {
        var known = registry
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .GroupBy(source => source.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var subjects = new List<CatalogueSubject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in Load().Where(candidate =>
                     candidate.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase)))
        {
            var name = candidate.Name.Trim();
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                continue;

            known.TryGetValue(name, out var source);
            subjects.Add(From(name, source));
        }

        if (platform.Equals("youtube", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var source in known.Values.Where(source => source.Import))
            {
                if (seen.Add(source.Name.Trim()))
                    subjects.Add(From(source.Name.Trim(), source));
            }
        }

        return subjects;
    }

    private static CatalogueSubject From(string name, SourceRegistryEntry? source)
    {
        var query = string.IsNullOrWhiteSpace(source?.SearchQuery) ? Quote(name) : source.SearchQuery.Trim();
        var community = string.IsNullOrWhiteSpace(source?.CommunitySlug) ? "influence" : source.CommunitySlug.Trim();
        var mentions = source?.Mentions is { Count: > 0 } terms
            ? terms
            : (IReadOnlyList<string>)[name];
        return new CatalogueSubject(name, query, community, mentions);
    }

    private static string Quote(string text) =>
        text.Contains(' ') ? $"\"{text}\"" : text;

    private static int Count(IReadOnlyList<FeedCandidate> candidates, string platform) =>
        candidates.Count(candidate => candidate.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<FeedCandidate> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "feed-candidate-catalogue.json");
        if (!File.Exists(path))
            return [];

        var document = JsonSerializer.Deserialize<FeedCandidateFile>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return document?.Candidates ?? [];
    }

    private sealed class FeedCandidateFile
    {
        public List<FeedCandidate> Candidates { get; set; } = [];
    }
}

internal sealed record CatalogueSubject(
    string Name,
    string Query,
    string CommunitySlug,
    IReadOnlyList<string> Mentions);

internal sealed class FeedCandidate
{
    public string Platform { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "included";
}
