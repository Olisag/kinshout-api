using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface ISearchService
{
    Task<SearchResultDto> SearchAsync(SearchRequestDto request, CancellationToken ct = default);
    Task<CategorizeResponseDto> CategorizeAsync(string text, CancellationToken ct = default);
}

public class SearchService(KinshoutDbContext db, IOpenAiService openAi) : ISearchService
{
    public async Task<SearchResultDto> SearchAsync(SearchRequestDto request, CancellationToken ct = default)
    {
        var query = request.Query.Trim();
        var adverts = await db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .Where(a => a.IsPublished)
            .OrderByDescending(a => a.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        var discussions = await db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .Include(d => d.Replies)
            .OrderByDescending(d => d.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        var analysis = await openAi.SearchAsync(query, adverts, discussions, ct);

        var advertResults = adverts
            .Where(a => analysis.AdvertIds.Contains(a.Id))
            .Select(AdvertService.ToDto)
            .ToList();

        var discussionResults = discussions
            .Where(d => analysis.DiscussionIds.Contains(d.Id))
            .Select(ToDiscussionDto)
            .ToList();

        if (request.Tab.Equals("annonces", StringComparison.OrdinalIgnoreCase))
            discussionResults = [];
        else if (request.Tab.Equals("discussions", StringComparison.OrdinalIgnoreCase))
            advertResults = [];

        return new SearchResultDto(advertResults, discussionResults, analysis.Summary);
    }

    public async Task<CategorizeResponseDto> CategorizeAsync(string text, CancellationToken ct = default)
    {
        var categories = await db.Categories.AsNoTracking().ToListAsync(ct);
        var beforeCount = categories.Count;
        var analysis = await openAi.AnalyzeAdvertAsync(text, categories, ct);

        var categoryCreated = false;
        if (analysis.CreateNewCategory && categories.All(c => c.Slug != analysis.CategorySlug))
        {
            var created = await CategoryResolver.ResolveOrCreateCategoryAsync(db, analysis, ct);
            analysis = analysis with { CategorySlug = created.Slug, CategoryLabel = created.Label, CategoryIcon = created.Icon };
            categoryCreated = true;
        }

        var intentLabel = analysis.Intent switch
        {
            "offre" => "Offre — le client vend ou propose",
            "discussion" => "Discussion communautaire",
            _ => "Demande — le client cherche",
        };

        return new CategorizeResponseDto(
            analysis.CategorySlug,
            analysis.CategoryLabel,
            analysis.CategoryIcon,
            analysis.Intent,
            intentLabel,
            analysis.Confidence,
            analysis.Summary,
            categoryCreated || beforeCount < categories.Count ? "openai" : "openai",
            categoryCreated
        );
    }

    private static DiscussionDto ToDiscussionDto(Discussion d) =>
        new(
            d.Id,
            d.Title,
            d.Body,
            d.User.DisplayName,
            TimeHelpers.Initials(d.User.DisplayName),
            d.Replies.Count,
            TimeHelpers.FormatRelative(d.CreatedAt),
            d.Category?.Slug
        );
}
