using System.Net;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IUnavailableSourceService
{
    /// <summary>True when the source post is gone and the discussion was deleted.</summary>
    Task<bool> DropIfUnavailableAsync(Guid discussionId, CancellationToken ct = default);

    Task<UnavailableSourceSweepResult> SweepAsync(int limit, CancellationToken ct = default);
}

public readonly record struct UnavailableSourceSweepResult(int Checked, int Removed);

public sealed class UnavailableSourceService(
    KinshoutDbContext db,
    IHttpClientFactory httpClientFactory) : IUnavailableSourceService
{
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromHours(6);
    private const int MaxBatch = 80;

    public async Task<bool> DropIfUnavailableAsync(Guid discussionId, CancellationToken ct = default)
    {
        var discussion = await db.Discussions.FirstOrDefaultAsync(d => d.Id == discussionId, ct);
        if (discussion is null || !CanCheck(discussion))
            return false;

        if (discussion.SourceLastSeenAt is DateTime seen && DateTime.UtcNow - seen < RecheckAfter)
            return false;

        var gone = await IsGoneAsync(discussion.SourceExternalUrl!, ct);
        if (gone is null)
            return false;

        if (!gone.Value)
        {
            discussion.SourceLastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return false;
        }

        await DeleteAsync(discussion, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<UnavailableSourceSweepResult> SweepAsync(int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxBatch);
        var cutoff = DateTime.UtcNow - RecheckAfter;
        var candidates = await db.Discussions
            .Where(d => d.SourceExternalUrl != null
                && (d.SourceProvider == DiscussionSourceProvider.Facebook
                    || d.SourceProvider == DiscussionSourceProvider.YouTube)
                && (d.SourceLastSeenAt == null || d.SourceLastSeenAt < cutoff))
            .OrderBy(d => d.SourceLastSeenAt.HasValue)
            .ThenBy(d => d.SourceLastSeenAt)
            .Take(take)
            .ToListAsync(ct);

        var removed = 0;
        foreach (var discussion in candidates)
        {
            var gone = await IsGoneAsync(discussion.SourceExternalUrl!, ct);
            if (gone is null)
                continue;

            if (!gone.Value)
            {
                discussion.SourceLastSeenAt = DateTime.UtcNow;
                continue;
            }

            await DeleteAsync(discussion, ct);
            removed++;
        }

        await db.SaveChangesAsync(ct);
        return new UnavailableSourceSweepResult(candidates.Count, removed);
    }

    private static bool CanCheck(Discussion discussion) =>
        !string.IsNullOrWhiteSpace(discussion.SourceExternalUrl)
        && (string.Equals(discussion.SourceProvider, DiscussionSourceProvider.Facebook, StringComparison.OrdinalIgnoreCase)
            || string.Equals(discussion.SourceProvider, DiscussionSourceProvider.YouTube, StringComparison.OrdinalIgnoreCase));

    private async Task<bool?> IsGoneAsync(string url, CancellationToken ct)
    {
        if (SourceUnavailable.IsFacebookPostUrl(url))
            return await FacebookGoneAsync(url, ct);

        if (SourceUnavailable.TryYouTubeWatchUrl(url, out var watchUrl))
            return await YouTubeGoneAsync(watchUrl, ct);

        return false;
    }

    private async Task<bool?> FacebookGoneAsync(string postUrl, CancellationToken ct)
    {
        var plugin = "https://www.facebook.com/plugins/post.php?href="
            + Uri.EscapeDataString(postUrl)
            + "&show_text=false&width=500";
        var (status, body) = await GetAsync(plugin, ct);
        if (status == HttpStatusCode.NotFound)
            return true;
        if (status != HttpStatusCode.OK || body is null)
            return null;

        return SourceUnavailable.FacebookPluginSaysGone(body);
    }

    private async Task<bool?> YouTubeGoneAsync(string watchUrl, CancellationToken ct)
    {
        var oembed = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(watchUrl);
        var (status, _) = await GetAsync(oembed, ct);
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            return true;
        if (status == HttpStatusCode.OK)
            return false;

        return null;
    }

    private async Task<(HttpStatusCode Status, string? Body)> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("SourceAvailability");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en,fr;q=0.8");
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK)
                return (response.StatusCode, null);

            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length > 500_000)
                body = body[..500_000];
            return (response.StatusCode, body);
        }
        catch (HttpRequestException)
        {
            return (0, null);
        }
        catch (TaskCanceledException)
        {
            return (0, null);
        }
    }

    private async Task DeleteAsync(Discussion discussion, CancellationToken ct)
    {
        var pins = await db.EditorialPins
            .Where(pin => pin.DiscussionId == discussion.Id)
            .ToListAsync(ct);
        foreach (var pin in pins)
            pin.DiscussionId = null;

        db.Discussions.Remove(discussion);
    }
}
