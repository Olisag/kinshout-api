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
            "apify-facebook-scraper" or "apify-facebook-marketplace-scraper" or "facebook-scraper" or "facebook-marketplace-scraper" => new ApifyFacebookMarketplaceScraperProvider(http, settings),
            "apify-linkedin-jobs-scraper" or "linkedin-jobs-scraper" => new ApifyLinkedInJobsScraperProvider(http, settings),
            _ => throw new InvalidOperationException($"Unsupported provider type '{settings.Type}' for provider '{settings.Name}'."),
        };

    private static IExternalAdvertProvider CreateScraper(HttpClient http, ExternalProviderSettings settings) =>
        settings.Provider.Trim().ToLowerInvariant() switch
        {
            "mediacongo" => new MediaCongoScraperProvider(http, settings),
            "zwandako" => new ZwandakoScraperProvider(http, settings),
            "jiji_rdc" or "jiji" => new JijiRdcScraperProvider(http, settings),
            "facebook_marketplace" or "facebook" => new ApifyFacebookMarketplaceScraperProvider(http, settings),
            "linkedin_jobs" or "linkedin" => new ApifyLinkedInJobsScraperProvider(http, settings),
            _ => throw new InvalidOperationException(
                $"Unknown scraper provider '{settings.Provider}'. Use mediacongo, zwandako, jiji_rdc, facebook_marketplace, or linkedin_jobs."),
        };
}
