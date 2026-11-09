using System.Text.Json;

namespace Kinshout.ExternalImporter.Providers;

/// <summary>Uses only reaction text actually included in the provider payload.</summary>
public static class SourceReactionReader
{
    public static List<string>? Read(JsonElement post)
    {
        foreach (var name in new[] { "sourceReactions", "topComments", "comments", "replies" })
        {
            if (!post.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) continue;
            var result = new List<string>();
            foreach (var item in array.EnumerateArray().Take(20))
            {
                string? text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (item.ValueKind == JsonValueKind.Object)
                    foreach (var key in new[] { "text", "comment", "body" })
                        if (item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                        { text = value.GetString(); break; }
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim()[..Math.Min(text.Trim().Length, 1000)]);
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return null;
    }
}
