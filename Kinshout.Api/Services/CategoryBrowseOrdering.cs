namespace Kinshout.Api.Services;

internal static class CategoryBrowseOrdering
{
    public const string AutresSlug = "autres";

    /// <summary>Sort key for EF queries — autres last. Do not use static helpers; EF cannot translate them.</summary>
    public static int AutresLastSortKey(string slug) =>
        string.Equals(slug, AutresSlug, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}
