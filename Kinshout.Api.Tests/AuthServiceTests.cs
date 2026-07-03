using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class AuthServiceTests
{
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
        Assert.Equal("test_user", profile.Username);
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
    public async Task UpdateUsernameAsync_SavesNormalizedUsername()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var service = CreateService(db);
        var profile = await service.UpdateUsernameAsync(
            user.Id,
            new UpdateUsernameRequestDto("Marie.K"));

        Assert.Equal("marie.k", profile.Username);
    }

    [Fact]
    public async Task UpdateUsernameAsync_RejectsTakenUsername()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        db.Users.Add(new User
        {
            Email = "other@test.com",
            Username = "taken_name",
            DisplayName = "Other",
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateUsernameAsync(user.Id, new UpdateUsernameRequestDto("taken_name")));

        Assert.Contains("déjà pris", ex.Message);
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
            new UsernameService(db),
            Options.Create(new OAuthSettings()),
            Mock.Of<IFacebookAuthValidator>(),
            Mock.Of<ILogger<AuthService>>());
}
