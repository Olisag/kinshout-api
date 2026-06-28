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
    string? ResumeUrl,
    string? WhatsAppNumber,
    IReadOnlyList<string> Tags,
    string Time,
    double AiConfidence,
    string? AiSummary,
    int ViewCount,
    int LikeCount,
    bool IsSaved = false
);

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
    int Replies,
    string Time,
    string? CategorySlug,
    int LikeCount,
    int ViewCount,
    [property: JsonPropertyName("isLiked")] bool IsLiked = false);

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
    [property: JsonPropertyName("isLiked")] bool IsLiked,
    PagedResultDto<DiscussionReplyDto> Thread);

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

public record SearchRequestDto(
    string Query,
    string Tab = "all",
    int Page = 1,
    int PageSize = 20,
    string Sort = "recent",
    string? Intent = null);

public record PopularSearchDto(string Query, int Count);

public record SearchPaginationDto(
    int Page,
    int PageSize,
    int TotalAdverts,
    int TotalDiscussions,
    bool HasMoreAdverts,
    bool HasMoreDiscussions);

public record SearchResultDto(
    IReadOnlyList<AdvertDto> Adverts,
    IReadOnlyList<DiscussionDto> Discussions,
    string? AiSummary,
    SearchPaginationDto Pagination);

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

