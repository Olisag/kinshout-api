using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Kinshout.Api.Services;

/// <summary>
/// Saves a TikTok file we downloaded and attaches it to the discussion.
/// The arrival time stays where it is.
/// </summary>
public sealed class ImportedVideoStore(KinshoutDbContext db, IUploadStorage storage)
{
    public const long MaxBytes = 28 * 1024 * 1024;
    public const long MaxPosterBytes = 2 * 1024 * 1024;
    internal const string CoverMarker = "_cover";
    private const int PosterMaxSide = 720;

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

    /// <summary>
    /// TikToks that already have a file, but still show the grey placeholder instead of the cover.
    /// </summary>
    public async Task<IReadOnlyList<TikTokPosterCandidateDto>> TikToksNeedingPosterAsync(
        int limit,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 8);
        var rows = await db.Discussions
            .AsNoTracking()
            .Where(d => d.SourceProvider == DiscussionSourceProvider.TikTok
                && d.SourceExternalId != null
                && d.SourceExternalUrl != null
                && d.VideoUrlsJson != null
                && d.VideoUrlsJson.Contains("/uploads/videos/"))
            .OrderByDescending(d => d.SourceImportedAt ?? d.CreatedAt)
            .Take(48)
            .Select(d => new { d.SourceExternalId, d.SourceExternalUrl, d.VideoUrlsJson })
            .ToListAsync(ct);

        var hosted = new List<(string ExternalId, string ExternalUrl, string VideoUrl)>();
        foreach (var row in rows)
        {
            var video = DiscussionMediaHelper.ParseUrlList(row.VideoUrlsJson).FirstOrDefault(IsHostedFile);
            if (video is null)
                continue;

            hosted.Add((row.SourceExternalId!, row.SourceExternalUrl!, video));
        }

        if (hosted.Count == 0)
            return [];

        var urls = hosted.Select(item => item.VideoUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var posters = await db.VideoAssets
            .AsNoTracking()
            .Where(asset => asset.DeletedAt == null && urls.Contains(asset.VideoUrl))
            .Select(asset => new { asset.VideoUrl, asset.PosterUrl })
            .ToListAsync(ct);
        var posterByUrl = posters
            .GroupBy(item => item.VideoUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().PosterUrl, StringComparer.OrdinalIgnoreCase);

        return hosted
            .Where(item => !HasCoverPoster(posterByUrl.GetValueOrDefault(item.VideoUrl)))
            .Take(limit)
            .Select(item => new TikTokPosterCandidateDto(item.ExternalId, item.ExternalUrl, item.VideoUrl))
            .ToList();
    }

    /// <summary>Stores the creator's cover as the still for a file we already have.</summary>
    public async Task<bool> AttachPosterAsync(string videoUrl, Stream image, CancellationToken ct = default)
    {
        if (!IsHostedFile(videoUrl))
            throw new ArgumentException("Fichier TikTok invalide.");

        await using var normalized = await NormalizeCoverAsync(image, ct);
        if (normalized is null)
            return false;

        var user = await ImportSeed.EnsureImportUserAsync(db, ct);
        var storedUrl = videoUrl.Trim();
        var asset = await db.VideoAssets
            .FirstOrDefaultAsync(item => item.DeletedAt == null && item.VideoUrl == storedUrl, ct);
        if (asset is not null && HasCoverPoster(asset.PosterUrl))
            return true;

        if (asset is null)
        {
            asset = new VideoAsset
            {
                UserId = user.Id,
                VideoUrl = storedUrl,
                ContentType = "video/mp4",
                ByteSize = 0,
                OriginalFileName = Path.GetFileName(storedUrl),
            };
            db.VideoAssets.Add(asset);
        }

        var fileId = Path.GetFileNameWithoutExtension(storedUrl);
        if (string.IsNullOrWhiteSpace(fileId))
            fileId = asset.Id.ToString("N");

        var previous = asset.PosterUrl;
        asset.PosterUrl = await storage.SaveNamedAsync(
            "videos",
            asset.UserId,
            normalized,
            $"{fileId}{CoverMarker}.webp",
            ct);
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(previous)
            && !string.Equals(previous, asset.PosterUrl, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await storage.DeleteIfExistsAsync(previous, ct);
            }
            catch (Exception)
            {
                // The old grey still can stay. The feed now points at the cover.
            }
        }

        return true;
    }

    internal static bool HasCoverPoster(string? posterUrl) =>
        !string.IsNullOrWhiteSpace(posterUrl)
        && posterUrl.Contains(CoverMarker, StringComparison.OrdinalIgnoreCase);

    private static async Task<MemoryStream?> NormalizeCoverAsync(Stream image, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, ct);
        if (buffer.Length < 512 || buffer.Length > MaxPosterBytes)
            return null;

        buffer.Position = 0;
        try
        {
            using var decoded = await Image.LoadAsync(buffer, ct);
            if (decoded.Width < 32 || decoded.Height < 32)
                return null;

            if (decoded.Width > PosterMaxSide || decoded.Height > PosterMaxSide)
            {
                decoded.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(PosterMaxSide, PosterMaxSide),
                    Mode = ResizeMode.Max,
                }));
            }

            var output = new MemoryStream();
            await decoded.SaveAsWebpAsync(output, new WebpEncoder { Quality = 75 }, ct);
            output.Position = 0;
            return output;
        }
        catch (UnknownImageFormatException)
        {
            return null;
        }
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
