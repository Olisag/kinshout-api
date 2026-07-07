using System.Text.Json.Serialization;

namespace Kinshout.ExternalImporter.Providers;

public sealed class SourceFeedAdvert
{
    [JsonPropertyName("externalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("externalUrl")]
    public string? ExternalUrl { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("subcategory")]
    public string? Subcategory { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("price")]
    public FeedPrice? Price { get; set; }

    [JsonPropertyName("location")]
    public FeedLocation? Location { get; set; }

    [JsonPropertyName("details")]
    public FeedDetails? Details { get; set; }

    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }

    [JsonPropertyName("contact")]
    public FeedContact? Contact { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("intent")]
    public List<string>? Intent { get; set; }

    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("duplicateGroupId")]
    public string? DuplicateGroupId { get; set; }
}

public sealed class FeedPrice
{
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Formatted { get; set; }
    public string? Period { get; set; }
    public bool Negotiable { get; set; }
}

public sealed class FeedLocation
{
    public string? City { get; set; }
    public string? Commune { get; set; }
    public string? Neighborhood { get; set; }
    public string? Address { get; set; }
    public string? Formatted { get; set; }
}

public sealed class FeedDetails
{
    public int? Bedrooms { get; set; }
    public int? Bathrooms { get; set; }
    public int? Area { get; set; }
    public bool? Furnished { get; set; }
    public int? Floor { get; set; }
    public string? PropertyType { get; set; }
    public string? Condition { get; set; }
    public bool? Parking { get; set; }
    public bool? PetFriendly { get; set; }
    public int? YearBuilt { get; set; }
}

public sealed class FeedContact
{
    public string? SellerName { get; set; }
    public string? SellerProfileUrl { get; set; }
    public string? Phone { get; set; }
    public string? WhatsApp { get; set; }
    public string? PreferredContact { get; set; }
    public bool? IsPubliclyListed { get; set; }
}
