using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

/// <summary>
/// Saves a TikTok file we downloaded and attaches it to the discussion.
/// The arrival time stays where it is.
/// </summary>
public sealed class ImportedVideoStore(KinshoutDbContext db, IUploadStorage storage)
{
    public const long MaxBytes = 28 * 1024 * 1024;

    public async Task<string> SaveAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Seule une vidéo mp4 peut être archivée.");

        var user = await ImportSeed.EnsureImportUserAsync(db, ct);
        var url = await storage.SaveAsync("videos", user.Id, stream, ".mp4", ct);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("L'archive vidéo n'a pas renvoyé d'adresse.");

        return url;
    }

    public async Task<IReadOnlyList<TikTokFileCandidateDto>> TikToksMissingFileAsync(
        int limit,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 12);
        return await db.Discussions
            .AsNoTracking()
            .Where(d => d.SourceProvider == DiscussionSourceProvider.TikTok
                && d.SourceExternalId != null
                && d.SourceExternalUrl != null
                && d.SourceExternalUrl.Contains("/video/")
                && (d.VideoUrlsJson == null || !d.VideoUrlsJson.Contains("/uploads/videos/")))
            .OrderByDescending(d => d.SourceImportedAt ?? d.CreatedAt)
            .Take(limit)
            .Select(d => new TikTokFileCandidateDto(d.SourceExternalId!, d.SourceExternalUrl!))
            .ToListAsync(ct);
    }

    public async Task<bool> AttachTikTokFileAsync(string externalId, string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalId) || !IsHostedFile(url))
            throw new ArgumentException("Fichier TikTok invalide.");

        var discussion = await db.Discussions
            .FirstOrDefaultAsync(
                d => d.SourceProvider == DiscussionSourceProvider.TikTok
                    && d.SourceExternalId == externalId.Trim(),
                ct);
        if (discussion is null)
            return false;

        var videos = DiscussionMediaHelper.ParseUrlList(discussion.VideoUrlsJson)
            .Where(video => !IsTikTokWatchPage(video))
            .ToList();
        if (!videos.Contains(url, StringComparer.OrdinalIgnoreCase))
            videos.Insert(0, url.Trim());

        discussion.VideoUrlsJson = DiscussionMediaHelper.SerializeUrlList(
            videos.Take(DiscussionMediaHelper.MaxVideos).ToList());
        discussion.Body = PublicBody(discussion);
        discussion.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    internal static string PublicBody(Discussion discussion)
    {
        if (discussion.SourceProvider != DiscussionSourceProvider.TikTok
            || !DiscussionMediaHelper.ParseUrlList(discussion.VideoUrlsJson).Any(IsHostedFile))
            return discussion.Body;

        return System.Text.RegularExpressions.Regex.Replace(
            discussion.Body,
            @"https?://(?:[a-z0-9-]+\.)*tiktok\.com/[^\s<>""'\x60\]\)]+",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
    }

    internal static bool IsHostedFile(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.Contains("/uploads/videos/", StringComparison.OrdinalIgnoreCase);

    private static bool IsTikTokWatchPage(string url) =>
        url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase)
        && url.Contains("/video/", StringComparison.OrdinalIgnoreCase);
}
