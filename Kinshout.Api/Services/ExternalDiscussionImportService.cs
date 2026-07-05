using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IExternalDiscussionImportService
{
    Task<ImportExternalDiscussionsResponseDto> ImportAsync(
        IReadOnlyList<ImportExternalDiscussionDto> discussions,
        CancellationToken ct = default);

    Task<IReadOnlyList<ImportKnownDiscussionKeyDto>> GetKnownDiscussionKeysAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DiscussionImportStateDto>> GetDiscussionImportStateAsync(CancellationToken ct = default);

    Task RecordDiscussionImportRunAsync(string provider, DateTime runAtUtc, CancellationToken ct = default);
}

public class ExternalDiscussionImportService(KinshoutDbContext db) : IExternalDiscussionImportService
{
    public async Task<ImportExternalDiscussionsResponseDto> ImportAsync(
        IReadOnlyList<ImportExternalDiscussionDto> discussions,
        CancellationToken ct = default)
    {
        if (discussions.Count == 0)
            throw new ArgumentException("Aucune discussion à importer.");

        var importUser = await ImportSeed.EnsureImportUserAsync(db, ct);
        var discussionCategory = await EnsureDiscussionCategoryAsync(db, ct);

        var created = 0;
        var updated = 0;
        var unchanged = 0;
        var skipped = 0;

        foreach (var item in discussions)
        {
            try
            {
                var outcome = await UpsertAsync(item, importUser, discussionCategory, ct);
                await db.SaveChangesAsync(ct);
                switch (outcome)
                {
                    case UpsertOutcome.Created: created++; break;
                    case UpsertOutcome.Updated: updated++; break;
                    case UpsertOutcome.Unchanged: unchanged++; break;
                    case UpsertOutcome.Skipped: skipped++; break;
                }
            }
            catch (Exception)
            {
                skipped++;
                db.ChangeTracker.Clear();
            }
        }

        return new ImportExternalDiscussionsResponseDto(created, updated, unchanged, skipped);
    }

    public async Task<IReadOnlyList<ImportKnownDiscussionKeyDto>> GetKnownDiscussionKeysAsync(CancellationToken ct = default)
    {
        return await db.Discussions
            .AsNoTracking()
            .Where(d => d.SourceProvider != null
                && d.SourceExternalId != null
                && d.SourceProvider != DiscussionSourceProvider.Kinshout)
            .Select(d => new ImportKnownDiscussionKeyDto(d.SourceProvider!, d.SourceExternalId!))
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DiscussionImportStateDto>> GetDiscussionImportStateAsync(CancellationToken ct = default)
    {
        const string importKind = "discussion";

        var watermarks = await db.ImportWatermarks
            .AsNoTracking()
            .Where(w => w.ImportKind == importKind)
            .Select(w => new DiscussionImportStateDto(w.Provider, w.LastRunAtUtc))
            .ToListAsync(ct);

        var fromDiscussions = await db.Discussions
            .AsNoTracking()
            .Where(d => d.SourceProvider != null
                && d.SourceExternalId != null
                && d.SourceProvider != DiscussionSourceProvider.Kinshout)
            .GroupBy(d => d.SourceProvider!)
            .Select(g => new DiscussionImportStateDto(
                g.Key,
                g.Max(d => d.SourceLastSeenAt ?? d.SourceImportedAt ?? d.CreatedAt)))
            .ToListAsync(ct);

        return watermarks
            .Concat(fromDiscussions)
            .GroupBy(s => s.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DiscussionImportStateDto(
                g.Key,
                g.Max(s => s.LastRunAtUtc)))
            .OrderBy(s => s.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task RecordDiscussionImportRunAsync(string provider, DateTime runAtUtc, CancellationToken ct = default)
    {
        var normalized = DiscussionSourceProvider.Normalize(provider);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("provider requis.");

        var runAt = runAtUtc.ToUniversalTime();
        var existing = await db.ImportWatermarks
            .FirstOrDefaultAsync(w => w.ImportKind == "discussion" && w.Provider == normalized, ct);

        if (existing is null)
        {
            db.ImportWatermarks.Add(new ImportWatermark
            {
                ImportKind = "discussion",
                Provider = normalized,
                LastRunAtUtc = runAt,
            });
        }
        else
        {
            existing.LastRunAtUtc = runAt;
        }

        await db.SaveChangesAsync(ct);
    }

    private enum UpsertOutcome { Created, Updated, Unchanged, Skipped }

    private async Task<UpsertOutcome> UpsertAsync(
        ImportExternalDiscussionDto item,
        User importUser,
        Category discussionCategory,
        CancellationToken ct)
    {
        var provider = DiscussionSourceProvider.Normalize(item.Source.Provider);
        if (string.IsNullOrWhiteSpace(item.Source.ExternalId))
            throw new ArgumentException("externalId requis.");

        var existing = await db.Discussions
            .FirstOrDefaultAsync(
                d => d.SourceProvider == provider && d.SourceExternalId == item.Source.ExternalId.Trim(),
                ct);

        var isActive = !item.Status.Equals("inactive", StringComparison.OrdinalIgnoreCase)
            && !item.Status.Equals("removed", StringComparison.OrdinalIgnoreCase);

        if (!isActive)
        {
            if (existing is null)
                return UpsertOutcome.Skipped;

            db.Discussions.Remove(existing);
            return UpsertOutcome.Updated;
        }

        if (string.IsNullOrWhiteSpace(item.Source.ExternalUrl))
            throw new ArgumentException("externalUrl requis.");

        if (string.IsNullOrWhiteSpace(item.Title))
            throw new ArgumentException("title requis.");

        if (string.IsNullOrWhiteSpace(item.Body))
            throw new ArgumentException("body requis.");

        var now = DateTime.UtcNow;
        var importedAt = item.Source.ImportedAt ?? now;
        var lastSeenAt = item.Source.LastSeenAt ?? importedAt;
        var firstSeenAt = item.Source.FirstSeenAt ?? importedAt;
        var mapped = MapFields(item, provider, importUser.Id, discussionCategory.Id, importedAt, lastSeenAt, firstSeenAt);

        if (existing is null)
        {
            db.Discussions.Add(mapped);
            return UpsertOutcome.Created;
        }

        if (DiscussionSourceMapper.IsSameContent(existing, mapped)
            && existing.SourceLastSeenAt >= lastSeenAt.AddMinutes(-1))
        {
            existing.SourceLastSeenAt = lastSeenAt;
            return UpsertOutcome.Unchanged;
        }

        existing.Title = mapped.Title;
        existing.Body = mapped.Body;
        existing.SourceProviderName = mapped.SourceProviderName;
        existing.SourceExternalUrl = mapped.SourceExternalUrl;
        existing.SourceImportedAt = existing.SourceImportedAt ?? importedAt;
        existing.SourceLastSeenAt = lastSeenAt;
        existing.SourceFirstSeenAt = existing.SourceFirstSeenAt ?? firstSeenAt;
        existing.SourceOriginalAuthor = mapped.SourceOriginalAuthor;
        existing.SourceEngagementScore = mapped.SourceEngagementScore;
        existing.ExternalPublishedAt = mapped.ExternalPublishedAt;
        existing.ViewCount = Math.Max(existing.ViewCount, mapped.ViewCount);
        existing.UpdatedAt = now;

        return UpsertOutcome.Updated;
    }

    private static Discussion MapFields(
        ImportExternalDiscussionDto item,
        string provider,
        Guid userId,
        Guid categoryId,
        DateTime importedAt,
        DateTime lastSeenAt,
        DateTime firstSeenAt)
    {
        var publishedAt = item.PublishedAt?.ToUniversalTime();
        var engagement = item.EngagementScore.GetValueOrDefault();
        var createdAt = publishedAt ?? importedAt;

        return new Discussion
        {
            UserId = userId,
            CategoryId = categoryId,
            Title = item.Title.Trim(),
            Body = item.Body.Trim(),
            CreatedAt = createdAt,
            UpdatedAt = importedAt,
            ReplyCount = 0,
            LikeCount = 0,
            ViewCount = Math.Max(engagement, 0),
            SourceProvider = provider,
            SourceProviderName = string.IsNullOrWhiteSpace(item.Source.ProviderName)
                ? DiscussionSourceProvider.DisplayName(provider)
                : item.Source.ProviderName.Trim(),
            SourceExternalId = item.Source.ExternalId.Trim(),
            SourceExternalUrl = item.Source.ExternalUrl.Trim(),
            SourceImportedAt = importedAt,
            SourceLastSeenAt = lastSeenAt,
            SourceFirstSeenAt = firstSeenAt,
            SourceOriginalAuthor = Clean(item.OriginalAuthor),
            SourceEngagementScore = engagement > 0 ? engagement : null,
            ExternalPublishedAt = publishedAt,
        };
    }

    private static async Task<Category> EnsureDiscussionCategoryAsync(KinshoutDbContext db, CancellationToken ct)
    {
        var category = await db.Categories
            .FirstOrDefaultAsync(c => c.Slug == Category.DiscussionSlug, ct);

        if (category is not null)
            return category;

        category = new Category
        {
            Slug = Category.DiscussionSlug,
            Label = "Discussions",
            Icon = "💬",
            IsSystem = true,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        return category;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
