using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Kinshout.Api.Configuration;
using Microsoft.AspNetCore.Http;

namespace Kinshout.Api.Tests;

public class AuthServiceEmailTests
{
    [Fact]
    public async Task RegisterWithEmailAsync_CreatesLocalUser()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        var response = await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("marie@kinoiserie.test", "password123", "Marie K."),
            "kinshout-web");

        Assert.False(string.IsNullOrWhiteSpace(response.Token));
        Assert.Equal("marie@kinoiserie.test", response.User.Email);
        Assert.Equal("Marie K.", response.User.DisplayName);

        var user = Assert.Single(db.Users, u => u.Email == "marie@kinoiserie.test");
        Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash));
        Assert.Contains(db.UserLogins, l => l.Provider == AuthProvider.Local);
        Assert.Contains(db.CommunityMembers, m =>
            m.UserId == user.Id
            && m.Community.Slug == CommunityDefaults.GeneralSlug
            && m.Status == CommunityMemberStatuses.Approved);
    }

    [Fact]
    public async Task LoginWithEmailAsync_ReturnsTokenForValidPassword()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("login@kinoiserie.test", "password123", "Login User"),
            "kinshout-web");

        var response = await service.LoginWithEmailAsync(
            new EmailLoginRequestDto("login@kinoiserie.test", "password123"),
            "kinshout-web");

        Assert.False(string.IsNullOrWhiteSpace(response.Token));
        Assert.Equal("login@kinoiserie.test", response.User.Email);
    }

    [Fact]
    public async Task LoginWithEmailAsync_RejectsWrongPassword()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("login@kinoiserie.test", "password123", null),
            "kinshout-web");

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.LoginWithEmailAsync(
                new EmailLoginRequestDto("login@kinoiserie.test", "wrong-password"),
                "kinshout-web"));

        Assert.Equal(EmailAuthErrorCodes.InvalidCredentials, ex.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task LoginWithEmailAsync_RejectsUnknownEmailLikeAWrongPassword()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.LoginWithEmailAsync(
                new EmailLoginRequestDto("nobody@kinoiserie.test", "password123"),
                "kinshout-web"));

        Assert.Equal(EmailAuthErrorCodes.InvalidCredentials, ex.Code);
    }

    [Fact]
    public async Task LoginWithEmailAsync_TellsSocialAccountsToUseTheirProvider()
    {
        await using var db = TestDbFactory.Create();
        db.Users.Add(new User { DisplayName = "Google User", Email = "google@kinoiserie.test" });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.LoginWithEmailAsync(
                new EmailLoginRequestDto("google@kinoiserie.test", "password123"),
                "kinshout-web"));

        Assert.Equal(EmailAuthErrorCodes.SocialAccount, ex.Code);
    }

    [Fact]
    public async Task RegisterWithEmailAsync_RejectsShortPassword()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.RegisterWithEmailAsync(
                new EmailRegisterRequestDto("short@kinoiserie.test", "short", null),
                "kinshout-web"));

        Assert.Equal(EmailAuthErrorCodes.PasswordTooShort, ex.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task RegisterWithEmailAsync_RejectsInvalidEmail()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.RegisterWithEmailAsync(
                new EmailRegisterRequestDto("not-an-email", "password123", null),
                "kinshout-web"));

        Assert.Equal(EmailAuthErrorCodes.InvalidEmail, ex.Code);
    }

    [Fact]
    public async Task RegisterWithEmailAsync_RejectsDuplicateEmail()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("dup@kinoiserie.test", "password123", null),
            "kinshout-web");

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.RegisterWithEmailAsync(
                new EmailRegisterRequestDto("dup@kinoiserie.test", "password456", null),
                "kinshout-web"));

        Assert.Equal(EmailAuthErrorCodes.EmailInUse, ex.Code);
        Assert.Equal(StatusCodes.Status409Conflict, ex.StatusCode);
    }

    private static AuthService CreateService(KinshoutDbContext db) =>
        new(
            db,
            new JwtTokenService(Options.Create(new JwtSettings
            {
                SecretKey = "kinshout-test-secret-key-32chars!!",
                Issuer = "kinshout-test",
                UserAudience = "kinshout-user",
            })),
            Mock.Of<IUploadStorage>(),
            new UploadUrlResolver(
                Options.Create(new UploadStorageSettings { PublicBaseUrl = "https://api.test" }),
                Mock.Of<IHttpContextAccessor>()),
            Options.Create(new OAuthSettings()),
            Mock.Of<IFacebookAuthValidator>(),
            new PasswordHasher<User>(),
            Mock.Of<ILogger<AuthService>>());
}
