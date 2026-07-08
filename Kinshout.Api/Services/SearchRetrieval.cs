using System.Data;
using System.Data.Common;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Services;

public static class SearchRetrieval
{
    public const int RetrieveCap = 500;
    public const int OpenAiCandidateLimit = 75;
    private const string FullTextAvailabilityCacheKey = "search:fulltext-available";
    private static readonly TimeSpan FullTextAvailabilityCacheDuration = TimeSpan.FromHours(1);

    public static async Task<List<Advert>> LoadSemanticAdvertsAsync(
        KinshoutDbContext db,
        IQueryable<Advert> baseQuery,
        string query,
        IMemoryCache? cache,
        CancellationToken ct)
    {
        var terms = SearchMatchHelper.ExtractTerms(query);
        var filtered = ApplyAdvertTextFilter(db, baseQuery, terms);

        if (await IsFullTextAvailableAsync(db, cache, ct))
        {
            try
            {
                var fullTextIds = await LoadAdvertIdsByFullTextAsync(db, filtered, query, terms, ct);
                if (fullTextIds.Count > 0)
                    return await LoadAdvertsByIdsAsync(db, fullTextIds, ct);
            }
            catch
            {
                // Full-text can fail on edge-case queries; fall back to local rank.
            }
        }

        return await LoadAdvertsWithLocalRankAsync(filtered, query, ct);
    }

    public static async Task<List<Discussion>> LoadSemanticDiscussionsAsync(
        KinshoutDbContext db,
        IQueryable<Discussion> baseQuery,
        string query,
        IMemoryCache? cache,
        CancellationToken ct)
    {
        var terms = SearchMatchHelper.ExtractTerms(query);
        var filtered = ApplyDiscussionTextFilter(db, baseQuery, terms, query);

        if (await IsFullTextAvailableAsync(db, cache, ct))
        {
            try
            {
                var fullTextIds = await LoadDiscussionIdsByFullTextAsync(db, filtered, query, terms, ct);
                if (fullTextIds.Count > 0)
                    return await LoadDiscussionsByIdsAsync(db, fullTextIds, ct);
            }
            catch
            {
                // Full-text can fail on edge-case queries; fall back to local rank.
            }
        }

        return await LoadDiscussionsWithLocalRankAsync(filtered, query, ct);
    }

    public static IQueryable<Advert> ApplyAdvertTextFilter(
        KinshoutDbContext db,
        IQueryable<Advert> query,
        IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return query;

        if (!SearchDbTextFilter.SupportsAccentInsensitiveSql(db))
        {
            if (terms.Count == 1)
                return SearchDbTextFilter.WhereAdvertTextContains(query, db, terms[0]);

            return ApplyAdvertTextFilterFallback(query, terms);
        }

        if (terms.Count == 1)
            return SearchDbTextFilter.WhereAdvertTextContains(query, db, terms[0]);

        const string collation = "Latin1_General_CI_AI";
        var t0 = terms[0].ToLowerInvariant();
        var t1 = terms[1].ToLowerInvariant();
        if (terms.Count == 2)
        {
            return query.Where(a =>
                EF.Functions.Collate(a.Title, collation).Contains(t0)
                || EF.Functions.Collate(a.Description, collation).Contains(t0)
                || (a.Location != null && EF.Functions.Collate(a.Location, collation).Contains(t0))
                || (a.TagsJson != null && EF.Functions.Collate(a.TagsJson, collation).Contains(t0))
                || (a.SubcategorySlug != null && EF.Functions.Collate(a.SubcategorySlug, collation).Contains(t0))
                || EF.Functions.Collate(a.Title, collation).Contains(t1)
                || EF.Functions.Collate(a.Description, collation).Contains(t1)
                || (a.Location != null && EF.Functions.Collate(a.Location, collation).Contains(t1))
                || (a.TagsJson != null && EF.Functions.Collate(a.TagsJson, collation).Contains(t1))
                || (a.SubcategorySlug != null && EF.Functions.Collate(a.SubcategorySlug, collation).Contains(t1)));
        }

        var t2 = terms[2].ToLowerInvariant();
        if (terms.Count == 3)
        {
            return query.Where(a =>
                EF.Functions.Collate(a.Title, collation).Contains(t0)
                || EF.Functions.Collate(a.Description, collation).Contains(t0)
                || (a.Location != null && EF.Functions.Collate(a.Location, collation).Contains(t0))
                || EF.Functions.Collate(a.Title, collation).Contains(t1)
                || EF.Functions.Collate(a.Description, collation).Contains(t1)
                || (a.Location != null && EF.Functions.Collate(a.Location, collation).Contains(t1))
                || EF.Functions.Collate(a.Title, collation).Contains(t2)
                || EF.Functions.Collate(a.Description, collation).Contains(t2)
                || (a.Location != null && EF.Functions.Collate(a.Location, collation).Contains(t2)));
        }

        var t3 = terms[3].ToLowerInvariant();
        return query.Where(a =>
            EF.Functions.Collate(a.Title, collation).Contains(t0)
            || EF.Functions.Collate(a.Description, collation).Contains(t0)
            || EF.Functions.Collate(a.Title, collation).Contains(t1)
            || EF.Functions.Collate(a.Description, collation).Contains(t1)
            || EF.Functions.Collate(a.Title, collation).Contains(t2)
            || EF.Functions.Collate(a.Description, collation).Contains(t2)
            || EF.Functions.Collate(a.Title, collation).Contains(t3)
            || EF.Functions.Collate(a.Description, collation).Contains(t3));
    }

    public static IQueryable<Discussion> ApplyDiscussionTextFilter(
        KinshoutDbContext db,
        IQueryable<Discussion> query,
        IReadOnlyList<string> terms,
        string? originalQuery = null)
    {
        if (!SearchDbTextFilter.SupportsAccentInsensitiveSql(db))
            return query;

        var subject = SearchQueryParser.Parse(originalQuery).SubjectText;
        if (string.IsNullOrWhiteSpace(subject))
            subject = originalQuery ?? string.Empty;

        var requiredTerms = SearchTermExpander.ExtractRawTerms(subject);
        if (requiredTerms.Count == 0 && !string.IsNullOrWhiteSpace(originalQuery))
            requiredTerms = SearchTermExpander.ExtractRawTerms(originalQuery);
        if (requiredTerms.Count >= 2)
            return ApplyDiscussionAndFilter(db, query, requiredTerms);

        if (terms.Count == 0)
            return query;

        if (terms.Count == 1)
            return SearchDbTextFilter.WhereTitleOrBodyContains(query, db, terms[0]);

        const string collation = "Latin1_General_CI_AI";
        var t0 = terms[0].ToLowerInvariant();
        var t1 = terms[1].ToLowerInvariant();
        if (terms.Count == 2)
        {
            return query.Where(d =>
                EF.Functions.Collate(d.Title, collation).Contains(t0)
                || EF.Functions.Collate(d.Body, collation).Contains(t0)
                || EF.Functions.Collate(d.Title, collation).Contains(t1)
                || EF.Functions.Collate(d.Body, collation).Contains(t1));
        }

        var t2 = terms[2].ToLowerInvariant();
        if (terms.Count == 3)
        {
            return query.Where(d =>
                EF.Functions.Collate(d.Title, collation).Contains(t0)
                || EF.Functions.Collate(d.Body, collation).Contains(t0)
                || EF.Functions.Collate(d.Title, collation).Contains(t1)
                || EF.Functions.Collate(d.Body, collation).Contains(t1)
                || EF.Functions.Collate(d.Title, collation).Contains(t2)
                || EF.Functions.Collate(d.Body, collation).Contains(t2));
        }

        var t3 = terms[3].ToLowerInvariant();
        return query.Where(d =>
            EF.Functions.Collate(d.Title, collation).Contains(t0)
            || EF.Functions.Collate(d.Body, collation).Contains(t0)
            || EF.Functions.Collate(d.Title, collation).Contains(t1)
            || EF.Functions.Collate(d.Body, collation).Contains(t1)
            || EF.Functions.Collate(d.Title, collation).Contains(t2)
            || EF.Functions.Collate(d.Body, collation).Contains(t2)
            || EF.Functions.Collate(d.Title, collation).Contains(t3)
            || EF.Functions.Collate(d.Body, collation).Contains(t3));
    }

    private static IQueryable<Discussion> ApplyDiscussionAndFilter(
        KinshoutDbContext db,
        IQueryable<Discussion> query,
        IReadOnlyList<string> requiredTerms)
    {
        foreach (var term in requiredTerms.Take(4))
            query = SearchDbTextFilter.WhereTitleOrBodyContains(query, db, term);

        return query;
    }

    private static IQueryable<Advert> ApplyAdvertTextFilterFallback(
        IQueryable<Advert> query,
        IReadOnlyList<string> terms)
    {
        if (terms.Count == 1)
        {
            var term = terms[0].ToLowerInvariant();
            return query.Where(a =>
                a.Title.ToLower().Contains(term)
                || a.Description.ToLower().Contains(term)
                || (a.Location != null && a.Location.ToLower().Contains(term))
                || (a.TagsJson != null && a.TagsJson.ToLower().Contains(term))
                || (a.SubcategorySlug != null && a.SubcategorySlug.ToLower().Contains(term)));
        }

        var t0 = terms[0].ToLowerInvariant();
        var t1 = terms[1].ToLowerInvariant();
        if (terms.Count == 2)
        {
            return query.Where(a =>
                a.Title.ToLower().Contains(t0) || a.Description.ToLower().Contains(t0)
                || (a.Location != null && a.Location.ToLower().Contains(t0))
                || (a.TagsJson != null && a.TagsJson.ToLower().Contains(t0))
                || (a.SubcategorySlug != null && a.SubcategorySlug.ToLower().Contains(t0))
                || a.Title.ToLower().Contains(t1) || a.Description.ToLower().Contains(t1)
                || (a.Location != null && a.Location.ToLower().Contains(t1))
                || (a.TagsJson != null && a.TagsJson.ToLower().Contains(t1))
                || (a.SubcategorySlug != null && a.SubcategorySlug.ToLower().Contains(t1)));
        }

        var t2 = terms[2].ToLowerInvariant();
        if (terms.Count == 3)
        {
            return query.Where(a =>
                a.Title.ToLower().Contains(t0) || a.Description.ToLower().Contains(t0)
                || (a.Location != null && a.Location.ToLower().Contains(t0))
                || a.Title.ToLower().Contains(t1) || a.Description.ToLower().Contains(t1)
                || (a.Location != null && a.Location.ToLower().Contains(t1))
                || a.Title.ToLower().Contains(t2) || a.Description.ToLower().Contains(t2)
                || (a.Location != null && a.Location.ToLower().Contains(t2)));
        }

        var t3 = terms[3].ToLowerInvariant();
        return query.Where(a =>
            a.Title.ToLower().Contains(t0) || a.Description.ToLower().Contains(t0)
            || a.Title.ToLower().Contains(t1) || a.Description.ToLower().Contains(t1)
            || a.Title.ToLower().Contains(t2) || a.Description.ToLower().Contains(t2)
            || a.Title.ToLower().Contains(t3) || a.Description.ToLower().Contains(t3));
    }

    private static async Task<List<Advert>> LoadAdvertsWithLocalRankAsync(
        IQueryable<Advert> filtered,
        string query,
        CancellationToken ct)
    {
        var candidates = await filtered
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .ToListAsync(ct);

        var rankedIds = SearchMatchHelper.RankAdvertIds(query, candidates).Take(RetrieveCap).ToList();
        if (rankedIds.Count == 0)
            return [];

        var byId = candidates.ToDictionary(a => a.Id);
        return rankedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private static async Task<List<Discussion>> LoadDiscussionsWithLocalRankAsync(
        IQueryable<Discussion> filtered,
        string query,
        CancellationToken ct)
    {
        var candidates = await filtered
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .ToListAsync(ct);

        var rankedIds = SearchMatchHelper.RankDiscussionIds(query, candidates).Take(RetrieveCap).ToList();
        if (rankedIds.Count == 0)
            return [];

        var byId = candidates.ToDictionary(d => d.Id);
        return rankedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private static async Task<List<Advert>> LoadAdvertsByIdsAsync(
        KinshoutDbContext db,
        IReadOnlyList<Guid> orderedIds,
        CancellationToken ct)
    {
        var adverts = await db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .Where(a => orderedIds.Contains(a.Id))
            .ToListAsync(ct);

        var byId = adverts.ToDictionary(a => a.Id);
        return orderedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private static async Task<List<Discussion>> LoadDiscussionsByIdsAsync(
        KinshoutDbContext db,
        IReadOnlyList<Guid> orderedIds,
        CancellationToken ct)
    {
        var discussions = await db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .Where(d => orderedIds.Contains(d.Id))
            .ToListAsync(ct);

        var byId = discussions.ToDictionary(d => d.Id);
        return orderedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private static async Task<List<Guid>> LoadAdvertIdsByFullTextAsync(
        KinshoutDbContext db,
        IQueryable<Advert> filtered,
        string query,
        IReadOnlyList<string> terms,
        CancellationToken ct)
    {
        var containsExpression = BuildContainsExpression(query, terms);
        if (containsExpression is null)
            return [];

        var rankedIds = await QueryFullTextIdsAsync(
            db,
            """
            SELECT TOP (@cap) ft.[KEY] AS Id
            FROM CONTAINSTABLE(Adverts, (Title, Description, Location, TagsJson), @search) AS ft
            INNER JOIN Adverts a ON a.Id = ft.[KEY]
            WHERE a.IsPublished = 1
            ORDER BY ft.RANK DESC, ft.[KEY]
            """,
            containsExpression,
            ct);

        if (rankedIds.Count == 0)
            return [];

        var allowedIds = await filtered
            .Where(a => rankedIds.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync(ct);

        var allowed = allowedIds.ToHashSet();
        return rankedIds.Where(allowed.Contains).Take(RetrieveCap).ToList();
    }

    private static async Task<List<Guid>> LoadDiscussionIdsByFullTextAsync(
        KinshoutDbContext db,
        IQueryable<Discussion> filtered,
        string query,
        IReadOnlyList<string> terms,
        CancellationToken ct)
    {
        var containsExpression = BuildContainsExpression(query, terms);
        if (containsExpression is null)
            return [];

        var rankedIds = await QueryFullTextIdsAsync(
            db,
            """
            SELECT TOP (@cap) ft.[KEY] AS Id
            FROM CONTAINSTABLE(Discussions, (Title, Body), @search) AS ft
            ORDER BY ft.RANK DESC, ft.[KEY]
            """,
            containsExpression,
            ct);

        if (rankedIds.Count == 0)
            return [];

        var allowedIds = await filtered
            .Where(d => rankedIds.Contains(d.Id))
            .Select(d => d.Id)
            .ToListAsync(ct);

        var allowed = allowedIds.ToHashSet();
        return rankedIds.Where(allowed.Contains).Take(RetrieveCap).ToList();
    }

    private static async Task<List<Guid>> QueryFullTextIdsAsync(
        KinshoutDbContext db,
        string sql,
        string containsExpression,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        AddParameter(cmd, "@cap", RetrieveCap);
        AddParameter(cmd, "@search", containsExpression);

        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetGuid(0));

        return ids;
    }

    private static string? BuildContainsExpression(string query, IReadOnlyList<string> terms)
    {
        if (terms.Count > 0)
        {
            var parts = terms
                .Select(EscapeContainsTerm)
                .Where(part => part.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return parts.Count == 0 ? null : string.Join(" OR ", parts.Select(part => $"\"{part}\""));
        }

        var normalized = SearchTextNormalizer.Normalize(query);
        if (normalized.Length < 2)
            return null;

        return $"\"{EscapeContainsTerm(normalized)}\"";
    }

    private static string EscapeContainsTerm(string term) =>
        term.Replace("\"", "\"\"", StringComparison.Ordinal);

    private static async Task<bool> IsFullTextAvailableAsync(
        KinshoutDbContext db,
        IMemoryCache? cache,
        CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
            return false;

        if (cache is not null
            && cache.TryGetValue(FullTextAvailabilityCacheKey, out bool cached))
        {
            return cached;
        }

        var available = await CheckFullTextAvailableAsync(db, ct);
        cache?.Set(FullTextAvailabilityCacheKey, available, FullTextAvailabilityCacheDuration);
        return available;
    }

    private static async Task<bool> CheckFullTextAvailableAsync(KinshoutDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM sys.fulltext_indexes
            WHERE object_id = OBJECT_ID('Adverts')
            """;
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        cmd.Parameters.Add(param);
    }
}
