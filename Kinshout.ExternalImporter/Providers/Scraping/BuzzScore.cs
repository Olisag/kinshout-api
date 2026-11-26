namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>
/// Separate 0–100 score. It does not replace a platform's eligibility floor.
/// Missing signals are left out of the weight, not counted as zero.
/// Simulated reactions are not an input.
/// </summary>
internal static class BuzzScore
{
    public static int Compute(
        bool relevant,
        int? likes,
        int? comments,
        int? shares,
        int? views,
        DateTime? publishedAt,
        DateTime utcNow,
        bool? novel = null)
    {
        var parts = new List<(double Weight, double Value)>();
        parts.Add((30, relevant ? 1 : 0));

        var engagement = Engagement(likes, comments, shares);
        if (engagement is { } direct)
            parts.Add((25, direct));

        if (publishedAt is { } published)
            parts.Add((20, Freshness(published, utcNow)));

        if (views is > 0 && engagement is not null)
        {
            var interactions = (likes ?? 0) + (comments ?? 0) + (shares ?? 0);
            var rate = Math.Clamp(interactions / (double)views.Value / 0.05, 0, 1);
            parts.Add((15, rate));
        }

        if (novel is { } isNovel)
            parts.Add((10, isNovel ? 1 : 0.3));

        var weight = parts.Sum(part => part.Weight);
        if (weight <= 0)
            return 0;

        var score = parts.Sum(part => part.Weight * part.Value) / weight * 100;
        return (int)Math.Round(Math.Clamp(score, 0, 100), MidpointRounding.AwayFromZero);
    }

    private static double? Engagement(int? likes, int? comments, int? shares)
    {
        if (likes is null && comments is null && shares is null)
            return null;

        var raw = (likes ?? 0) + 3 * (comments ?? 0) + 3 * (shares ?? 0);
        return Math.Clamp(raw / 500d, 0, 1);
    }

    private static double Freshness(DateTime publishedAt, DateTime utcNow)
    {
        var age = utcNow - publishedAt.ToUniversalTime();
        if (age <= TimeSpan.FromHours(48))
            return 1;
        if (age <= TimeSpan.FromDays(5))
            return 0.6;
        return 0.2;
    }
}
