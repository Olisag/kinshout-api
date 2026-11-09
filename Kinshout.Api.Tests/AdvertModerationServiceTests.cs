using Kinshout.Api.Configuration;
using Kinshout.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class AdvertModerationServiceTests
{
    private static AdvertModerationService CreateService(
        string apiKey = "",
        bool moderationEnabled = true,
        bool discussionModerationEnabled = false) =>
        new(
            new TestHttpClientFactory(),
            Options.Create(new OpenAiSettings
            {
                ApiKey = apiKey,
                ModerationEnabled = moderationEnabled,
                DiscussionModerationEnabled = discussionModerationEnabled,
            }),
            Mock.Of<ILogger<AdvertModerationService>>());

    [Fact]
    public async Task EnsureTextAllowedAsync_FallbackBlocksSexualKeywords()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<AdvertModerationException>(() =>
            service.EnsureTextAllowedAsync("Massage érotique à domicile"));
    }

    [Fact]
    public async Task EnsureTextAllowedAsync_FallbackAllowsNormalText()
    {
        var service = CreateService();
        await service.EnsureTextAllowedAsync("Appartement 2 chambres à Gombe, 500 $");
    }

    [Fact]
    public async Task EnsureTextAllowedAsync_FallbackBlocksDiscriminatoryKeywords()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<AdvertModerationException>(() =>
            service.EnsureTextAllowedAsync("Message de haine raciale contre un groupe"));
    }

    [Fact]
    public async Task EnsureImageAllowedAsync_WithoutApiKey_SkipsModeration()
    {
        var service = CreateService();
        await using var stream = new MemoryStream([0xFF, 0xD8, 0xFF, 0x00]);

        await service.EnsureImageAllowedAsync(stream, "image/jpeg");
    }

    [Fact]
    public async Task EnsureTextAllowedAsync_WhenModerationDisabled_AllowsBlockedKeywords()
    {
        var service = CreateService(moderationEnabled: false);
        await service.EnsureTextAllowedAsync("Massage érotique à domicile");
    }

    [Fact]
    public async Task EnsureDiscussionTextAllowedAsync_WhenDisabled_AllowsBlockedKeywords()
    {
        var service = CreateService(discussionModerationEnabled: false);
        await service.EnsureDiscussionTextAllowedAsync("Massage érotique à domicile");
    }

    [Fact]
    public async Task EnsureDiscussionTextAllowedAsync_WhenEnabled_BlocksSexualKeywords()
    {
        var service = CreateService(discussionModerationEnabled: true);

        await Assert.ThrowsAsync<AdvertModerationException>(() =>
            service.EnsureDiscussionTextAllowedAsync("Massage érotique à domicile"));
    }

    [Fact]
    public async Task EnsureTextAllowedAsync_StaysEnabled_WhenDiscussionModerationIsOff()
    {
        var service = CreateService(moderationEnabled: true, discussionModerationEnabled: false);

        await Assert.ThrowsAsync<AdvertModerationException>(() =>
            service.EnsureTextAllowedAsync("Massage érotique à domicile"));
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
