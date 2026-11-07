namespace Kinshout.Api.Services;

/// <summary>
/// Culture tops are filled by import and moderators. Members can read, react, and comment. They cannot start a post.
/// </summary>
public static class ReadOnlyCommunityCatalog
{
    private static readonly HashSet<string> Slugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "musique",
        "culture",
        "humour",
        "influence",
        "mode",
        "sport",
        "foi",
        "classiques",
        "diaspora-fr",
        "diaspora-us",
        "diaspora-ca",
        "diaspora-za",
    };

    public static bool IsReadOnly(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && Slugs.Contains(slug.Trim());

    public static bool IsKnownLaunchRoom(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return false;

        var normalized = slug.Trim();
        return IsReadOnly(normalized)
            || normalized.Equals("general", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ville", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("education", StringComparison.OrdinalIgnoreCase);
    }
}
