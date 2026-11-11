using Kinshout.Api.Models;
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

    internal static string SeededCommunitySlug(Guid runId, string theme) => $"{theme}-{runId.ToString("N")[..8]}";

    internal static IQueryable<Community> BySlug(IQueryable<Community> communities, string slug)
    {
        if (!TryParseLegacySlug(slug, out var runId, out var theme))
            return communities.Where(c => c.Slug == slug);

        var current = SeededCommunitySlug(runId, theme);
        return communities.Where(c => c.Slug == slug || (c.Slug == current && c.CreatedByUser.SeedRunId == runId))
            .OrderByDescending(c => c.Slug == slug);
    }

    internal static bool TryParseLegacySlug(string slug, out Guid runId, out string theme)
    {
        runId = default;
        theme = string.Empty;
        const string prefix = "simulation-";
        var separator = prefix.Length + 32;
        if (slug.Length <= separator + 1 || !slug.StartsWith(prefix, StringComparison.Ordinal)
            || slug[separator] != '-' || !Guid.TryParseExact(slug.AsSpan(prefix.Length, 32), "N", out runId))
            return false;

        theme = slug[(separator + 1)..];
        return true;
    }

    public static string ToRouteSlug(string slug) => $"{RoutePrefix}{slug}";

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();
}
