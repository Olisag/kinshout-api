using System.Text.Json.Serialization;

namespace Kinshout.ExternalImporter.Import;

public sealed record ImportExternalAdvertsRequestDto(
    [property: JsonPropertyName("adverts")] IReadOnlyList<ImportExternalAdvertDto> Adverts);

public sealed record ImportExternalAdvertsResponseDto(
    [property: JsonPropertyName("created")] int Created,
    [property: JsonPropertyName("updated")] int Updated,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("skipped")] int Skipped);

public sealed record ImportKnownAdvertKeyDto(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("externalId")] string ExternalId);

public sealed record ImportKnownAdvertsResponseDto(
    [property: JsonPropertyName("adverts")] IReadOnlyList<ImportKnownAdvertKeyDto> Adverts);

public sealed record ImportExternalAdvertDto(
    [property: JsonPropertyName("source")] ImportExternalAdvertSourceDto Source,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("subcategory")] string? Subcategory,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("price")] ImportExternalAdvertPriceDto? Price,
    [property: JsonPropertyName("location")] ImportExternalAdvertLocationDto? Location,
    [property: JsonPropertyName("details")] ImportExternalAdvertDetailsDto? Details,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("images")] IReadOnlyList<string>? Images,
    [property: JsonPropertyName("contact")] ImportExternalAdvertContactDto? Contact,
    [property: JsonPropertyName("status")] string Status = "active",
    [property: JsonPropertyName("publishedAt")] DateTime? PublishedAt = null,
    [property: JsonPropertyName("modality")] string? Modality = null,
    [property: JsonPropertyName("ai")] ImportExternalAdvertAiDto? Ai = null,
    [property: JsonPropertyName("duplicateGroupId")] string? DuplicateGroupId = null);

public sealed record ImportExternalAdvertSourceDto(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("providerName")] string? ProviderName,
    [property: JsonPropertyName("externalId")] string ExternalId,
    [property: JsonPropertyName("externalUrl")] string ExternalUrl,
    [property: JsonPropertyName("importedAt")] DateTime? ImportedAt,
    [property: JsonPropertyName("lastSeenAt")] DateTime? LastSeenAt,
    [property: JsonPropertyName("firstSeenAt")] DateTime? FirstSeenAt);

public sealed record ImportExternalAdvertPriceDto(
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("formatted")] string? Formatted,
    [property: JsonPropertyName("period")] string? Period,
    [property: JsonPropertyName("negotiable")] bool Negotiable);

public sealed record ImportExternalAdvertLocationDto(
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("commune")] string? Commune,
    [property: JsonPropertyName("neighborhood")] string? Neighborhood,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("formatted")] string? Formatted);

public sealed record ImportExternalAdvertDetailsDto(
    [property: JsonPropertyName("bedrooms")] int? Bedrooms,
    [property: JsonPropertyName("bathrooms")] int? Bathrooms,
    [property: JsonPropertyName("area")] int? Area,
    [property: JsonPropertyName("furnished")] bool? Furnished,
    [property: JsonPropertyName("floor")] int? Floor,
    [property: JsonPropertyName("propertyType")] string? PropertyType,
    [property: JsonPropertyName("condition")] string? Condition,
    [property: JsonPropertyName("parking")] bool? Parking,
    [property: JsonPropertyName("petFriendly")] bool? PetFriendly,
    [property: JsonPropertyName("yearBuilt")] int? YearBuilt);

public sealed record ImportExternalAdvertContactDto(
    [property: JsonPropertyName("sellerName")] string? SellerName,
    [property: JsonPropertyName("sellerProfileUrl")] string? SellerProfileUrl,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("whatsapp")] string? WhatsApp,
    [property: JsonPropertyName("preferredContact")] string? PreferredContact,
    [property: JsonPropertyName("isPubliclyListed")] bool? IsPubliclyListed);

public sealed record ImportExternalAdvertAiDto(
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("intent")] IReadOnlyList<string>? Intent);
