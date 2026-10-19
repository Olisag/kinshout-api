using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class CommunityJoinNotifierTests
{
    [Fact]
    public async Task NotifyJoinApprovedAsync_SendsEmailToMember()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);
        await db.SaveChangesAsync();

        var community = new Community
        {
            Slug = "open-club",
            Name = "Open Club",
            CreatedByUserId = creator.Id,
        };
        db.Communities.Add(community);
        await db.SaveChangesAsync();

        var email = new Mock<IEmailService>();
        var notifier = new CommunityJoinNotifier(
            db,
            email.Object,
            Options.Create(new EmailSettings { WebBaseUrl = "https://app.test" }),
            Mock.Of<ILogger<CommunityJoinNotifier>>());

        await notifier.NotifyJoinApprovedAsync(community, joiner);

        email.Verify(
            e => e.SendAsync(
                "joiner@test",
                It.Is<string>(s => s.Contains("open-club")),
                It.Is<string>(b => b.Contains("acceptée")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
