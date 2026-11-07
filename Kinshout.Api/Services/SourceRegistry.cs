using System.Text.Json;

namespace Kinshout.Api.Services;

public sealed class SourceRegistryEntry
{
    public string Name { get; set; } = "";
    public string Priority { get; set; } = "P0";
    public string Moderation { get; set; } = "Publish";
    public string CommunitySlug { get; set; } = "";
    public bool Import { get; set; }
    public bool WebSub { get; set; }
    public bool Backfill { get; set; }
    public string? YoutubeChannelId { get; set; }
    public string? YoutubeHandle { get; set; }
    public string? Tiktok { get; set; }
    public string? Facebook { get; set; }
    public string? X { get; set; }
    public string? Notes { get; set; }
}

public static class SourceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<SourceRegistryEntry> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "source-registry.json");
        if (!File.Exists(path))
            return [];

        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<SourceRegistryFile>(json, JsonOptions);
        return document?.Sources ?? [];
    }

    public static SourceRegistryEntry? FindByYouTubeChannel(string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            return null;

        return Load().FirstOrDefault(source =>
            string.Equals(source.YoutubeChannelId, channelId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SourceRegistryFile
    {
        public List<SourceRegistryEntry> Sources { get; set; } = [];
    }
}
