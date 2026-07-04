namespace Kinshout.Api.Models;

public static class AdvertSourceProvider
{
    public const string Kinshout = "kinshout";
    public const string FacebookMarketplace = "facebook_marketplace";
    public const string MediaCongo = "mediacongo";
    public const string Zwandako = "zwandako";
    public const string JijiRdc = "jiji_rdc";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> KnownProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Kinshout,
        FacebookMarketplace,
        MediaCongo,
        Zwandako,
        JijiRdc,
        Other,
    };

    public static string Normalize(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return Kinshout;

        var normalized = provider.Trim().ToLowerInvariant();
        return KnownProviders.Contains(normalized) ? normalized : Other;
    }

    public static string DisplayName(string provider) =>
        provider switch
        {
            FacebookMarketplace => "Facebook Marketplace",
            MediaCongo => "MediaCongo",
            Zwandako => "Zwandako",
            JijiRdc => "Jiji RDC",
            Other => "Autre source",
            _ => "Kinshout",
        };
}
