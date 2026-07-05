namespace Kinshout.Api.Services;

internal static class CategoryBrowseOrdering
{
    /// <summary>0 = normal categories first, 1 = autres always last.</summary>
    public static int AutresLastKey(string slug) =>
        string.Equals(slug, "autres", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}
