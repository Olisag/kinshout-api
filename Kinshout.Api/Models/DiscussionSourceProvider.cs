namespace Kinshout.Api.Models;

public static class DiscussionSourceProvider
{
    public const string Kinshout = "kinshout";
    public const string Facebook = "facebook";
    public const string Twitter = "twitter";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> KnownProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Kinshout,
        Facebook,
        Twitter,
        Other,
    };

    public static string Normalize(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return Kinshout;

        var normalized = provider.Trim().ToLowerInvariant();
        return normalized switch
        {
            "x" or "twitter_x" => Twitter,
            _ => KnownProviders.Contains(normalized) ? normalized : Other,
        };
    }

    public static string DisplayName(string provider) =>
        provider switch
        {
            Facebook => "Facebook",
            Twitter => "X",
            Other => "Autre source",
            _ => "Kinshout",
        };
}
