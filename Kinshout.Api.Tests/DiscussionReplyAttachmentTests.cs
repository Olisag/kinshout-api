using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Kinshout.Api.Tests;

public class DiscussionReplyAttachmentTests
{
    [Fact]
    public async Task AddReplyAsync_TextOnly_SucceedsWithoutAttachment()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var service = CreateService(db);

        var reply = await service.AddReplyAsync(user.Id, discussion.Id, new CreateReplyRequestDto("Texte seul"));

        Assert.Equal("Texte seul", reply.Text);
        Assert.Null(reply.ImageUrl);
        Assert.Null(reply.VideoUrl);
        Assert.Null(reply.Location);

        var stored = await db.DiscussionReplies.AsNoTracking().SingleAsync(r => r.Id == reply.Id);
        Assert.Null(stored.ImageUrl);
        Assert.Null(stored.VideoUrl);
        Assert.Null(stored.Latitude);
    }

    [Fact]
    public async Task AddReplyAsync_ImageOnly_StoresOwnedImageUrl()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var imageUrl = $"/uploads/images/{user.Id:N}/pic.jpg";
        var service = CreateService(db);

        var reply = await service.AddReplyAsync(
            user.Id,
            discussion.Id,
            new CreateReplyRequestDto("Avec photo", ImageUrl: imageUrl));

        Assert.Equal(imageUrl, reply.ImageUrl);
        Assert.Null(reply.VideoUrl);
        Assert.Null(reply.Location);
    }

    [Fact]
    public async Task AddReplyAsync_VideoOnly_StoresOwnedVideoUrl()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var videoUrl = $"/uploads/videos/{user.Id:N}/clip.mp4";
        var service = CreateService(db);

        var reply = await service.AddReplyAsync(
            user.Id,
            discussion.Id,
            new CreateReplyRequestDto("Avec vidéo", VideoUrl: videoUrl));

        Assert.Equal(videoUrl, reply.VideoUrl);
        Assert.Null(reply.ImageUrl);
        Assert.Null(reply.Location);
    }

    [Fact]
    public async Task AddReplyAsync_LocationOnly_StoresCoordinatesAndLabel()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var service = CreateService(db);

        var reply = await service.AddReplyAsync(
            user.Id,
            discussion.Id,
            new CreateReplyRequestDto(
                "Au marché",
                Location: new DiscussionReplyLocationDto(-4.325, 15.322, "Marché central", "Ave Kasa-Vubu")));

        Assert.Null(reply.ImageUrl);
        Assert.Null(reply.VideoUrl);
        Assert.NotNull(reply.Location);
        Assert.Equal(-4.325, reply.Location!.Latitude);
        Assert.Equal(15.322, reply.Location.Longitude);
        Assert.Equal("Marché central", reply.Location.PlaceName);
        Assert.Equal("Ave Kasa-Vubu", reply.Location.Address);
    }

    [Fact]
    public async Task AddReplyAsync_ImageAndVideo_Rejects()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddReplyAsync(
                user.Id,
                discussion.Id,
                new CreateReplyRequestDto(
                    "Trop",
                    ImageUrl: $"/uploads/images/{user.Id:N}/a.jpg",
                    VideoUrl: $"/uploads/videos/{user.Id:N}/b.mp4")));

        Assert.Contains("pièce jointe", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddReplyAsync_ImageAndLocation_Rejects()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddReplyAsync(
                user.Id,
                discussion.Id,
                new CreateReplyRequestDto(
                    "Trop",
                    ImageUrl: $"/uploads/images/{user.Id:N}/a.jpg",
                    Location: new DiscussionReplyLocationDto(-4.3, 15.3))));

        Assert.Contains("pièce jointe", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddReplyAsync_ForeignImageUrl_Rejects()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var other = Guid.NewGuid();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddReplyAsync(
                user.Id,
                discussion.Id,
                new CreateReplyRequestDto(
                    "Volé",
                    ImageUrl: $"/uploads/images/{other:N}/a.jpg")));

        Assert.Contains("téléversés", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddReplyAsync_ForeignVideoUrl_Rejects()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var other = Guid.NewGuid();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddReplyAsync(
                user.Id,
                discussion.Id,
                new CreateReplyRequestDto(
                    "Volé",
                    VideoUrl: $"/uploads/videos/{other:N}/a.mp4")));

        Assert.Contains("téléversés", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddReplyAsync_InvalidLocationCoordinates_Rejects()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddReplyAsync(
                user.Id,
                discussion.Id,
                new CreateReplyRequestDto(
                    "Lieu",
                    Location: new DiscussionReplyLocationDto(95, 15.3))));

        Assert.Contains("Coordonnées", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateReplyAsync_CanReplaceTextWithImageAttachment()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var service = CreateService(db);
        var created = await service.AddReplyAsync(user.Id, discussion.Id, new CreateReplyRequestDto("Avant"));
        var imageUrl = $"/uploads/images/{user.Id:N}/after.jpg";

        var updated = await service.UpdateReplyAsync(
            user.Id,
            discussion.Id,
            created.Id,
            new UpdateReplyRequestDto("Après", ImageUrl: imageUrl));

        Assert.Equal("Après", updated.Text);
        Assert.Equal(imageUrl, updated.ImageUrl);
        Assert.Null(updated.VideoUrl);
        Assert.Null(updated.Location);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsReplyAttachmentInThread()
    {
        await using var db = TestDbFactory.Create();
        var (user, discussion) = await SeedDiscussionAsync(db);
        var imageUrl = $"/uploads/images/{user.Id:N}/thread.jpg";
        db.DiscussionReplies.Add(new DiscussionReply
        {
            DiscussionId = discussion.Id,
            UserId = user.Id,
            Body = "Vu",
            ImageUrl = imageUrl,
        });
        discussion.ReplyCount = 1;
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var detail = await service.GetByIdAsync(discussion.Id);

        Assert.NotNull(detail);
        var reply = Assert.Single(detail!.Thread.Items);
        Assert.Equal(imageUrl, reply.ImageUrl);
    }

    private static async Task<(User User, Discussion Discussion)> SeedDiscussionAsync(KinshoutDbContext db)
    {
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Thread",
            Body = "Body",
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();
        return (user, discussion);
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
