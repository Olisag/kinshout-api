namespace Kinshout.Api.Services;

/// <summary>Thrown when OpenAI moderation blocks an advert or upload.</summary>
public class AdvertModerationException(string message) : Exception(message);

public record AdvertModerationCheckResult(
    bool Allowed,
    string? Reason,
    IReadOnlyList<string> Violations);

public interface IAdvertModerationService
{
    Task EnsureTextAllowedAsync(string text, CancellationToken ct = default);
    /// <summary>Text checks for discussion posts and replies; gated by DiscussionModerationEnabled.</summary>
    Task EnsureDiscussionTextAllowedAsync(string text, CancellationToken ct = default);
    Task EnsureImageAllowedAsync(Stream imageStream, string contentType, CancellationToken ct = default);
    Task EnsureDocumentAllowedAsync(Stream fileStream, string contentType, string fileName, CancellationToken ct = default);
}
