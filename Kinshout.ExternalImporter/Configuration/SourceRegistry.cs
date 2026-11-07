using System.Text.Json;

namespace Kinshout.ExternalImporter.Configuration;

public sealed class SourceRegistryEntry
{
    public string Name { get; set; } = "";
    public string Priority { get; set; } = "P0";
    public string Moderation { get; set; } = "Publish";
    public string CommunitySlug { get; set; } = "";
    public bool Import { get; set; }
    public bool WebSub { get; set; }
    public bool Backfill { get; set; }
    /// <summary>Recent uploads are kept only when they are already a hit. The channel is not subscribed.</summary>
    public bool HitsOnly { get; set; }
    public string? YoutubeChannelId { get; set; }
    public string? YoutubeHandle { get; set; }
    public string? Tiktok { get; set; }
    public string? Facebook { get; set; }
    public string? X { get; set; }
    public string? SearchQuery { get; set; }
    public List<string> Mentions { get; set; } = [];
    public string? Notes { get; set; }
}

public static class SourceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static IReadOnlyList<SourceRegistryEntry>? remoteSources;

    /// <summary>Ops toggles and desk-added sources replace the file for this process.</summary>
    public static void UseRemote(IReadOnlyList<SourceRegistryEntry> sources)
    {
        if (sources.Count > 0)
            remoteSources = sources;
    }

    public static IReadOnlyList<SourceRegistryEntry> Load() => remoteSources ?? LoadFromDisk();

    private static IReadOnlyList<SourceRegistryEntry> LoadFromDisk()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "source-registry.json");
        if (!File.Exists(path))
            return [];

        var document = JsonSerializer.Deserialize<SourceRegistryFile>(File.ReadAllText(path), JsonOptions);
        return document?.Sources ?? [];
    }

    public static SourceRegistryEntry? MatchTikTok(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
            return null;

        var normalized = handle.Trim().TrimStart('@');
        return Load().FirstOrDefault(source =>
            string.Equals(
                source.Tiktok?.Trim().TrimStart('@'),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    public static SourceRegistryEntry? MatchAuthor(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
            return null;

        var normalized = author.Trim();
        return Load()
            .Where(source => source.Name.Length >= 4)
            .FirstOrDefault(source =>
                normalized.Contains(source.Name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SourceRegistryFile
    {
        public List<SourceRegistryEntry> Sources { get; set; } = [];
    }
}
