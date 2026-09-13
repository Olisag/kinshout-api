using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class DiscussionMediaHelperTests
{
    [Fact]
    public void NormalizeUrls_AcceptsOwnedUploads()
    {
        var userId = Guid.NewGuid();
        var urls = DiscussionMediaHelper.NormalizeUrls(
            [$"/uploads/images/{userId:N}/a.jpg", $"/uploads/images/{userId:N}/b.jpg"],
            userId,
            "images",
            DiscussionMediaHelper.MaxImages,
            "photos");

        Assert.Equal(2, urls.Count);
    }

    [Fact]
    public void NormalizeUrls_RejectsForeignUserPath()
    {
        var userId = Guid.NewGuid();
        var other = Guid.NewGuid();

        var ex = Assert.Throws<ArgumentException>(() =>
            DiscussionMediaHelper.NormalizeUrls(
                [$"/uploads/images/{other:N}/a.jpg"],
                userId,
                "images",
                DiscussionMediaHelper.MaxImages,
                "photos"));

        Assert.Contains("téléversés", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeUrls_EnforcesMaxCount()
    {
        var userId = Guid.NewGuid();
        var urls = Enumerable.Range(0, DiscussionMediaHelper.MaxVideos + 1)
            .Select(i => $"/uploads/videos/{userId:N}/{i}.mp4")
            .ToList();

        Assert.Throws<ArgumentException>(() =>
            DiscussionMediaHelper.NormalizeUrls(
                urls,
                userId,
                "videos",
                DiscussionMediaHelper.MaxVideos,
                "vidéos"));
    }

    [Fact]
    public void NormalizeOwnedUploadUrl_AcceptsOwnedPath()
    {
        var userId = Guid.NewGuid();
        var url = $"/uploads/images/{userId:N}/one.jpg";

        Assert.Equal(url, DiscussionMediaHelper.NormalizeOwnedUploadUrl(url, userId, "images"));
    }

    [Fact]
    public void NormalizeOwnedUploadUrl_RejectsForeignPath()
    {
        var userId = Guid.NewGuid();
        var other = Guid.NewGuid();

        var ex = Assert.Throws<ArgumentException>(() =>
            DiscussionMediaHelper.NormalizeOwnedUploadUrl(
                $"/uploads/videos/{other:N}/x.mp4",
                userId,
                "videos"));

        Assert.Contains("téléversés", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToMediaDtos_VideoWithPoster_UsesPreviewAsPrimaryUrl()
    {
        var asset = new VideoAsset
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            VideoUrl = "/uploads/videos/abc/clip.mp4",
            PosterUrl = "/uploads/videos/abc/clip_poster.webp",
            ContentType = "video/mp4",
            ByteSize = 1000,
        };
        var map = new Dictionary<string, VideoAsset>(StringComparer.OrdinalIgnoreCase)
        {
            [asset.VideoUrl] = asset,
        };

        var media = DiscussionMediaHelper.ToMediaDtos([], [asset.VideoUrl], map);
        var item = Assert.Single(media);

        Assert.Equal("video", item.Type);
        Assert.Equal($"/api/videos/{asset.Id}/preview", item.Url);
        Assert.Equal($"/api/videos/{asset.Id}/preview", item.PreviewUrl);
        Assert.Equal($"/api/videos/{asset.Id}/stream", item.PlayUrl);
    }

    [Fact]
    public void ToMediaDtos_VideoWithoutAsset_FallsBackToStorageUrl()
    {
        var storage = "/uploads/videos/abc/legacy.mp4";
        var media = DiscussionMediaHelper.ToMediaDtos([], [storage]);
        var item = Assert.Single(media);

        Assert.Equal("video", item.Type);
        Assert.Equal(storage, item.Url);
        Assert.Null(item.PreviewUrl);
        Assert.Equal(storage, item.PlayUrl);
    }
}

public class DiscussionMediaServiceTests
{
    [Fact]
    public async Task AddAndRemoveMedia_UpdatesDiscussionJson()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Media thread",
            Body = "Body",
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var imageUrl = $"/uploads/images/{user.Id:N}/photo1.jpg";
        var videoUrl = $"/uploads/videos/{user.Id:N}/clip1.mp4";
        var service = CreateService(db);

        var withMedia = await service.AddMediaAsync(
            user.Id,
            discussion.Id,
            new DiscussionMediaUpdateRequestDto([imageUrl], [videoUrl]));

        Assert.Equal(2, withMedia.Media!.Count);
        Assert.Contains(withMedia.Media, m => m.Type == "image" && m.Url == imageUrl);
        Assert.Contains(withMedia.Media, m => m.Type == "video" && m.Url == videoUrl);

        var cleared = await service.RemoveMediaAsync(
            user.Id,
            discussion.Id,
            new DiscussionMediaUpdateRequestDto([imageUrl], [videoUrl]));

        Assert.True(cleared.Media is null || cleared.Media.Count == 0);
    }

    private static DiscussionService CreateService(KinshoutDbContext db)
    {
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new DiscussionService(
            db,
            Mock.Of<IOpenAiService>(),
            moderation.Object,
            Mock.Of<IUploadStorage>(),
            TestDbFactory.CreatePermissiveCommunityService(),
            TestDbFactory.CreatePermissiveDiscussionParticipationService(),
            TestDbFactory.CreateMemoryCache());
    }
}
