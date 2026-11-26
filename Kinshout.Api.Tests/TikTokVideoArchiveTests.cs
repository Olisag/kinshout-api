using System.Text.Json;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Kinshout.ExternalImporter.Import;
using Microsoft.EntityFrameworkCore;
using Moq;

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
