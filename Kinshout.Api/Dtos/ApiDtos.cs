using System.Text.Json.Serialization;

namespace Kinshout.Api.Dtos;

public record AuthResponseDto(
    string Token,
    DateTime ExpiresAt,
    UserProfileDto User
);

public record UserProfileDto(
    Guid Id,
    string Email,
    string DisplayName,
    string? AvatarUrl,
    string? WhatsAppNumber,
    bool HasWhatsApp,
    string DisplayPreference,
    bool IsProfilePublic,
    string MemberSince
);

public record UpdateProfileRequestDto(string WhatsAppNumber);

public record UpdateDisplayNameRequestDto(string DisplayName);

public record PublicUserProfileDto(
    Guid Id,
    string DisplayName,
    string? AvatarUrl,
    string MemberSince,
    int PublishedAdvertCount
);

public record ProfileVisibilityDto(bool IsPublic);

public record UpdateProfileVisibilityRequestDto(bool IsPublic);

public record DisplayPreferenceDto(string Mode);

public record UpdateDisplayPreferenceRequestDto(string Mode);

/// <summary>Google or Apple ID token (RS256 JWT from the provider). Not the Kinshout client or user JWT.</summary>
/// <param name="IdToken">Provider-issued ID token.</param>
public record ExternalLoginRequestDto(string IdToken);

public record FacebookLoginRequestDto(string AccessToken);

public record ClientAuthRequestDto(string ClientId, string? ClientSecret);

public record ClientAuthResponseDto(
    string ClientToken,
    DateTime ExpiresAt,
    string ClientId,
    string ClientName
);

public record CreateAdvertRequestDto(
    string Text,
    string? Price,
    string? Location,
    IReadOnlyList<string>? ImageUrls,
    string? ResumeUrl,
    string? Intent
);

public record UpdateAdvertRequestDto(
    string Text,
    string? Price,
    string? Location,
    IReadOnlyList<string>? ImageUrls,
    string? ResumeUrl,
    string? Intent
);

public record AdvertDto(
    Guid Id,
    string Title,
    string Description,
    string? Price,
    string? Location,
    string Intent,
    string CategoryId,
    string CategoryLabel,
    string CategoryIcon,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<string> ThumbnailUrls,
    IReadOnlyList<string> DisplayImageUrls,
    string? ResumeUrl,
    string? WhatsAppNumber,
    IReadOnlyList<string> Tags,
    string Time,
    double AiConfidence,
    string? AiSummary,
    int ViewCount,
    int LikeCount,
    bool IsSaved = false,
    bool IsExternal = false,
    AdvertSourceDto? Source = null,
    AdvertDetailsDto? Details = null,
    AdvertContactDto? Contact = null);

/// <summary>External listing provenance — present when <see cref="AdvertDto.IsExternal"/> is true.</summary>
/// <param name="Provider">Source slug: <c>facebook_marketplace</c>, <c>mediacongo</c>, <c>zwandako</c>, <c>jiji_rdc</c>, or <c>other</c>.</param>
/// <param name="ProviderName">Human-readable source label shown in the app.</param>
/// <param name="ExternalId">Stable id from the source site (dedupe key with <see cref="Provider"/>).</param>
/// <param name="ExternalUrl">Canonical URL on the source marketplace.</param>
/// <param name="ImportedAt">When Kinshout first imported this listing.</param>
/// <param name="LastSeenAt">When the importer last saw this listing active.</param>
/// <param name="FirstSeenAt">First observation timestamp from the source feed.</param>
public record AdvertSourceDto(
    string Provider,
    string ProviderName,
    string ExternalId,
    string ExternalUrl,
    DateTime ImportedAt,
    DateTime LastSeenAt,
    DateTime FirstSeenAt);

public record AdvertDetailsDto(
    int? Bedrooms,
    int? Bathrooms,
    int? Area,
    bool? Furnished,
    int? Floor,
    string? PropertyType,
    string? Condition,
    bool? Parking,
    bool? PetFriendly,
    int? YearBuilt);

public record AdvertContactDto(
    string? SellerName,
    string? SellerProfileUrl,
    string? Phone,
    string? WhatsApp,
    string? PreferredContact,
    bool IsPubliclyListed);

/// <summary>Source identity for an import payload item.</summary>
/// <param name="Provider"><c>facebook_marketplace</c>, <c>mediacongo</c>, <c>zwandako</c>, <c>jiji_rdc</c>, or <c>other</c>.</param>
/// <param name="ProviderName">Optional display name; defaults from <see cref="Provider"/> when omitted.</param>
/// <param name="ExternalId">Required. Unique id on the source site.</param>
/// <param name="ExternalUrl">Required. Public listing URL.</param>
/// <param name="ImportedAt">When this batch first imported the listing (defaults to now).</param>
/// <param name="LastSeenAt">Last time the source was seen active (defaults to now).</param>
/// <param name="FirstSeenAt">Optional first-seen timestamp from the source feed.</param>
public record ImportExternalAdvertSourceDto(
    string Provider,
    string? ProviderName,
    string ExternalId,
    string ExternalUrl,
    DateTime? ImportedAt,
    DateTime? LastSeenAt,
    DateTime? FirstSeenAt);

public record ImportExternalAdvertPriceDto(
    decimal? Amount,
    string? Currency,
    string? Formatted,
    string? Period,
    bool Negotiable);

public record ImportExternalAdvertLocationDto(
    string? City,
    string? Commune,
    string? Neighborhood,
    string? Address,
    string? Formatted);

public record ImportExternalAdvertDetailsDto(
    int? Bedrooms,
    int? Bathrooms,
    int? Area,
    bool? Furnished,
    int? Floor,
    string? PropertyType,
    string? Condition,
    bool? Parking,
    bool? PetFriendly,
    int? YearBuilt);

public record ImportExternalAdvertContactDto(
    string? SellerName,
    string? SellerProfileUrl,
    string? Phone,
    string? WhatsApp,
    string? PreferredContact,
    bool? IsPubliclyListed);

public record ImportExternalAdvertAiDto(
    IReadOnlyList<string>? Tags,
    string? Summary,
    IReadOnlyList<string>? Intent);

public record ImportExternalAdvertDto(
    ImportExternalAdvertSourceDto Source,
    string Category,
    string? Subcategory,
    string Title,
    ImportExternalAdvertPriceDto? Price,
    ImportExternalAdvertLocationDto? Location,
    ImportExternalAdvertDetailsDto? Details,
    string Description,
    IReadOnlyList<string>? Images,
    ImportExternalAdvertContactDto? Contact,
    string Status = "active",
    DateTime? PublishedAt = null,
    string? Modality = null,
    ImportExternalAdvertAiDto? Ai = null,
    string? DuplicateGroupId = null);

public record ImportExternalAdvertsRequestDto(IReadOnlyList<ImportExternalAdvertDto> Adverts);

public record ImportExternalAdvertsResponseDto(
    int Created,
    int Updated,
    int Unchanged,
    int Skipped);

public record ImportKnownAdvertKeyDto(string Provider, string ExternalId);

public record ImportKnownAdvertsResponseDto(IReadOnlyList<ImportKnownAdvertKeyDto> Adverts);

public record ImportExternalDiscussionSourceDto(
    string Provider,
    string? ProviderName,
    string ExternalId,
    string ExternalUrl,
    DateTime? ImportedAt,
    DateTime? LastSeenAt,
    DateTime? FirstSeenAt);

public record ImportExternalDiscussionDto(
    ImportExternalDiscussionSourceDto Source,
    string Title,
    string Body,
    string? OriginalAuthor = null,
    int? EngagementScore = null,
    string Status = "active",
    DateTime? PublishedAt = null);

public record ImportExternalDiscussionsRequestDto(IReadOnlyList<ImportExternalDiscussionDto> Discussions);

public record ImportExternalDiscussionsResponseDto(
    int Created,
    int Updated,
    int Unchanged,
    int Skipped);

public record ImportKnownDiscussionKeyDto(string Provider, string ExternalId);

public record ImportKnownDiscussionsResponseDto(IReadOnlyList<ImportKnownDiscussionKeyDto> Discussions);

public record DiscussionImportStateDto(string Provider, DateTime LastRunAtUtc);

public record DiscussionImportStateResponseDto(IReadOnlyList<DiscussionImportStateDto> Providers);

public record RecordDiscussionImportRunRequestDto(string Provider, DateTime? RunAt = null);

public record RetransformExternalDiscussionsResponseDto(
    int Transformed,
    int Unchanged,
    int Skipped,
    int Failed,
    int Remaining);

public record CategoryDto(
    Guid Id,
    string Slug,
    string Label,
    string Icon,
    bool IsAiGenerated
);

public record DiscussionDto(
    Guid Id,
    string Title,
    string Body,
    string Author,
    string Avatar,
    int ReplyCount,
    string Time,
    string? CategorySlug,
    int LikeCount,
    int ViewCount,
    [property: JsonPropertyName("isLiked")] bool IsLiked = false,
    bool IsExternal = false,
    DiscussionSourceDto? Source = null);

/// <summary>External discussion provenance — present when <see cref="DiscussionDto.IsExternal"/> is true.</summary>
public record DiscussionSourceDto(
    string Provider,
    string ProviderName,
    string ExternalId,
    string ExternalUrl,
    DateTime ImportedAt,
    DateTime LastSeenAt,
    DateTime FirstSeenAt,
    string? OriginalAuthor = null,
    int? EngagementScore = null,
    DateTime? PublishedAt = null);

public record DiscussionDetailDto(
    Guid Id,
    string Title,
    string Body,
    Guid AuthorId,
    string Author,
    string Avatar,
    string Time,
    int LikeCount,
    int ViewCount,
    int ReplyCount,
    [property: JsonPropertyName("isLiked")] bool IsLiked,
    PagedResultDto<DiscussionReplyDto> Thread,
    bool IsExternal = false,
    DiscussionSourceDto? Source = null);

public record DiscussionReplyDto(
    Guid Id,
    Guid AuthorId,
    string Author,
    string Avatar,
    string Time,
    string Text
);

public record CreateDiscussionRequestDto(string Title, string Body);

public record UpdateDiscussionRequestDto(string Title, string Body);

public record CreateReplyRequestDto(string Body);

public record UpdateReplyRequestDto(string Body);

/// <summary>Search request body for POST /api/search.</summary>
/// <param name="Query">Free-text search. Optional for browse-style queries with filters only.</param>
/// <param name="Tab"><c>all</c>, <c>annonces</c>, or <c>discussions</c>.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Results per type per page (max 50).</param>
/// <param name="Sort"><c>recent</c> or <c>popular</c>.</param>
/// <param name="Intent">Optional: <c>demande</c>, <c>offre</c>, or <c>discussion</c>.</param>
/// <param name="Source">
/// Optional advert source filter: omit/<c>all</c>/<c>toutes</c>, <c>kinshout</c>, <c>external</c>,
/// or provider slug (<c>facebook_marketplace</c>, <c>mediacongo</c>, <c>zwandako</c>, <c>jiji_rdc</c>, <c>other</c>).
/// </param>
public record SearchRequestDto(
    string Query = "",
    string Tab = "all",
    int Page = 1,
    int PageSize = 20,
    string Sort = "recent",
    string? Intent = null,
    string? Source = null,
    Guid? CategoryId = null,
    Guid? TopicId = null);

public record PopularSearchDto(string Query, int Count);

public record SearchPaginationDto(
    int Page,
    int PageSize,
    int TotalAdverts,
    int TotalDiscussions,
    bool HasMoreAdverts,
    bool HasMoreDiscussions,
    int? TotalItems = null,
    bool? HasMore = null);

public record SearchFeedItemDto(
    string Kind,
    AdvertDto? Advert,
    DiscussionDto? Discussion);

public record SearchResultDto(
    IReadOnlyList<AdvertDto> Adverts,
    IReadOnlyList<DiscussionDto> Discussions,
    string? AiSummary,
    SearchPaginationDto Pagination,
    IReadOnlyList<SearchFeedItemDto>? Items = null);

public record CategorizeRequestDto(string Text);

public record CategorizeResponseDto(
    string CategoryId,
    string CategoryLabel,
    string CategoryIcon,
    string Intent,
    string IntentLabel,
    double Confidence,
    string Summary,
    string Source,
    bool CategoryCreated
);

public record UploadResponseDto(IReadOnlyList<string> Urls);

public record PagedResultDto<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasMore);

