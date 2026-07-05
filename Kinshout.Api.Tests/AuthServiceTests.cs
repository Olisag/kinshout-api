using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class AuthServiceTests
{
    private const string ApiBase = "https://api.test";

    [Fact]
    public async Task UpdateProfileAsync_SavesNormalizedWhatsApp()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db, withWhatsApp: false);

        var service = CreateService(db);

        var profile = await service.UpdateProfileAsync(
            user.Id,
            new UpdateProfileRequestDto("900 000 222"));

        Assert.Equal("+243900000222", profile.WhatsAppNumber);
        Assert.True(profile.HasWhatsApp);
    }

    [Fact]
    public async Task GetProfileAsync_ReflectsWhatsApp()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var service = CreateService(db);

        var profile = await service.GetProfileAsync(user.Id);

        Assert.NotNull(profile);
        Assert.True(profile!.HasWhatsApp);
        Assert.Equal("+243900000001", profile.WhatsAppNumber);
        Assert.Equal("Test User", profile.DisplayName);
    }

    [Fact]
    public async Task UpdateDisplayPreferenceAsync_SavesMode()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var service = CreateService(db);

        var preference = await service.UpdateDisplayPreferenceAsync(
            user.Id,
            new UpdateDisplayPreferenceRequestDto("sombre"));

        Assert.Equal("sombre", preference.Mode);

        var profile = await service.GetProfileAsync(user.Id);
        Assert.NotNull(profile);
        Assert.Equal("sombre", profile!.DisplayPreference);
    }

    [Fact]
    public async Task UpdateDisplayPreferenceAsync_RejectsInvalidMode()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateDisplayPreferenceAsync(
                user.Id,
                new UpdateDisplayPreferenceRequestDto("neon")));
    }

    [Fact]
    public async Task UpdateProfileVisibilityAsync_SavesSetting()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var service = CreateService(db);
        var visibility = await service.UpdateProfileVisibilityAsync(
            user.Id,
            new UpdateProfileVisibilityRequestDto(true));

        Assert.True(visibility.IsPublic);

        var profile = await service.GetProfileAsync(user.Id);
        Assert.NotNull(profile);
        Assert.True(profile!.IsProfilePublic);
    }

    [Fact]
    public async Task UpdateDisplayNameAsync_SavesTrimmedDisplayName()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var service = CreateService(db);
        var profile = await service.UpdateDisplayNameAsync(
            user.Id,
            new UpdateDisplayNameRequestDto("  Marie K.  "));

        Assert.Equal("Marie K.", profile.DisplayName);
    }

    [Fact]
    public async Task UpdateDisplayNameAsync_RejectsTakenDisplayName()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        db.Users.Add(new User
        {
            Email = "other@test.com",
            DisplayName = "Marie K.",
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateDisplayNameAsync(user.Id, new UpdateDisplayNameRequestDto("marie k.")));

        Assert.Contains("déjà pris", ex.Message);
    }

    [Fact]
    public async Task UpdateDisplayNameAsync_AllowsKeepingSameDisplayName()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var service = CreateService(db);
        var profile = await service.UpdateDisplayNameAsync(
            user.Id,
            new UpdateDisplayNameRequestDto("Test User"));

        Assert.Equal("Test User", profile.DisplayName);
    }

    [Fact]
    public async Task SetAvatarUrlAsync_SavesPathAndReturnsAbsoluteUrl()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var avatarPath = $"/uploads/avatars/{user.Id:N}/abc123.png";

        var service = CreateService(db);
        var profile = await service.SetAvatarUrlAsync(user.Id, avatarPath);

        Assert.Equal($"{ApiBase}{avatarPath}", profile.AvatarUrl);

        var saved = await db.Users.FindAsync(user.Id);
        Assert.Equal(avatarPath, saved!.AvatarUrl);
    }

    [Fact]
    public async Task GetProfileAsync_ReturnsAbsoluteOAuthAvatarUrl()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        user.AvatarUrl = "https://cdn.example/avatar.jpg";
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var profile = await service.GetProfileAsync(user.Id);

        Assert.Equal("https://cdn.example/avatar.jpg", profile!.AvatarUrl);
    }

    [Fact]
    public async Task SetAvatarUrlAsync_RejectsForeignUrl()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SetAvatarUrlAsync(user.Id, "/uploads/images/other/file.jpg"));
    }

    [Fact]
    public async Task ClearAvatarAsync_ClearsProfileAvatar()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        user.AvatarUrl = $"/uploads/avatars/{user.Id:N}/old.png";
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var profile = await service.ClearAvatarAsync(user.Id);

        Assert.Null(profile.AvatarUrl);
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
            CreateUploadUrlResolver(),
            Options.Create(new OAuthSettings()),
            Mock.Of<IFacebookAuthValidator>(),
            Mock.Of<ILogger<AuthService>>());

    private static UploadUrlResolver CreateUploadUrlResolver() =>
        new(
            Options.Create(new UploadStorageSettings { PublicBaseUrl = ApiBase }),
            Mock.Of<IHttpContextAccessor>());
}
