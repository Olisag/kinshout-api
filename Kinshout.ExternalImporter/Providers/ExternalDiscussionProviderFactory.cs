using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Providers.Scraping;

namespace Kinshout.ExternalImporter.Providers;

public static class ExternalDiscussionProviderFactory
{
    public static IExternalDiscussionProvider Create(HttpClient http, ExternalProviderSettings settings) =>
        settings.Type.Trim().ToLowerInvariant() switch
        {
            "apify-facebook-posts-scraper" or "facebook-posts-scraper" => new ApifyFacebookPostsScraperProvider(http, settings),
            "apify-twitter-posts-scraper" or "apify-x-posts-scraper" or "twitter-posts-scraper" or "x-posts-scraper" => new ApifyTwitterPostsScraperProvider(http, settings),
            _ => throw new InvalidOperationException($"Unsupported discussion provider type '{settings.Type}' for provider '{settings.Name}'."),
        };
}
