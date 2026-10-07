using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class AuthServiceFacebookTests
{
    [Fact]
    public async Task SignInWithFacebookAsync_CreatesUserAndLogin()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db, FacebookReturns("fb-123", "marie@example.com"));

        var auth = await service.SignInWithFacebookAsync("fb-token", "kinshout-web");

        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.Equal("Marie K.", auth.User.DisplayName);
        Assert.Equal("marie@example.com", auth.User.Email);
        Assert.Single(db.UserLogins.Where(x => x.Provider == AuthProvider.Facebook && x.ProviderKey == "fb-123"));
        Assert.NotNull((await db.Users.SingleAsync(u => u.Email == "marie@example.com")).EmailConfirmedAt);
    }

    [Fact]
    public async Task SignInWithFacebookAsync_TakesOverAPendingEmailSignUp()
    {
        await using var db = TestDbFactory.Create();
        var pending = new User
        {
            DisplayName = "Squatter",
            Email = "marie@example.com",
            PasswordHash = "not-proven",
            EmailConfirmationTokenHash = EmailConfirmationTokens.Hash("pending-token"),
            EmailConfirmationSentAt = DateTime.UtcNow,
        };
        db.Users.Add(pending);
        db.UserLogins.Add(new UserLogin { User = pending, Provider = AuthProvider.Local, ProviderKey = pending.Email });
        await db.SaveChangesAsync();
        var service = CreateService(db, FacebookReturns("fb-123", "marie@example.com"));

        await service.SignInWithFacebookAsync("fb-token", "kinshout-web");

        var user = await db.Users.SingleAsync(u => u.Email == "marie@example.com");
        Assert.NotNull(user.EmailConfirmedAt);
        Assert.Null(user.PasswordHash);
        Assert.Null(user.EmailConfirmationTokenHash);
        var login = Assert.Single(db.UserLogins.Where(l => l.UserId == user.Id));
        Assert.Equal(AuthProvider.Facebook, login.Provider);
    }

    private static IFacebookAuthValidator FacebookReturns(string facebookId, string email)
    {
        var facebook = new Mock<IFacebookAuthValidator>();
        facebook
            .Setup(x => x.ValidateAccessTokenAsync("fb-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacebookUserInfo(facebookId, email, "Marie K.", "https://cdn.example/avatar.jpg"));
        return facebook.Object;
    }

    private static AuthService CreateService(KinshoutDbContext db, IFacebookAuthValidator facebook) =>
        new(
            db,
            new JwtTokenService(Options.Create(new JwtSettings
            {
                SecretKey = "kinshout-test-secret-key-32chars!!",
                Issuer = "kinshout-test",
                UserAudience = "kinshout-user",
                ClientAudience = "kinshout-client",
            })),
            Mock.Of<IUploadStorage>(),
            new UploadUrlResolver(
                Options.Create(new UploadStorageSettings { PublicBaseUrl = "https://api.test" }),
                Mock.Of<IHttpContextAccessor>()),
            Options.Create(new OAuthSettings()),
            facebook,
            new Microsoft.AspNetCore.Identity.PasswordHasher<User>(),
            Mock.Of<IEmailConfirmationSender>(),
            Mock.Of<ILogger<AuthService>>());
}
