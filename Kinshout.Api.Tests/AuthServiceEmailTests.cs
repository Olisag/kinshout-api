using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
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
        var sender = new CapturingAuthEmailSender();
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

        var (sentTo, token) = Assert.Single(sender.Confirmations);
        Assert.Equal(user.Id, sentTo.Id);
        Assert.Equal(AuthEmailTokens.Hash(token), user.EmailConfirmationTokenHash);
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
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("confirm@kinoiserie.test", "password123", "Confirm Me"));
        var token = sender.Confirmations.Single().Token;

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
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("late@kinoiserie.test", "password123", null));
        var token = sender.Confirmations.Single().Token;

        var unknown = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ConfirmEmailAsync("not-a-real-token", "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.InvalidConfirmationToken, unknown.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, unknown.StatusCode);

        var user = await db.Users.SingleAsync(u => u.Email == "late@kinoiserie.test");
        user.EmailConfirmationSentAt = DateTime.UtcNow - AuthEmailTokens.ConfirmationLifetime - TimeSpan.FromMinutes(1);
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
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("resend@kinoiserie.test", "password123", null));
        var firstToken = sender.Confirmations.Single().Token;

        await service.ResendConfirmationAsync("resend@kinoiserie.test");
        Assert.Single(sender.Confirmations);

        var user = await db.Users.SingleAsync(u => u.Email == "resend@kinoiserie.test");
        user.EmailConfirmationSentAt = DateTime.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync();

        await service.ResendConfirmationAsync("resend@kinoiserie.test");
        Assert.Equal(2, sender.Confirmations.Count);

        var stale = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ConfirmEmailAsync(firstToken, "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.InvalidConfirmationToken, stale.Code);

        var response = await service.ConfirmEmailAsync(sender.Confirmations[1].Token, "kinshout-web");
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
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);

        await service.ResendConfirmationAsync("nobody@kinoiserie.test");
        await service.ResendConfirmationAsync("done@kinoiserie.test");

        Assert.Empty(sender.Confirmations);
    }

    [Fact]
    public async Task RegisterWithEmailAsync_ReplacesAPendingSignUpForTheSameAddress()
    {
        await using var db = TestDbFactory.Create();
        var sender = new CapturingAuthEmailSender();
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
        Assert.Equal(2, sender.Confirmations.Count);

        await service.ConfirmEmailAsync(sender.Confirmations[1].Token, "kinshout-web");
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
        var sender = new Mock<IAuthEmailSender>();
        sender
            .Setup(s => s.SendConfirmationAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

    [Fact]
    public async Task RequestPasswordResetAsync_SendsALinkToAnExistingAccount()
    {
        await using var db = TestDbFactory.Create();
        await AddConfirmedLocalUserAsync(db, "forgot@kinoiserie.test", "password123");
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);

        await service.RequestPasswordResetAsync(" Forgot@Kinoiserie.test ");

        var (sentTo, token) = Assert.Single(sender.PasswordResets);
        var user = await db.Users.SingleAsync(u => u.Email == "forgot@kinoiserie.test");
        Assert.Equal(user.Id, sentTo.Id);
        Assert.Equal(AuthEmailTokens.Hash(token), user.PasswordResetTokenHash);
        Assert.NotNull(user.PasswordResetSentAt);
    }

    [Fact]
    public async Task RequestPasswordResetAsync_SaysNothingForUnknownOrUndeliverableAddresses()
    {
        await using var db = TestDbFactory.Create();
        db.Users.Add(new User
        {
            DisplayName = "Facebook User",
            Email = "1234@facebook.kinshout",
            EmailConfirmedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);

        await service.RequestPasswordResetAsync("nobody@kinoiserie.test");
        await service.RequestPasswordResetAsync("1234@facebook.kinshout");

        Assert.Empty(sender.PasswordResets);
    }

    [Fact]
    public async Task RequestPasswordResetAsync_WaitsAMinuteBeforeSendingAnotherLink()
    {
        await using var db = TestDbFactory.Create();
        await AddConfirmedLocalUserAsync(db, "again@kinoiserie.test", "password123");
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);

        await service.RequestPasswordResetAsync("again@kinoiserie.test");
        await service.RequestPasswordResetAsync("again@kinoiserie.test");
        Assert.Single(sender.PasswordResets);

        var user = await db.Users.SingleAsync(u => u.Email == "again@kinoiserie.test");
        user.PasswordResetSentAt = DateTime.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync();

        await service.RequestPasswordResetAsync("again@kinoiserie.test");
        Assert.Equal(2, sender.PasswordResets.Count);

        var stale = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ResetPasswordAsync(sender.PasswordResets[0].Token, "new-password1", "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.InvalidResetToken, stale.Code);
    }

    [Fact]
    public async Task RequestPasswordResetAsync_ReportsWhenTheEmailCannotBeSent()
    {
        await using var db = TestDbFactory.Create();
        await AddConfirmedLocalUserAsync(db, "down@kinoiserie.test", "password123");
        var sender = new Mock<IAuthEmailSender>();
        sender
            .Setup(s => s.SendPasswordResetAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));
        var service = CreateService(db, sender.Object);

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.RequestPasswordResetAsync("down@kinoiserie.test"));

        Assert.Equal(EmailAuthErrorCodes.PasswordResetEmailFailed, ex.Code);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
        var user = await db.Users.SingleAsync(u => u.Email == "down@kinoiserie.test");
        Assert.Null(user.PasswordResetSentAt);
    }

    [Fact]
    public async Task ResetPasswordAsync_ChangesThePasswordSignsInAndBurnsTheLink()
    {
        await using var db = TestDbFactory.Create();
        await AddConfirmedLocalUserAsync(db, "reset@kinoiserie.test", "old-password");
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);
        await service.RequestPasswordResetAsync("reset@kinoiserie.test");
        var token = sender.PasswordResets.Single().Token;

        var response = await service.ResetPasswordAsync(token, "new-password", "kinshout-web");

        Assert.False(string.IsNullOrWhiteSpace(response.Token));
        Assert.Equal("reset@kinoiserie.test", response.User.Email);
        var user = await db.Users.SingleAsync(u => u.Email == "reset@kinoiserie.test");
        Assert.Null(user.PasswordResetTokenHash);
        Assert.Null(user.PasswordResetSentAt);
        Assert.Equal(user.Id, Assert.Single(sender.PasswordChanges).Id);

        var sessions = new UserSessionValidator(db, new MemoryCache(new MemoryCacheOptions()));
        Assert.True(await sessions.IsActiveAsync(user.Id, new JsonWebToken(response.Token).IssuedAt));
        Assert.False(await sessions.IsActiveAsync(user.Id, user.SessionsValidAfter!.Value.AddSeconds(-1)));

        var reuse = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ResetPasswordAsync(token, "another-password", "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.InvalidResetToken, reuse.Code);

        await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.LoginWithEmailAsync(new EmailLoginRequestDto("reset@kinoiserie.test", "old-password"), "kinshout-web"));
        var login = await service.LoginWithEmailAsync(
            new EmailLoginRequestDto("reset@kinoiserie.test", "new-password"),
            "kinshout-web");
        Assert.False(string.IsNullOrWhiteSpace(login.Token));
    }

    [Fact]
    public async Task ResetPasswordAsync_RejectsUnknownAndExpiredLinks()
    {
        await using var db = TestDbFactory.Create();
        await AddConfirmedLocalUserAsync(db, "late@kinoiserie.test", "old-password");
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);
        await service.RequestPasswordResetAsync("late@kinoiserie.test");
        var token = sender.PasswordResets.Single().Token;

        var unknown = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ResetPasswordAsync("not-a-real-token", "new-password", "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.InvalidResetToken, unknown.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, unknown.StatusCode);

        var user = await db.Users.SingleAsync(u => u.Email == "late@kinoiserie.test");
        user.PasswordResetSentAt = DateTime.UtcNow - AuthEmailTokens.PasswordResetLifetime - TimeSpan.FromMinutes(1);
        await db.SaveChangesAsync();

        var expired = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ResetPasswordAsync(token, "new-password", "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.InvalidResetToken, expired.Code);
        Assert.Empty(sender.PasswordChanges);
    }

    [Fact]
    public async Task ResetPasswordAsync_RejectsAShortPasswordAndKeepsTheLink()
    {
        await using var db = TestDbFactory.Create();
        await AddConfirmedLocalUserAsync(db, "short@kinoiserie.test", "old-password");
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);
        await service.RequestPasswordResetAsync("short@kinoiserie.test");
        var token = sender.PasswordResets.Single().Token;

        var ex = await Assert.ThrowsAsync<EmailAuthException>(() =>
            service.ResetPasswordAsync(token, "short", "kinshout-web"));
        Assert.Equal(EmailAuthErrorCodes.PasswordTooShort, ex.Code);

        var response = await service.ResetPasswordAsync(token, "long-enough", "kinshout-web");
        Assert.Equal("short@kinoiserie.test", response.User.Email);
    }

    [Fact]
    public async Task ResetPasswordAsync_ConfirmsAPendingSignUp()
    {
        await using var db = TestDbFactory.Create();
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("pending@kinoiserie.test", "password123", null));
        await service.RequestPasswordResetAsync("pending@kinoiserie.test");

        await service.ResetPasswordAsync(sender.PasswordResets.Single().Token, "new-password", "kinshout-web");

        var user = await db.Users.SingleAsync(u => u.Email == "pending@kinoiserie.test");
        Assert.NotNull(user.EmailConfirmedAt);
        Assert.Null(user.EmailConfirmationTokenHash);
        var login = await service.LoginWithEmailAsync(
            new EmailLoginRequestDto("pending@kinoiserie.test", "new-password"),
            "kinshout-web");
        Assert.False(string.IsNullOrWhiteSpace(login.Token));
    }

    [Fact]
    public async Task ResetPasswordAsync_LetsASocialAccountAddAPassword()
    {
        await using var db = TestDbFactory.Create();
        var google = new User
        {
            DisplayName = "Google User",
            Email = "google@kinoiserie.test",
            EmailConfirmedAt = DateTime.UtcNow,
        };
        db.Users.Add(google);
        db.UserLogins.Add(new UserLogin { User = google, Provider = AuthProvider.Google, ProviderKey = "google-sub" });
        await db.SaveChangesAsync();
        var sender = new CapturingAuthEmailSender();
        var service = CreateService(db, sender);
        await service.RequestPasswordResetAsync("google@kinoiserie.test");

        await service.ResetPasswordAsync(sender.PasswordResets.Single().Token, "new-password", "kinshout-web");

        Assert.Contains(db.UserLogins, l => l.UserId == google.Id && l.Provider == AuthProvider.Local);
        Assert.Contains(db.UserLogins, l => l.UserId == google.Id && l.Provider == AuthProvider.Google);
        var login = await service.LoginWithEmailAsync(
            new EmailLoginRequestDto("google@kinoiserie.test", "new-password"),
            "kinshout-web");
        Assert.Equal(google.Id, login.User.Id);
    }

    [Fact]
    public async Task ResetPasswordAsync_StillSignsInWhenTheNoticeCannotBeSent()
    {
        await using var db = TestDbFactory.Create();
        await AddConfirmedLocalUserAsync(db, "notice@kinoiserie.test", "old-password");
        var capturing = new CapturingAuthEmailSender();
        await CreateService(db, capturing).RequestPasswordResetAsync("notice@kinoiserie.test");
        var sender = new Mock<IAuthEmailSender>();
        sender
            .Setup(s => s.SendPasswordChangedAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));

        var response = await CreateService(db, sender.Object)
            .ResetPasswordAsync(capturing.PasswordResets.Single().Token, "new-password", "kinshout-web");

        Assert.False(string.IsNullOrWhiteSpace(response.Token));
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

    private sealed class CapturingAuthEmailSender : IAuthEmailSender
    {
        public List<(User User, string Token)> Confirmations { get; } = [];
        public List<(User User, string Token)> PasswordResets { get; } = [];
        public List<User> PasswordChanges { get; } = [];

        public Task SendConfirmationAsync(User user, string token, CancellationToken ct = default)
        {
            Confirmations.Add((user, token));
            return Task.CompletedTask;
        }

        public Task SendPasswordResetAsync(User user, string token, CancellationToken ct = default)
        {
            PasswordResets.Add((user, token));
            return Task.CompletedTask;
        }

        public Task SendPasswordChangedAsync(User user, CancellationToken ct = default)
        {
            PasswordChanges.Add(user);
            return Task.CompletedTask;
        }
    }

    private static AuthService CreateService(KinshoutDbContext db, IAuthEmailSender? sender = null) =>
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
            sender ?? new CapturingAuthEmailSender(),
            Mock.Of<ILogger<AuthService>>());
}
