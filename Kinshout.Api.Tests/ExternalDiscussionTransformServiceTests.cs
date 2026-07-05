using Kinshout.Api.Configuration;
using Kinshout.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Tests;

public class ExternalDiscussionTransformServiceTests
{
    [Fact]
    public async Task TransformAsync_FallbackFormatsLeopardsPostAsTopicPlusQuestion()
    {
        const string raw = """
            🚨 FLASH 🔥 Trois Léopards sont aperçus aux portes de Kinshasa après l'élimination de la RDC en coupe du monde.
            Il s'agit de Arthur Masuaku, Brian Cipenga et Timothy Fayulu.
            #LeopardsRDC #FootballRDC
            """;

        var result = await CreateService().TransformAsync(raw, "Sports Page", "Facebook");

        Assert.DoesNotContain("🚨", result.Title);
        Assert.DoesNotContain("#", result.Title);
        Assert.DoesNotContain("?", result.Title);
        Assert.True(result.Body.TrimEnd().EndsWith('?'));
        Assert.Contains("Masuaku", result.Body, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public async Task TransformAsync_FallbackUsesExetatQuestionForExamPosts()
    {
        const string raw = """
            EXETAT 2026 | Publication des résultats. Les résultats de Kinshasa Lukunga seront disponibles aujourd'hui.
            """;

        var result = await CreateService().TransformAsync(raw, null, "Facebook");

        Assert.Contains("EXETAT", result.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("équitables", result.Body, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.UsedFallback);
    }

    private static ExternalDiscussionTransformService CreateService() =>
        new(
            new TestHttpClientFactory(),
            Options.Create(new OpenAiSettings { ApiKey = "", Model = "gpt-4o-mini" }),
            NullLogger<ExternalDiscussionTransformService>.Instance);

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
