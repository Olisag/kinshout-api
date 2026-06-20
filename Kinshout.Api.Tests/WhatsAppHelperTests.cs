using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class WhatsAppHelperTests
{
    [Theory]
    [InlineData("+243 900 000 001", "+243900000001")]
    [InlineData("900000001", "+243900000001")]
    [InlineData("243900000001", "+243900000001")]
    [InlineData("+1 555 123 4567", "+15551234567")]
    public void Normalize_ValidNumbers_ReturnsE164(string input, string expected) =>
        Assert.Equal(expected, WhatsAppHelper.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    public void Normalize_InvalidNumbers_Throws(string input) =>
        Assert.Throws<ArgumentException>(() => WhatsAppHelper.Normalize(input));
}
