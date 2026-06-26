using Kinshout.Api.Configuration;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Tests;

public class OpenAiServiceFallbackTests
{
    [Theory]
    [InlineData("Je souhaite vendre ma voiture", "vehicules_transport", "offre")]
    [InlineData("Appartement à louer à Gombe", "immobilier", "offre")]
    [InlineData("Je cherche un iPhone 13 pas cher", "electronique", "demande")]
    [InlineData("Recrute chauffeur VTC", "emploi_services", "demande")]
    [InlineData("Nazali koteka motuka ya Toyota", "vehicules_transport", "offre")]
    [InlineData("Nalingi ndako na Gombe", "immobilier", "demande")]
    [InlineData("Looking for iPhone 14 in Kinshasa", "electronique", "demande")]
    [InlineData("Apartment for rent in Limete", "immobilier", "offre")]
    public async Task AnalyzeAdvertAsync_WithoutApiKey_UsesKeywordRules(
        string text,
        string expectedSlug,
        string expectedIntent)
    {
        var service = CreateServiceWithoutApiKey();
        var categories = SeedCategories();

        var result = await service.AnalyzeAdvertAsync(text, categories);

        Assert.Equal(expectedSlug, result.CategorySlug);
        Assert.Equal(expectedIntent, result.Intent);
    }

    private static OpenAiService CreateServiceWithoutApiKey() =>
        new(
            new TestHttpClientFactory(),
            Options.Create(new OpenAiSettings { ApiKey = "", Model = "gpt-4o-mini" }),
            NullLogger<OpenAiService>.Instance);

    private static List<Category> SeedCategories() =>
    [
        new() { Slug = "immobilier", Label = "Appartements à louer", Icon = "⌂", IsSystem = true },
        new() { Slug = "vehicules_transport", Label = "Véhicules", Icon = "🚗", IsSystem = true },
        new() { Slug = "emploi_services", Label = "Emplois", Icon = "▣", IsSystem = true },
        new() { Slug = "electronique", Label = "Électroniques", Icon = "▯", IsSystem = true },
        new() { Slug = "maison_jardin", Label = "Services", Icon = "⌘", IsSystem = true },
        new() { Slug = "discussion", Label = "Discussions", Icon = "💬", IsSystem = true },
    ];

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
