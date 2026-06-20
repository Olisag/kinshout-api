using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class TimeHelpersTests
{
    [Fact]
    public void FormatRelative_RecentMinutes_UsesMinutesLabel()
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-15);
        var result = TimeHelpers.FormatRelative(createdAt);
        Assert.StartsWith("Il y a", result);
        Assert.Contains("min", result);
    }

    [Fact]
    public void FormatRelative_Yesterday_ReturnsHier()
    {
        var createdAt = DateTime.UtcNow.AddDays(-1).AddHours(-1);
        Assert.Equal("Hier", TimeHelpers.FormatRelative(createdAt));
    }

    [Theory]
    [InlineData("Jonathan M.", "JM")]
    [InlineData("Sarah", "SA")]
    [InlineData("A", "A")]
    public void Initials_ReturnsExpected(string name, string expected) =>
        Assert.Equal(expected, TimeHelpers.Initials(name));
}
