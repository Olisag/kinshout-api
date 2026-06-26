using Kinshout.Api.Dtos;

namespace Kinshout.Api.Services;

public static class PagingHelper
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    public static (int Page, int PageSize) Normalize(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize));

    public static PagedResultDto<T> Create<T>(
        IReadOnlyList<T> items,
        int page,
        int pageSize,
        int totalCount) =>
        new(items, page, pageSize, totalCount, page * pageSize < totalCount);
}

public static class ListSortHelper
{
    public const string Popular = "popular";
    public const string Recent = "recent";

    public static bool IsPopular(string? sort) =>
        sort?.Equals(Popular, StringComparison.OrdinalIgnoreCase) == true;

    public static bool TryNormalize(string? sort, out string normalized)
    {
        normalized = (sort ?? Recent).Trim().ToLowerInvariant();
        return normalized is Recent or Popular;
    }
}

public static class SearchIntentHelper
{
    public const string Demande = "demande";
    public const string Offre = "offre";
    public const string Discussion = "discussion";

    public static bool TryNormalize(string? intent, out string? normalized)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            normalized = null;
            return true;
        }

        normalized = intent.Trim().ToLowerInvariant();
        return normalized is Demande or Offre or Discussion;
    }
}

public static class DiscussionMineFilterHelper
{
    public const string All = "all";
    public const string Authored = "authored";
    public const string Replies = "replies";

    public static string Normalize(string? filter) =>
        filter?.ToLowerInvariant() switch
        {
            Authored => Authored,
            Replies => Replies,
            _ => All,
        };
}
