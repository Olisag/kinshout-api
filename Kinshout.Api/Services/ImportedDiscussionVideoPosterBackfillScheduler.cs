using System.Text.Json;
using System.Text.RegularExpressions;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IImportedDiscussionVideoPosterBackfillScheduler
{
    void ScheduleBackfill();
}

/// <summary>
/// Restores still posters for older X imports that stored only the MP4.
/// Uses FxTwitter's public status API (thumbnail_url) — no Apify cost.
/// </summary>
public sealed partial class ImportedDiscussionVideoPosterBackfillScheduler(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<ImportedDiscussionVideoPosterBackfillScheduler> logger)
    : IImportedDiscussionVideoPosterBackfillScheduler
{
    public const int RuleVersion = 1;
    private const int BatchSize = 12;
    private int _running;

    public void ScheduleBackfill()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        var healed = 0;
        var reviewed = 0;

        try
        {
            var http = httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "KinoiseriePosterBackfill/1.0 (+https://kinoiserie.com)");

            while (true)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<KinshoutDbContext>();

                var batch = await db.Discussions
                    .Where(d =>
                        d.PublicFeedVideoPosterRuleVersion < RuleVersion
                        && d.SourceProvider == DiscussionSourceProvider.Twitter
                        && d.VideoUrlsJson != null
                        && d.VideoUrlsJson != ""
                        && d.VideoUrlsJson != "[]"
                        && d.VideoUrlsJson.Contains("video.twimg.com"))
                    .OrderByDescending(d => d.CreatedAt)
                    .Take(BatchSize)
                    .ToListAsync();

                if (batch.Count == 0)
                    break;

                foreach (var discussion in batch)
                {
                    reviewed++;
                    try
                    {
                        if (await TryHealAsync(discussion, http, CancellationToken.None))
                            healed++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(
                            ex,
                            "Video poster heal skipped for discussion {DiscussionId}.",
                            discussion.Id);
                    }

                    discussion.PublicFeedVideoPosterRuleVersion = RuleVersion;
                }

                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                await Task.Delay(250);
            }

            if (reviewed > 0)
            {
                logger.LogInformation(
                    "Imported X video poster backfill reviewed {Reviewed} posts; healed {Healed}.",
                    reviewed,
                    healed);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Imported X video poster backfill failed after reviewing {Reviewed} posts.",
                reviewed);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static async Task<bool> TryHealAsync(
        Discussion discussion,
        HttpClient http,
        CancellationToken ct)
    {
        var images = DiscussionMediaHelper.ParseUrlList(discussion.ImageUrlsJson);
        if (images.Any(DiscussionMultimedia.LooksLikeStillImageUrl))
            return false;

        var videos = DiscussionMediaHelper.ParseUrlList(discussion.VideoUrlsJson);
        if (!videos.Any(v => v.Contains("video.twimg.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        var poster = await FetchTwitterPosterAsync(discussion.SourceExternalUrl, http, ct);
        if (string.IsNullOrWhiteSpace(poster) || !DiscussionMultimedia.LooksLikeStillImageUrl(poster))
            return false;

        images.Add(poster);
        discussion.ImageUrlsJson = DiscussionMediaHelper.SerializeUrlList(images);
        discussion.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    private static async Task<string?> FetchTwitterPosterAsync(
        string? externalUrl,
        HttpClient http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalUrl)
            || !TryParseTwitterStatusPath(externalUrl, out var path))
        {
            return null;
        }

        using var response = await http.GetAsync(
            $"https://api.fxtwitter.com/{path}",
            ct);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("tweet", out var tweet)
            || !tweet.TryGetProperty("media", out var media)
            || !media.TryGetProperty("all", out var all)
            || all.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in all.EnumerateArray())
        {
            if (item.TryGetProperty("thumbnail_url", out var thumb)
                && thumb.ValueKind == JsonValueKind.String)
            {
                var url = thumb.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }
        }

        return null;
    }

    /// <summary>
    /// Accepts x.com / twitter.com status URLs → <c>user/status/id</c> for FxTwitter.
    /// </summary>
    public static bool TryParseTwitterStatusPath(string url, out string path)
    {
        path = "";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        if (!host.Equals("x.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("mobile.twitter.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = StatusPathRegex().Match(uri.AbsolutePath);
        if (!match.Success)
            return false;

        path = $"{match.Groups[1].Value}/status/{match.Groups[2].Value}";
        return true;
    }

    [GeneratedRegex(@"^/([^/]+)/status/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StatusPathRegex();
}
