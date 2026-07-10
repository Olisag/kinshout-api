using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

/// <summary>
/// Accent-insensitive SQL text matching for search prefilters.
/// SQL Server uses CI_AI collation; other providers fall back to simple contains or skip
/// broader prefilters so in-memory ranking can use normalized text.
/// </summary>
internal static class SearchDbTextFilter
{
    private const string AccentInsensitiveCollation = "Latin1_General_CI_AI";

    public static bool SupportsAccentInsensitiveSql(KinshoutDbContext db) =>
        db.Database.IsSqlServer();

    public static IQueryable<Discussion> WhereTitleOrBodyContains(
        IQueryable<Discussion> query,
        KinshoutDbContext db,
        string normalizedTerm)
    {
        if (!SupportsAccentInsensitiveSql(db))
            return query;

        var term = normalizedTerm.ToLowerInvariant();
        return query.Where(d =>
            EF.Functions.Collate(d.Title, AccentInsensitiveCollation).Contains(term)
            || EF.Functions.Collate(d.Body, AccentInsensitiveCollation).Contains(term));
    }

    public static IQueryable<Advert> WhereAdvertLocationContains(
        IQueryable<Advert> query,
        KinshoutDbContext db,
        string normalizedTerm)
    {
        var term = normalizedTerm.ToLowerInvariant();
        if (SupportsAccentInsensitiveSql(db))
        {
            return query.Where(a =>
                a.Location != null && EF.Functions.Collate(a.Location, AccentInsensitiveCollation).Contains(term));
        }

        return query.Where(a =>
            a.Location != null && a.Location.ToLower().Contains(term));
    }

    public static IQueryable<Advert> WhereAdvertTextContains(
        IQueryable<Advert> query,
        KinshoutDbContext db,
        string normalizedTerm)
    {
        var term = normalizedTerm.ToLowerInvariant();
        if (SupportsAccentInsensitiveSql(db))
        {
            return query.Where(a =>
                EF.Functions.Collate(a.Title, AccentInsensitiveCollation).Contains(term)
                || EF.Functions.Collate(a.Description, AccentInsensitiveCollation).Contains(term)
                || (a.Location != null && EF.Functions.Collate(a.Location, AccentInsensitiveCollation).Contains(term))
                || (a.TagsJson != null && EF.Functions.Collate(a.TagsJson, AccentInsensitiveCollation).Contains(term))
                || (a.SubcategorySlug != null && EF.Functions.Collate(a.SubcategorySlug, AccentInsensitiveCollation).Contains(term)));
        }

        return query.Where(a =>
            a.Title.ToLower().Contains(term)
            || a.Description.ToLower().Contains(term)
            || (a.Location != null && a.Location.ToLower().Contains(term))
            || (a.TagsJson != null && a.TagsJson.ToLower().Contains(term))
            || (a.SubcategorySlug != null && a.SubcategorySlug.ToLower().Contains(term)));
    }
}
