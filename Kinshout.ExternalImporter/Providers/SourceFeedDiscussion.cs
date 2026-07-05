using System.Text.Json.Serialization;

namespace Kinshout.ExternalImporter.Providers;

public sealed class SourceFeedDiscussion
{
    [JsonPropertyName("externalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("externalUrl")]
    public string? ExternalUrl { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("originalAuthor")]
    public string? OriginalAuthor { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("engagementScore")]
    public int? EngagementScore { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}
