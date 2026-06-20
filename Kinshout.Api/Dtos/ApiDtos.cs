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
    string MemberSince
);

public record UpdateProfileRequestDto(string WhatsAppNumber);

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
    string? AiSummary
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
    string? CategorySlug
);

public record DiscussionDetailDto(
    Guid Id,
    string Title,
    string Body,
    string Author,
    string Avatar,
    string Time,
    IReadOnlyList<DiscussionReplyDto> Thread
);

public record DiscussionReplyDto(
    Guid Id,
    string Author,
    string Avatar,
    string Time,
    string Text
);

public record CreateDiscussionRequestDto(string Title, string Body);

public record CreateReplyRequestDto(string Body);

public record SearchRequestDto(string Query, string Tab = "all");

public record SearchResultDto(
    IReadOnlyList<AdvertDto> Adverts,
    IReadOnlyList<DiscussionDto> Discussions,
    string? AiSummary
);

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

