namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>
/// Growth is the active profile. Aggressive is not authorized and stays on Growth limits.
/// </summary>
internal static class FeedOperatingProfile
{
    public const string Growth = "Growth";
    public const string Economy = "Economy";

    public static string Normalize(string? profile)
    {
        if (string.Equals(profile, Economy, StringComparison.OrdinalIgnoreCase))
            return Economy;

        return Growth;
    }

    public static int SearchSlots(string platform, int configured, string? profile)
    {
        var active = Normalize(profile);
        if (configured < 1)
            return 0;

        if (active == Growth && platform.Equals("facebook", StringComparison.OrdinalIgnoreCase))
            return Math.Min(configured, 2);

        return configured;
    }

    public static int ResultCap(string platform, int configured, string? profile)
    {
        if (configured < 1)
            return configured;

        if (Normalize(profile) != Growth)
            return configured;

        if (platform.Equals("tiktok", StringComparison.OrdinalIgnoreCase))
            return Math.Min(configured, 8);

        if (platform.Equals("twitter", StringComparison.OrdinalIgnoreCase)
            || platform.Equals("x", StringComparison.OrdinalIgnoreCase))
            return Math.Min(configured, 15);

        return configured;
    }
}
