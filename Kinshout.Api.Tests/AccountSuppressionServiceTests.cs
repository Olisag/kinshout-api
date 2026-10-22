using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Kinshout.Api.Tests;

public class AccountSuppressionServiceTests
{
    [Fact]
    public async Task SuppressAsync_DeletesUserAndOwnedContent()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var other = new User { Email = "other@kinshout.test", DisplayName = "Other" };
        db.Users.Add(other);
        await db.SaveChangesAsync();

        var general = await CommunitySeed.EnsureGeneralCommunityAsync(db);
        await CommunitySeed.JoinGeneralCommunityAsync(db, user.Id);
        await CommunitySeed.JoinGeneralCommunityAsync(db, other.Id);

        var emptyCommunity = new Community
        {
            Slug = "empty-club",
            Name = "Empty Club",
            CreatedByUserId = user.Id,
        };
        var keptCommunity = new Community
        {
            Slug = "shared-club",
            Name = "Shared Club",
            CreatedByUserId = user.Id,
        };
        db.Communities.AddRange(emptyCommunity, keptCommunity);
        await db.SaveChangesAsync();

        var advert = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Ma chambre",
            Description = "À louer",
            ImageUrlsJson = """["/uploads/images/abc.jpg"]""",
            ResumeUrl = "/uploads/resumes/cv.pdf",
        };
        db.Adverts.Add(advert);

        var discussion = new Discussion
        {
            UserId = user.Id,
            CommunityId = general.Id,
            Title = "Salut Kinshasa",
            Body = "Bonjour",
            ImageUrlsJson = """["/uploads/images/post.jpg"]""",
            ReplyCount = 1,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var ownedReply = new DiscussionReply
        {
            DiscussionId = discussion.Id,
            UserId = other.Id,
            Body = "Réponse d'un autre",
        };
        var replyOnOther = new Discussion
        {
            UserId = other.Id,
            CommunityId = keptCommunity.Id,
            Title = "Sujet partagé",
            Body = "Contenu",
            ReplyCount = 1,
        };
        db.Discussions.Add(replyOnOther);
        await db.SaveChangesAsync();

        var userReply = new DiscussionReply
        {
            DiscussionId = replyOnOther.Id,
            UserId = user.Id,
            Body = "Ma réponse",
            ImageUrl = "/uploads/images/reply.jpg",
        };
        db.DiscussionReplies.AddRange(ownedReply, userReply);

        db.UserLogins.Add(new UserLogin
        {
            UserId = user.Id,
            Provider = AuthProvider.Google,
            ProviderKey = "google-1",
        });
        db.SavedAdverts.Add(new SavedAdvert { UserId = other.Id, AdvertId = advert.Id });
        db.SavedAdverts.Add(new SavedAdvert
        {
            UserId = user.Id,
            AdvertId = (await SeedOtherAdvertAsync(db, other.Id, category.Id)).Id,
        });
        db.LikedDiscussions.Add(new LikedDiscussion { UserId = user.Id, DiscussionId = replyOnOther.Id });
        db.VideoAssets.Add(new VideoAsset
        {
            UserId = user.Id,
            VideoUrl = "/uploads/videos/clip.mp4",
            PosterUrl = "/uploads/videos/clip.jpg",
            Status = VideoAssetStatus.Ready,
        });
        user.AvatarUrl = $"/uploads/avatars/{user.Id:N}/avatar.jpg";
        await db.SaveChangesAsync();

        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storage = new Mock<IUploadStorage>();
        storage.Setup(s => s.DeleteIfExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((url, _) => deleted.Add(url))
            .Returns(Task.CompletedTask);

        var service = new AccountSuppressionService(
            db,
            storage.Object,
            NullLogger<AccountSuppressionService>.Instance);

        await service.SuppressAsync(user.Id);

        Assert.Null(await db.Users.FindAsync(user.Id));
        Assert.Empty(await db.Adverts.Where(a => a.UserId == user.Id).ToListAsync());
        Assert.Empty(await db.Discussions.Where(d => d.UserId == user.Id).ToListAsync());
        Assert.Empty(await db.DiscussionReplies.Where(r => r.UserId == user.Id).ToListAsync());
        Assert.Empty(await db.UserLogins.Where(l => l.UserId == user.Id).ToListAsync());
        Assert.Empty(await db.SavedAdverts.Where(s => s.UserId == user.Id).ToListAsync());
        Assert.Empty(await db.LikedDiscussions.Where(l => l.UserId == user.Id).ToListAsync());
        Assert.Empty(await db.VideoAssets.Where(v => v.UserId == user.Id).ToListAsync());
        Assert.Empty(await db.CommunityMembers.Where(m => m.UserId == user.Id).ToListAsync());
        Assert.Null(await db.Communities.FirstOrDefaultAsync(c => c.Slug == "empty-club"));

        var shared = await db.Communities.SingleAsync(c => c.Slug == "shared-club");
        var importUser = await db.Users.SingleAsync(u => u.Email == ImportSeed.ImportUserEmail);
        Assert.Equal(importUser.Id, shared.CreatedByUserId);

        var otherDiscussion = await db.Discussions.SingleAsync(d => d.Id == replyOnOther.Id);
        Assert.Equal(0, otherDiscussion.ReplyCount);

        Assert.Contains("/uploads/images/abc.jpg", deleted);
        Assert.Contains("/uploads/resumes/cv.pdf", deleted);
        Assert.Contains("/uploads/images/post.jpg", deleted);
        Assert.Contains("/uploads/images/reply.jpg", deleted);
        Assert.Contains("/uploads/videos/clip.mp4", deleted);
        Assert.Contains($"/uploads/avatars/{user.Id:N}/avatar.jpg", deleted);

        Assert.NotNull(await db.Users.FindAsync(other.Id));
    }

    [Fact]
    public async Task SuppressAsync_RejectsSystemImportUser()
    {
        await using var db = TestDbFactory.Create();
        var importUser = await ImportSeed.EnsureImportUserAsync(db);
        var service = new AccountSuppressionService(
            db,
            Mock.Of<IUploadStorage>(),
            NullLogger<AccountSuppressionService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SuppressAsync(importUser.Id));
        Assert.NotNull(await db.Users.FindAsync(importUser.Id));
    }

    private static async Task<Advert> SeedOtherAdvertAsync(KinshoutDbContext db, Guid userId, Guid categoryId)
    {
        var advert = new Advert
        {
            UserId = userId,
            CategoryId = categoryId,
            Title = "Autre annonce",
            Description = "Conservée",
        };
        db.Adverts.Add(advert);
        await db.SaveChangesAsync();
        return advert;
    }
}
