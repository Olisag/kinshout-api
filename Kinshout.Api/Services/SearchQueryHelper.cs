using System.Text.RegularExpressions;

namespace Kinshout.Api.Services;

public static partial class SearchQueryHelper
{
    public static string? Normalize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var collapsed = Whitespace().Replace(query.Trim(), " ");
        return collapsed.Length < 2 ? null : collapsed.ToLowerInvariant();
    }

    public static string Display(string query) =>
        Whitespace().Replace(query.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
