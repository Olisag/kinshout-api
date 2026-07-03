using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class UsernameServiceTests
{
    [Fact]
    public void Normalize_LowercasesAndTrims()
    {
        var service = new UsernameService(TestDbFactory.Create());
        Assert.Equal("cool_panda", service.Normalize("  Cool_Panda  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData("has space")]
    [InlineData("toolongusernamethatexceeds")]
    public void ValidateFormat_RejectsInvalid(string username)
    {
        var service = new UsernameService(TestDbFactory.Create());
        Assert.Throws<ArgumentException>(() => service.ValidateFormat(username));
    }

    [Fact]
    public void ValidateFormat_AllowsUserChosenFormats()
    {
        var service = new UsernameService(TestDbFactory.Create());

        service.ValidateFormat("Marie.K");
        service.ValidateFormat("jean-paul");
        service.ValidateFormat("User123");
        service.ValidateFormat("🙂nick");
    }

    [Fact]
    public async Task IsAvailableAsync_ReturnsFalseWhenTaken()
    {
        await using var db = TestDbFactory.Create();
        db.Users.Add(new User
        {
            Email = "a@test.com",
            Username = "taken_name",
            DisplayName = "A",
        });
        await db.SaveChangesAsync();

        var service = new UsernameService(db);
        Assert.False(await service.IsAvailableAsync("taken_name"));
        Assert.False(await service.IsAvailableAsync("Taken_Name"));
        Assert.True(await service.IsAvailableAsync("free_name"));
    }

    [Fact]
    public async Task GenerateUniqueAsync_ReturnsDistinctUsernames()
    {
        await using var db = TestDbFactory.Create();
        var service = new UsernameService(db);

        var first = await service.GenerateUniqueAsync();
        var second = await service.GenerateUniqueAsync();

        Assert.NotEqual(first, second);
        Assert.Matches("^[a-z][a-z0-9_]{2,19}$", first);
    }

    [Fact]
    public async Task EnsureAllUsersHaveUsernamesAsync_BackfillsMissing()
    {
        await using var db = TestDbFactory.Create();
        db.Users.Add(new User
        {
            Email = "empty@test.com",
            DisplayName = "Empty",
            Username = "",
        });
        await db.SaveChangesAsync();

        var service = new UsernameService(db);
        await service.EnsureAllUsersHaveUsernamesAsync();

        var user = db.Users.Single();
        Assert.False(string.IsNullOrWhiteSpace(user.Username));
    }
}
