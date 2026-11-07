using Kinshout.Api.Configuration;
using Kinshout.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Tests;

public class ExternalDiscussionTransformServiceTests
{
    [Fact]
    public async Task TransformAsync_FallbackFormatsLeopardsPostWithoutForcedQuestion()
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
        Assert.False(result.Body.TrimEnd().EndsWith('?'));
        Assert.DoesNotContain("Qu'en pensez-vous", result.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Masuaku", result.Body, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public async Task TransformAsync_FallbackKeepsYouTubeWatchUrlInBody()
    {
        const string raw = """
            Nouveau clip rumba tourné à Kinshasa ce week-end.
            https://www.youtube.com/watch?v=dQw4w9WgXcQ
            https://www.youtube.com/
            #RumbaKin
            """;

        var result = await CreateService().TransformAsync(raw, "Culture Page", "Facebook");

        Assert.Contains("https://www.youtube.com/watch?v=dQw4w9WgXcQ", result.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://www.youtube.com/\n", result.Body + "\n", StringComparison.OrdinalIgnoreCase);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public async Task TransformAsync_FallbackKeepsExetatContextWithoutCannedQuestion()
    {
        const string raw = """
            EXETAT 2026 | Publication des résultats. Les résultats de Kinshasa Lukunga seront disponibles aujourd'hui.
            """;

        var result = await CreateService().TransformAsync(raw, null, "Facebook");

        Assert.Contains("EXETAT", result.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Lukunga", result.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("équitables", result.Body, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public async Task TransformAsync_UsesEachVideoTitleWhenTheDescriptionIsShared()
    {
        const string body = "Devenez partenaire de Moise Mbiye Ministries. Contactez le secretariat.";
        var titles = new[]
        {
            "BIENVENUE À NOTRE CULTE D'INTERCESSION MONDIALE/ JEUDI 05.11.2026/ AVEC LE PASTEUR JOSE KABENGELE",
            "CULTE DES MIRACLES & DELIVRANCE/MERCREDI 04 NOV 2026/SESSION DU SOIR.",
            "CULTE DE DELIVRANCE ET MIRACLE/MERCREDI 04.11.2026/VACATION MATIN.",
            "BIENVENUE À NOTRE TROISIEME CULTE DOMICAL /DIMANCHE 01.11.2026/ AVEC LE PASTEUR MOISE MBIYE",
        };

        var results = new List<string>();
        foreach (var title in titles)
        {
            var result = await CreateService().TransformAsync($"Titre: {title}\n\n{body}", "Moise Mbiye Officiel", "YouTube");
            Assert.DoesNotContain("partenariat", result.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("?", result.Title);
            Assert.True(result.Title.Length <= 90);
            results.Add(result.Title);
        }

        Assert.Contains("intercession", results[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("05.11.2026", results[0], StringComparison.Ordinal);
        Assert.Contains("miracles", results[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("soir", results[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("04.11.2026", results[2], StringComparison.Ordinal);
        Assert.Contains("matin", results[2], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("domical", results[3], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("01.11.2026", results[3], StringComparison.Ordinal);
        Assert.Equal(4, results.Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
