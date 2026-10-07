using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Kinshout.Api.Configuration;
using Microsoft.AspNetCore.Http;

namespace Kinshout.Api.Tests;

public class AuthServiceEmailTests
{
    [Fact]
    public async Task RegisterWithEmailAsync_CreatesUnconfirmedLocalUserAndSendsLink()
    {
        await using var db = TestDbFactory.Create();
        var sender = new CapturingConfirmationSender();
        var service = CreateService(db, sender);

        var response = await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("Marie@Kinoiserie.test", "password123", "Marie K."));

        Assert.Equal("marie@kinoiserie.test", response.Email);
        Assert.True(response.ConfirmationRequired);

        var user = Assert.Single(db.Users, u => u.Email == "marie@kinoiserie.test");
        Assert.Equal("Marie K.", user.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash));
        Assert.Null(user.EmailConfirmedAt);
        Assert.Contains(db.UserLogins, l => l.Provider == AuthProvider.Local);
        Assert.Contains(db.CommunityMembers, m =>
            m.UserId == user.Id
            && m.Community.Slug == CommunityDefaults.GeneralSlug
            && m.Status == CommunityMemberStatuses.Approved);

        var (sentTo, token) = Assert.Single(sender.Sent);
        Assert.Equal(user.Id, sentTo.Id);
        Assert.Equal(EmailConfirmationTokens.Hash(token), user.EmailConfirmationTokenHash);
    }

    [Fact]
    public async Task LoginWithEmailAsync_BlocksUntilEmailIsConfirmed()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("pending@kinoiserie.test", "password123", null));

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.LoginWithEmailAsync(
                new EmailLoginRequestDto("pending@kinoiserie.test", "password123"),
                "kinshout-web"));

        Assert.Equal(EmailAuthErrorCodes.EmailNotConfirmed, ex.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task LoginWithEmailAsync_WrongPasswordOnUnconfirmedAccountIsStillInvalidCredentials()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("pending@kinoiserie.test", "password123", null));

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.LoginWithEmailAsync(
                new EmailLoginRequestDto("pending@kinoiserie.test", "wrong-password"),
                "kinshout-web"));

        Assert.Equal(EmailAuthErrorCodes.InvalidCredentials, ex.Code);
    }

    [Fact]
    public async Task ConfirmEmailAsync_ConfirmsSignsInAndBurnsTheToken()
    {
        await using var db = TestDbFactory.Create();
        var sender = new CapturingConfirmationSender();
        var service = CreateService(db, sender);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("confirm@kinoiserie.test", "password123", "Confirm Me"));
        var token = sender.Sent.Single().Token;

        var response = await service.ConfirmEmailAsync(token, "kinshout-web");

        Assert.False(string.IsNullOrWhiteSpace(response.Token));
        Assert.Equal("confirm@kinoiserie.test", response.User.Email);
        var user = await db.Users.SingleAsync(u => u.Email == "confirm@kinoiserie.test");
        Assert.NotNull(user.EmailConfirmedAt);
        Assert.Null(user.EmailConfirmationTokenHash);

        var reuse = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ConfirmEmailAsync(token, "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.InvalidConfirmationToken, reuse.Code);

        var login = await service.LoginWithEmailAsync(
            new EmailLoginRequestDto("confirm@kinoiserie.test", "password123"),
            "kinshout-web");
        Assert.False(string.IsNullOrWhiteSpace(login.Token));
    }

    [Fact]
    public async Task ConfirmEmailAsync_RejectsUnknownAndExpiredTokens()
    {
        await using var db = TestDbFactory.Create();
        var sender = new CapturingConfirmationSender();
        var service = CreateService(db, sender);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("late@kinoiserie.test", "password123", null));
        var token = sender.Sent.Single().Token;

        var unknown = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ConfirmEmailAsync("not-a-real-token", "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.InvalidConfirmationToken, unknown.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, unknown.StatusCode);

        var user = await db.Users.SingleAsync(u => u.Email == "late@kinoiserie.test");
        user.EmailConfirmationSentAt = DateTime.UtcNow - EmailConfirmationTokens.Lifetime - TimeSpan.FromMinutes(1);
        await db.SaveChangesAsync();

        var expired = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ConfirmEmailAsync(token, "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.InvalidConfirmationToken, expired.Code);
        Assert.Null((await db.Users.SingleAsync(u => u.Id == user.Id)).EmailConfirmedAt);
    }

    [Fact]
    public async Task ResendConfirmationAsync_SendsNewLinkAfterCooldownAndInvalidatesOldOne()
    {
        await using var db = TestDbFactory.Create();
        var sender = new CapturingConfirmationSender();
        var service = CreateService(db, sender);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("resend@kinoiserie.test", "password123", null));
        var firstToken = sender.Sent.Single().Token;

        await service.ResendConfirmationAsync("resend@kinoiserie.test");
        Assert.Single(sender.Sent);

        var user = await db.Users.SingleAsync(u => u.Email == "resend@kinoiserie.test");
        user.EmailConfirmationSentAt = DateTime.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync();

        await service.ResendConfirmationAsync("resend@kinoiserie.test");
        Assert.Equal(2, sender.Sent.Count);

        var stale = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ConfirmEmailAsync(firstToken, "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.InvalidConfirmationToken, stale.Code);

        var response = await service.ConfirmEmailAsync(sender.Sent[1].Token, "kinshout-web");
        Assert.Equal("resend@kinoiserie.test", response.User.Email);
    }

    [Fact]
    public async Task ResendConfirmationAsync_SaysNothingForUnknownOrConfirmedAddresses()
    {
        await using var db = TestDbFactory.Create();
        db.Users.Add(new User
        {
            DisplayName = "Done",
            Email = "done@kinoiserie.test",
            EmailConfirmedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var sender = new CapturingConfirmationSender();
        var service = CreateService(db, sender);

        await service.ResendConfirmationAsync("nobody@kinoiserie.test");
        await service.ResendConfirmationAsync("done@kinoiserie.test");

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task RegisterWithEmailAsync_ReplacesAPendingSignUpForTheSameAddress()
    {
        await using var db = TestDbFactory.Create();
        var sender = new CapturingConfirmationSender();
        var service = CreateService(db, sender);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("again@kinoiserie.test", "first-password", "First"));
        var user = await db.Users.SingleAsync(u => u.Email == "again@kinoiserie.test");
        user.EmailConfirmationSentAt = DateTime.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync();

        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("again@kinoiserie.test", "second-password", "Second"));

        var only = await db.Users.SingleAsync(u => u.Email == "again@kinoiserie.test");
        Assert.Equal("Second", only.DisplayName);
        Assert.Single(db.UserLogins, l => l.UserId == only.Id);
        Assert.Equal(2, sender.Sent.Count);

        await service.ConfirmEmailAsync(sender.Sent[1].Token, "kinshout-web");
        await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.LoginWithEmailAsync(
                new EmailLoginRequestDto("again@kinoiserie.test", "first-password"),
                "kinshout-web"));
        var login = await service.LoginWithEmailAsync(
            new EmailLoginRequestDto("again@kinoiserie.test", "second-password"),
            "kinshout-web");
        Assert.Equal("Second", login.User.DisplayName);
    }

    [Fact]
    public async Task RegisterWithEmailAsync_ReportsWhenTheEmailCannotBeSent()
    {
        await using var db = TestDbFactory.Create();
        var sender = new Mock<IEmailConfirmationSender>();
        sender
            .Setup(s => s.SendAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));
        var service = CreateService(db, sender.Object);

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.RegisterWithEmailAsync(
                new EmailRegisterRequestDto("down@kinoiserie.test", "password123", null)));

        Assert.Equal(EmailAuthErrorCodes.ConfirmationEmailFailed, ex.Code);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
        var user = await db.Users.SingleAsync(u => u.Email == "down@kinoiserie.test");
        Assert.Null(user.EmailConfirmationSentAt);
    }

    [Fact]
    public async Task LoginWithEmailAsync_RejectsWrongPassword()
    {
        await using var db = TestDbFactory.Create();
        await AddConfirmedLocalUserAsync(db, "login@kinoiserie.test", "password123");
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.LoginWithEmailAsync(
                new EmailLoginRequestDto("login@kinoiserie.test", "wrong-password"),
                "kinshout-web"));

        Assert.Equal(EmailAuthErrorCodes.InvalidCredentials, ex.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task LoginWithEmailAsync_ReturnsTokenForConfirmedAccount()
    {
        await using var db = TestDbFactory.Create();
        await AddConfirmedLocalUserAsync(db, "login@kinoiserie.test", "password123");
        var service = CreateService(db);

        var response = await service.LoginWithEmailAsync(
            new EmailLoginRequestDto("login@kinoiserie.test", "password123"),
            "kinshout-web");

        Assert.False(string.IsNullOrWhiteSpace(response.Token));
        Assert.Equal("login@kinoiserie.test", response.User.Email);
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
                new EmailRegisterRequestDto("short@kinoiserie.test", "short", null)));

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
                new EmailRegisterRequestDto("not-an-email", "password123", null)));

        Assert.Equal(EmailAuthErrorCodes.InvalidEmail, ex.Code);
    }

    [Fact]
    public async Task RegisterWithEmailAsync_RejectsConfirmedDuplicateEmail()
    {
        await using var db = TestDbFactory.Create();
        await AddConfirmedLocalUserAsync(db, "dup@kinoiserie.test", "password123");
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.RegisterWithEmailAsync(
                new EmailRegisterRequestDto("dup@kinoiserie.test", "password456", null)));

        Assert.Equal(EmailAuthErrorCodes.EmailInUse, ex.Code);
        Assert.Equal(StatusCodes.Status409Conflict, ex.StatusCode);
    }

    private static async Task AddConfirmedLocalUserAsync(KinshoutDbContext db, string email, string password)
    {
        var user = new User
        {
            DisplayName = email.Split('@')[0],
            Email = email,
            EmailConfirmedAt = DateTime.UtcNow,
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
        db.Users.Add(user);
        db.UserLogins.Add(new UserLogin { User = user, Provider = AuthProvider.Local, ProviderKey = email });
        await db.SaveChangesAsync();
    }

    private sealed class CapturingConfirmationSender : IEmailConfirmationSender
    {
        public List<(User User, string Token)> Sent { get; } = [];

        public Task SendAsync(User user, string token, CancellationToken ct = default)
        {
            Sent.Add((user, token));
            return Task.CompletedTask;
        }
    }

    private static AuthService CreateService(KinshoutDbContext db, IEmailConfirmationSender? sender = null) =>
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
            sender ?? new CapturingConfirmationSender(),
            Mock.Of<ILogger<AuthService>>());
}
