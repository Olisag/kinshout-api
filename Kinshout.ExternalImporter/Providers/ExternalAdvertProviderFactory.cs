using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Providers.Scraping;

namespace Kinshout.ExternalImporter.Providers;

public static class ExternalAdvertProviderFactory
{
    public static IExternalAdvertProvider Create(HttpClient http, ExternalProviderSettings settings) =>
        settings.Type.Trim().ToLowerInvariant() switch
        {
            "rss" or "atom" or "rss-feed" => new RssAdvertProvider(http, settings),
            "json" or "json-feed" or "approved-json-feed" or "facebook-approved-feed" => new JsonFeedAdvertProvider(http, settings),
            "scraper" or "web-scraper" => CreateScraper(http, settings),
            "mediacongo-scraper" => new MediaCongoScraperProvider(http, settings),
            "zwandako-scraper" => new ZwandakoScraperProvider(http, settings),
            "jiji-scraper" or "jiji-rdc-scraper" => new JijiRdcScraperProvider(http, settings),
            "facebook-scraper" or "facebook-marketplace-scraper" or "sociavault-facebook-scraper" => new FacebookMarketplaceScraperProvider(http, settings),
            "apify-facebook-scraper" or "apify-facebook-marketplace-scraper" => new ApifyFacebookMarketplaceScraperProvider(http, settings),
            _ => throw new InvalidOperationException($"Unsupported provider type '{settings.Type}' for provider '{settings.Name}'."),
        };

    private static IExternalAdvertProvider CreateScraper(HttpClient http, ExternalProviderSettings settings) =>
        settings.Provider.Trim().ToLowerInvariant() switch
        {
            "mediacongo" => new MediaCongoScraperProvider(http, settings),
            "zwandako" => new ZwandakoScraperProvider(http, settings),
            "jiji_rdc" or "jiji" => new JijiRdcScraperProvider(http, settings),
            "facebook_marketplace" or "facebook" => new FacebookMarketplaceScraperProvider(http, settings),
            _ => throw new InvalidOperationException(
                $"Unknown scraper provider '{settings.Provider}'. Use mediacongo, zwandako, jiji_rdc, or facebook_marketplace."),
        };
}
