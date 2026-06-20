using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class DiscussionServiceThreadTests
{
    [Fact]
    public async Task GetByIdAsync_PaginatesRepliesChronologically()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Thread test",
            Body = "Main post",
            User = user,
            Category = category,
        };
        db.Discussions.Add(discussion);

        for (var i = 0; i < 5; i++)
        {
            db.DiscussionReplies.Add(new DiscussionReply
            {
                DiscussionId = discussion.Id,
                UserId = user.Id,
                Body = $"Reply {i + 1}",
                CreatedAt = DateTime.UtcNow.AddMinutes(i),
            });
        }

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var page1 = await service.GetByIdAsync(discussion.Id, page: 1, pageSize: 2);
        var page2 = await service.GetByIdAsync(discussion.Id, page: 2, pageSize: 2);

        Assert.NotNull(page1);
        Assert.Equal("Thread test", page1!.Title);
        Assert.Equal(5, page1.Thread.TotalCount);
        Assert.Equal(["Reply 1", "Reply 2"], page1.Thread.Items.Select(r => r.Text).ToArray());
        Assert.True(page1.Thread.HasMore);

        Assert.Equal(["Reply 3", "Reply 4"], page2!.Thread.Items.Select(r => r.Text).ToArray());
        Assert.True(page2.Thread.HasMore);
    }

    private static DiscussionService CreateService(KinshoutDbContext db)
    {
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new DiscussionService(db, Mock.Of<IOpenAiService>(), moderation.Object);
    }
}
