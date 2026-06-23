namespace Kinshout.Api.Models;

public static class DisplayPreferenceMode
{
    public const string Clair = "clair";
    public const string Sombre = "sombre";
    public const string Default = Clair;

    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return Default;

        return mode.Trim().ToLowerInvariant() switch
        {
            Clair => Clair,
            Sombre => Sombre,
            _ => throw new ArgumentException("Le mode d'affichage doit être « clair » ou « sombre »."),
        };
    }
}
