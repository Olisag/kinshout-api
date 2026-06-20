using Kinshout.Api.Configuration;
using Kinshout.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class AdvertModerationServiceTests
{
    private static AdvertModerationService CreateService(string apiKey = "") =>
        new(
            new TestHttpClientFactory(),
            Options.Create(new OpenAiSettings { ApiKey = apiKey }),
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
    public async Task EnsureImageAllowedAsync_WithoutApiKey_Throws()
    {
        var service = CreateService();
        await using var stream = new MemoryStream([0xFF, 0xD8, 0xFF, 0x00]);

        var ex = await Assert.ThrowsAsync<AdvertModerationException>(() =>
            service.EnsureImageAllowedAsync(stream, "image/jpeg"));

        Assert.Contains("OpenAI", ex.Message);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
