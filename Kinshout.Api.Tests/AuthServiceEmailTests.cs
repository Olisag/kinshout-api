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

        var user = Assert.Single(db.Users);
        Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash));
        Assert.Contains(db.UserLogins, l => l.Provider == AuthProvider.Local);
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

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.LoginWithEmailAsync(
                new EmailLoginRequestDto("login@kinoiserie.test", "wrong-password"),
                "kinshout-web"));
    }

    [Fact]
    public async Task RegisterWithEmailAsync_RejectsDuplicateEmail()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);
        await service.RegisterWithEmailAsync(
            new EmailRegisterRequestDto("dup@kinoiserie.test", "password123", null),
            "kinshout-web");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RegisterWithEmailAsync(
                new EmailRegisterRequestDto("dup@kinoiserie.test", "password456", null),
                "kinshout-web"));

        Assert.Contains("e-mail", ex.Message, StringComparison.OrdinalIgnoreCase);
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
