using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

/// <summary>
/// Vision-checks hotlinked images on imported discussions. Marks
/// <see cref="Discussion.PublicFeedBlocked"/> when politics / hate / adult
/// content is in the screenshot rather than the caption.
/// </summary>
public static class ImportedDiscussionImageModeration
{
    /// <summary>Bump to re-scan existing image posts on the next backfill.</summary>
    public const int RuleVersion = 2;

    public const int MaxImagesToModerate = 2;
    public const int MaxImageBytes = 5_000_000;

    /// <summary>
    /// Download and vision-check images when needed. Only stamps
    /// <see cref="Discussion.PublicFeedMediaRuleVersion"/> after a conclusive
    /// review (or when there is nothing to review), so failed downloads retry.
    /// </summary>
    public static async Task ApplyAsync(
        Discussion discussion,
        IHttpClientFactory httpClientFactory,
        IAdvertModerationService moderation,
        ILogger logger,
        CancellationToken ct)
    {
        if (discussion.PublicFeedBlocked)
        {
            discussion.PublicFeedMediaRuleVersion = RuleVersion;
            return;
        }

        var images = DiscussionMediaHelper.ParseUrlList(discussion.ImageUrlsJson);
        if (images.Count == 0)
        {
            discussion.PublicFeedMediaRuleVersion = RuleVersion;
            return;
        }

        var client = httpClientFactory.CreateClient();
        var reviewedAny = false;

        foreach (var url in images.Take(MaxImagesToModerate))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (compatible; KinoiserieImport/1.0; +https://kinoiserie.com)");
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "Imported image download failed ({Status}) for discussion {DiscussionId}: {Url}",
                        (int)response.StatusCode,
                        discussion.Id,
                        url);
                    continue;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && !LooksLikeImageUrl(url))
                {
                    continue;
                }

                await using var network = await response.Content.ReadAsStreamAsync(ct);
                await using var buffer = new MemoryStream();
                var copyTask = network.CopyToAsync(buffer, ct);
                var completed = await Task.WhenAny(copyTask, Task.Delay(TimeSpan.FromSeconds(20), ct));
                if (completed != copyTask)
                {
                    logger.LogWarning(
                        "Imported image download timed out for discussion {DiscussionId}: {Url}",
                        discussion.Id,
                        url);
                    continue;
                }

                await copyTask;

                if (buffer.Length == 0 || buffer.Length > MaxImageBytes)
                    continue;

                buffer.Position = 0;
                var result = await moderation.CheckImageAllowedAsync(
                    buffer,
                    contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                        ? contentType
                        : GuessImageContentType(url),
                    ct);

                if (result.Skipped)
                {
                    logger.LogWarning(
                        "Image moderation skipped for discussion {DiscussionId}; will retry later.",
                        discussion.Id);
                    return;
                }

                reviewedAny = true;
                if (result.Allowed)
                    continue;

                discussion.PublicFeedBlocked = true;
                discussion.PublicFeedRuleVersion = PoliticsFeedFilter.RuleVersion;
                discussion.PublicFeedMediaRuleVersion = RuleVersion;
                logger.LogInformation(
                    "Discussion {DiscussionId} ({ExternalId}) hidden after image moderation: {Reason}",
                    discussion.Id,
                    discussion.SourceExternalId,
                    result.Reason);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Could not moderate imported image {Url} for discussion {DiscussionId}; will retry later.",
                    url,
                    discussion.Id);
            }
        }

        if (reviewedAny)
            discussion.PublicFeedMediaRuleVersion = RuleVersion;
    }

    private static bool LooksLikeImageUrl(string url) =>
        url.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
        || url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase)
        || url.Contains(".png", StringComparison.OrdinalIgnoreCase)
        || url.Contains(".webp", StringComparison.OrdinalIgnoreCase)
        || url.Contains("pbs.twimg.com/media/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("format=jpg", StringComparison.OrdinalIgnoreCase)
        || url.Contains("format=png", StringComparison.OrdinalIgnoreCase);

    private static string GuessImageContentType(string url)
    {
        if (url.Contains(".png", StringComparison.OrdinalIgnoreCase)
            || url.Contains("format=png", StringComparison.OrdinalIgnoreCase))
        {
            return "image/png";
        }

        if (url.Contains(".webp", StringComparison.OrdinalIgnoreCase)
            || url.Contains("format=webp", StringComparison.OrdinalIgnoreCase))
        {
            return "image/webp";
        }

        return "image/jpeg";
    }
}
