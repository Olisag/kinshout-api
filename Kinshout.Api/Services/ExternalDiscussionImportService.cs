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

    Task<RetransformExternalDiscussionsResponseDto> RetransformAllAsync(
        bool force = false,
        CancellationToken ct = default);
}

public class ExternalDiscussionImportService(
    KinshoutDbContext db,
    IExternalDiscussionTransformService transform) : IExternalDiscussionImportService
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

    public async Task<RetransformExternalDiscussionsResponseDto> RetransformAllAsync(
        bool force = false,
        CancellationToken ct = default)
    {
        var discussions = await db.Discussions
            .Where(d => d.SourceProvider != null
                && d.SourceExternalId != null
                && d.SourceProvider != DiscussionSourceProvider.Kinshout)
            .ToListAsync(ct);

        var transformed = 0;
        var unchanged = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var discussion in discussions)
        {
            try
            {
                var raw = discussion.SourceRawBody?.Trim() ?? discussion.Body.Trim();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    skipped++;
                    continue;
                }

                if (!force
                    && !string.IsNullOrWhiteSpace(discussion.SourceRawBody)
                    && discussion.Title.Length > 0
                    && !LooksLikeRawPost(discussion))
                {
                    unchanged++;
                    continue;
                }

                var result = await transform.TransformAsync(
                    raw,
                    discussion.SourceOriginalAuthor,
                    discussion.SourceProviderName ?? DiscussionSourceProvider.DisplayName(discussion.SourceProvider!),
                    ct);

                discussion.SourceRawBody = raw;
                discussion.Title = result.Title;
                discussion.Body = result.Body;
                discussion.UpdatedAt = DateTime.UtcNow;
                transformed++;
            }
            catch
            {
                failed++;
                db.ChangeTracker.Clear();
            }
        }

        if (transformed > 0)
            await db.SaveChangesAsync(ct);

        return new RetransformExternalDiscussionsResponseDto(transformed, unchanged, skipped, failed);
    }

    /// <summary>Heuristic: title still looks like a raw headline (emoji, hashtag, ALL CAPS burst).</summary>
    private static bool LooksLikeRawPost(Discussion discussion)
    {
        var title = discussion.Title;
        if (title.Contains('#')
            || title.Contains("🔴", StringComparison.Ordinal)
            || title.Contains("🚨", StringComparison.Ordinal))
            return true;
        if (title.Length > 90)
            return true;
        return title.Count(char.IsUpper) > 12 && title.Length < 80;
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

        if (string.IsNullOrWhiteSpace(item.Body))
            throw new ArgumentException("body requis.");

        var rawBody = item.Body.Trim();
        var engagement = item.EngagementScore;

        if (existing is not null
            && DiscussionSourceMapper.IsSameContent(existing, rawBody, engagement)
            && existing.SourceLastSeenAt >= (item.Source.LastSeenAt ?? DateTime.UtcNow).AddMinutes(-1))
        {
            existing.SourceLastSeenAt = item.Source.LastSeenAt ?? DateTime.UtcNow;
            return UpsertOutcome.Unchanged;
        }

        var platformName = item.Source.ProviderName ?? DiscussionSourceProvider.DisplayName(provider);
        var transformed = await transform.TransformAsync(rawBody, item.OriginalAuthor, platformName, ct);

        var now = DateTime.UtcNow;
        var importedAt = item.Source.ImportedAt ?? now;
        var lastSeenAt = item.Source.LastSeenAt ?? importedAt;
        var firstSeenAt = item.Source.FirstSeenAt ?? importedAt;
        var mapped = MapFields(
            item,
            provider,
            importUser.Id,
            discussionCategory.Id,
            importedAt,
            lastSeenAt,
            firstSeenAt,
            transformed.Title,
            transformed.Body,
            rawBody);

        if (existing is null)
        {
            db.Discussions.Add(mapped);
            return UpsertOutcome.Created;
        }

        existing.Title = mapped.Title;
        existing.Body = mapped.Body;
        existing.SourceRawBody = rawBody;
        existing.SourceProviderName = mapped.SourceProviderName;
        existing.SourceExternalUrl = mapped.SourceExternalUrl;
        existing.SourceImportedAt = existing.SourceImportedAt ?? importedAt;
        existing.SourceLastSeenAt = lastSeenAt;
        existing.SourceFirstSeenAt = existing.SourceFirstSeenAt ?? firstSeenAt;
        existing.SourceOriginalAuthor = mapped.SourceOriginalAuthor;
        existing.SourceEngagementScore = mapped.SourceEngagementScore;
        existing.ExternalPublishedAt = mapped.ExternalPublishedAt;
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
        DateTime firstSeenAt,
        string title,
        string body,
        string rawBody)
    {
        var publishedAt = item.PublishedAt?.ToUniversalTime();
        var engagement = item.EngagementScore.GetValueOrDefault();
        var createdAt = publishedAt ?? importedAt;

        return new Discussion
        {
            UserId = userId,
            CategoryId = categoryId,
            Title = title.Trim(),
            Body = body.Trim(),
            SourceRawBody = rawBody,
            CreatedAt = createdAt,
            UpdatedAt = importedAt,
            ReplyCount = 0,
            LikeCount = 0,
            ViewCount = 0,
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
