using System.Text.Json;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Kinshout.ExternalImporter.Import;
using Microsoft.EntityFrameworkCore;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Kinshout.Api.Tests;

public class TikTokVideoArchiveTests
{
    [Fact]
    public void Read_TakesTheDownloadedFileAndSkipsASlideshow()
    {
        const string json = """
        [
          {
            "webVideoUrl": "https://www.tiktok.com/@a/video/111?is_from_webapp=1",
            "mediaUrls": ["https://api.apify.com/v2/key-value-stores/x/records/111.mp4"],
            "isSlideshow": false
          },
          {
            "webVideoUrl": "https://www.tiktok.com/@a/video/222",
            "isSlideshow": true,
            "mediaUrls": ["https://api.apify.com/v2/key-value-stores/x/records/222.mp4"]
          },
          {
            "webVideoUrl": "https://www.tiktok.com/@a/video/333",
            "mediaUrls": [],
            "videoMeta": { "downloadAddr": "https://cdn.example/333.mp4" }
          }
        ]
        """;

        using var doc = JsonDocument.Parse(json);
        var files = TikTokDownloadedFiles.Read(doc);

        Assert.Equal(2, files.Count);
        Assert.Equal("111", TikTokDownloadedFiles.VideoId(files[0].WatchUrl));
        Assert.Contains("111.mp4", files[0].FileUrl, StringComparison.Ordinal);
        Assert.Equal("333", TikTokDownloadedFiles.VideoId(files[1].WatchUrl));
        Assert.Equal("https://cdn.example/333.mp4", files[1].FileUrl);
        Assert.Null(files[0].CoverUrl);
    }

    [Fact]
    public void Read_PrefersTheCreatorCoverOverASmallerStill()
    {
        const string json = """
        [
          {
            "webVideoUrl": "https://www.tiktok.com/@a/video/111",
            "mediaUrls": ["https://api.apify.com/v2/key-value-stores/x/records/111.mp4"],
            "videoMeta": {
              "coverUrl": "https://cdn.example/small.jpg",
              "originalCoverUrl": "https://cdn.example/cover.jpg"
            }
          },
          {
            "webVideoUrl": "https://www.tiktok.com/@a/video/444",
            "covers": { "default": "https://cdn.example/default.jpg", "origin": "https://cdn.example/origin.jpg" }
          },
          {
            "webVideoUrl": "https://www.tiktok.com/@a/video/555",
            "coverUrl": "https://cdn.example/clip.mp4"
          }
        ]
        """;

        using var doc = JsonDocument.Parse(json);
        var files = TikTokDownloadedFiles.Read(doc);
        var covers = TikTokDownloadedFiles.ReadCovers(doc);

        Assert.Equal("https://cdn.example/cover.jpg", Assert.Single(files).CoverUrl);
        Assert.Equal(2, covers.Count);
        Assert.Equal("https://cdn.example/origin.jpg", covers[1].CoverUrl);
    }

    [Fact]
    public async Task AttachPoster_ReplacesTheGreyStillAndLeavesAClipThatAlreadyHasACover()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var grey = "/uploads/videos/import/clip_poster.webp";
        var video = "/uploads/videos/import/clip.mp4";
        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            Title = "Une maman",
            Body = "Une maman.",
            SourceProvider = DiscussionSourceProvider.TikTok,
            SourceExternalId = "7710601313859521813",
            SourceExternalUrl = "https://www.tiktok.com/@worldactuallite0/video/7710601313859521813",
            VideoUrlsJson = $"[\"{video}\"]",
        });
        db.VideoAssets.Add(new VideoAsset
        {
            UserId = user.Id,
            VideoUrl = video,
            PosterUrl = grey,
            ContentType = "video/mp4",
        });
        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            Title = "Déjà couvert",
            Body = "Déjà.",
            SourceProvider = DiscussionSourceProvider.TikTok,
            SourceExternalId = "222",
            SourceExternalUrl = "https://www.tiktok.com/@a/video/222",
            VideoUrlsJson = "[\"/uploads/videos/import/done.mp4\"]",
        });
        db.VideoAssets.Add(new VideoAsset
        {
            UserId = user.Id,
            VideoUrl = "/uploads/videos/import/done.mp4",
            PosterUrl = "/uploads/videos/import/done_cover.webp",
            ContentType = "video/mp4",
        });
        await db.SaveChangesAsync();

        var storage = new MemoryStorage();
        var store = new ImportedVideoStore(db, storage);
        var needing = await store.TikToksNeedingPosterAsync(8);
        var onlyGrey = Assert.Single(needing);
        Assert.Equal(video, onlyGrey.VideoUrl);

        var attached = await store.AttachPosterAsync(video, await CoverPngAsync());

        Assert.True(attached);
        Assert.EndsWith("_cover.webp", storage.LastName, StringComparison.Ordinal);
        var asset = await db.VideoAssets.SingleAsync(item => item.VideoUrl == video);
        Assert.Contains("_cover.webp", asset.PosterUrl);
        Assert.Empty(await store.TikToksNeedingPosterAsync(8));
    }

    private static async Task<MemoryStream> CoverPngAsync()
    {
        using var image = new Image<Rgba32>(96, 160);
        var random = new Random(7);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        });
        var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream);
        stream.Position = 0;
        return stream;
    }

    private sealed class MemoryStorage : IUploadStorage
    {
        public string? LastName { get; private set; }

        public Task<string> SaveAsync(string folder, Guid userId, Stream stream, string extension, CancellationToken ct = default) =>
            SaveNamedAsync(folder, userId, stream, $"{Guid.NewGuid():N}{extension}", ct);

        public async Task<string> SaveNamedAsync(
            string folder,
            Guid userId,
            Stream stream,
            string fileName,
            CancellationToken ct = default)
        {
            await stream.CopyToAsync(Stream.Null, ct);
            LastName = fileName;
            return $"/uploads/{folder}/{userId:N}/{fileName}";
        }

        public Task<UploadFileContent?> OpenReadAsync(string uploadUrl, CancellationToken ct = default) =>
            Task.FromResult<UploadFileContent?>(null);

        public Task DeleteIfExistsAsync(string uploadUrl, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string uploadUrl, CancellationToken ct = default) => Task.FromResult(true);
    }

    [Fact]
    public void Choose_FilesNewClipsFirstAndStopsTheBacklogAtEight()
    {
        var fresh = Enumerable.Range(1, 2)
            .Select(i => Clip($"fresh-{i}", $"https://www.tiktok.com/@a/video/{i}"))
            .ToList();
        var backlog = Enumerable.Range(1, 20)
            .Select(i => new TikTokFileBacklogItem($"old-{i}", $"https://www.tiktok.com/@b/video/{100 + i}"))
            .ToList();

        var chosen = TikTokVideoArchiver.Choose(fresh, backlog);

        Assert.Equal(10, chosen.Count);
        Assert.Equal("https://www.tiktok.com/@a/video/1", chosen[0]);
        Assert.Equal(8, chosen.Count(url => url.Contains("/@b/", StringComparison.Ordinal)));
    }

    [Fact]
    public void WithStoredFile_DropsAClipThatStillPointsAtTikTok()
    {
        var saved = Clip("saved", "https://www.tiktok.com/@a/video/1") with
        {
            VideoUrls = ["/uploads/videos/import/clip.mp4"],
        };
        var missing = Clip("missing", "https://www.tiktok.com/@a/video/2");

        var ready = TikTokVideoArchiver.WithStoredFile([saved, missing]);

        Assert.Equal(["saved"], ready.Select(item => item.Source.ExternalId).ToArray());
    }

    [Fact]
    public void Choose_DoesNotDownloadTheSameClipTwice()
    {
        var watch = "https://www.tiktok.com/@a/video/7710601313859521813";
        var fresh = new List<ImportExternalDiscussionDto> { Clip("7710601313859521813", watch) };
        var backlog = new List<TikTokFileBacklogItem>
        {
            new("7710601313859521813", watch + "?lang=fr"),
        };

        var chosen = TikTokVideoArchiver.Choose(fresh, backlog);

        Assert.Equal([watch], chosen);
    }

    [Fact]
    public async Task Attach_PointsThePostAtTheFileAndLeavesTheArrivalTime()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var arrived = new DateTime(2026, 11, 26, 14, 13, 26, DateTimeKind.Utc);
        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            Title = "Une maman",
            Body = "Une maman que certains Congolais croyaient déjà décédée.",
            SourceProvider = DiscussionSourceProvider.TikTok,
            SourceExternalId = "7710601313859521813",
            SourceExternalUrl = "https://www.tiktok.com/@worldactuallite0/video/7710601313859521813",
            SourceImportedAt = arrived,
            CreatedAt = arrived.AddDays(-5),
            VideoUrlsJson = "[\"https://www.tiktok.com/@worldactuallite0/video/7710601313859521813\"]",
        });
        await db.SaveChangesAsync();

        var store = new ImportedVideoStore(db, Mock.Of<IUploadStorage>());
        var attached = await store.AttachTikTokFileAsync(
            "7710601313859521813",
            "/uploads/videos/import/clip.mp4");

        Assert.True(attached);
        var discussion = await db.Discussions.SingleAsync();
        Assert.Equal(arrived, discussion.SourceImportedAt);
        Assert.Equal(arrived.AddDays(-5), discussion.CreatedAt);
        Assert.Contains("/uploads/videos/import/clip.mp4", discussion.VideoUrlsJson);
        Assert.DoesNotContain("tiktok.com", discussion.VideoUrlsJson);
        Assert.Empty(await store.TikToksMissingFileAsync(8));
    }

    private static ImportExternalDiscussionDto Clip(string externalId, string watchUrl) =>
        new(
            Source: new ImportExternalDiscussionSourceDto(
                "tiktok",
                "TikTok",
                externalId,
                watchUrl,
                null,
                null,
                null),
            Title: "clip",
            Body: "clip",
            OriginalAuthor: null,
            EngagementScore: 1,
            Status: "active",
            PublishedAt: null,
            VideoUrls: [watchUrl]);
}
