namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>
/// A post is buzzing when people are reacting to it.
/// Comments and shares count more than likes. View totals are not part of the score.
/// The same score is used for a video, a photo, and a text post.
/// </summary>
public static class BuzzGate
{
    public const int Floor = 25;

    public static int Score(int likes, int comments, int shares) =>
        Math.Max(0, likes) + (Math.Max(0, comments) * 3) + (Math.Max(0, shares) * 3);

    public static bool IsBuzzing(int likes, int comments, int shares, int floor) =>
        Score(likes, comments, shares) >= Math.Max(1, floor);

    public static bool IsBuzzing(int? score, int floor) =>
        score.GetValueOrDefault() >= Math.Max(1, floor);
}
