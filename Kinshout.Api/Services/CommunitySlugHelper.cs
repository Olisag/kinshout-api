using System.Text.RegularExpressions;

namespace Kinshout.Api.Services;

public static partial class CommunitySlugHelper
{
    public const string RoutePrefix = "k/";

    /// <summary>
    /// Normalizes <c>k/community1</c> or <c>community1</c> to stored slug <c>community1</c>.
    /// Rejects spaces and invalid characters.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Le slug de la communauté est requis.");

        var value = raw.Trim().ToLowerInvariant();
        if (value.StartsWith(RoutePrefix, StringComparison.Ordinal))
            value = value[RoutePrefix.Length..];

        value = value.Trim().Trim('/');
        if (value.Length == 0)
            throw new ArgumentException("Le slug de la communauté est requis.");

        if (value.Contains(' ') || value.Contains('\t'))
            throw new ArgumentException("Le slug ne peut pas contenir d'espaces. Utilisez des tirets (ex: k/ma-communaute).");

        if (!SlugRegex().IsMatch(value))
            throw new ArgumentException(
                "Slug invalide. Utilisez uniquement a-z, 0-9 et tirets (ex: k/community1).");

        if (value.Length > 64)
            throw new ArgumentException("Le slug est trop long (max 64 caractères).");

        return value;
    }

    public static string ToRouteSlug(string slug) => $"{RoutePrefix}{slug}";

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();
}
