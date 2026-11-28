using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services.WhatsApp;

public interface IKinshasaEventSource
{
    Task<IReadOnlyList<KinshasaEvent>> UpcomingAsync(CancellationToken ct);
}

/// <summary>No structured event store exists yet. The slot stays quiet instead of guessing.</summary>
public sealed class EmptyKinshasaEventSource : IKinshasaEventSource
{
    public Task<IReadOnlyList<KinshasaEvent>> UpcomingAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<KinshasaEvent>>([]);
}

public sealed class EditorialCandidateSource(KinshoutDbContext db, IOptions<WhatsAppPublishingSettings> settings)
{
    private static readonly string[] EssentielTopics = ["ville", "education"];
    private static readonly string[] BuzzTopics = ["influence"];
    private static readonly string[] InsoliteTopics = ["humour"];
    private static readonly string[] MusicTopics = ["musique", "classiques", "culture", "sport"];
    private static readonly string[] PrideTopics = ["diaspora-fr", "diaspora-us", "diaspora-ca", "diaspora-za"];

    public async Task<EditorialStory?> NextAsync(string category, DateTime utcNow, CancellationToken ct)
    {
        var topics = Topics(category);
        var window = Window(category);
        if (topics.Length == 0)
            return null;
        var stories = await EligibleAsync(utcNow, window, allowPreviouslyPublished: false, ct);
        return stories.FirstOrDefault(s => s.TopicSlug is not null && topics.Contains(s.TopicSlug));
    }

    public async Task<IReadOnlyList<EditorialStory>> HighlightsAsync(DateTime utcNow, CancellationToken ct)
    {
        var stories = await EligibleAsync(utcNow, TimeSpan.FromDays(7), allowPreviouslyPublished: true, ct);
        return stories
            .GroupBy(s => s.TopicSlug ?? s.Id.ToString())
            .Select(g => g.First())
            .Take(5)
            .ToList();
    }

    public async Task<IReadOnlyCollection<string>> UsedPromptIdsAsync(DateTime utcNow, CancellationToken ct)
    {
        var since = utcNow.AddDays(-60);
        return await db.WhatsAppPublications
            .Where(p => p.Category == "interaction" && p.ProviderReference != null && p.CreatedAtUtc >= since)
            .Select(p => p.ProviderReference!)
            .ToListAsync(ct);
    }

    private async Task<List<EditorialStory>> EligibleAsync(DateTime utcNow, TimeSpan window, bool allowPreviouslyPublished, CancellationToken ct)
    {
        var since = utcNow.Subtract(window);
        var duplicateSince = utcNow.AddDays(-Math.Max(1, settings.Value.EditorialDuplicateWindowDays));
        var used = allowPreviouslyPublished
            ? new HashSet<Guid>()
            : (await db.WhatsAppPublications
                .Where(p => p.DiscussionId != null
                    && p.CreatedAtUtc >= duplicateSince
                    && p.Purpose == WhatsAppPublicationPurpose.Publish
                    && p.Status != WhatsAppPublicationStatus.Skipped
                    && p.Status != WhatsAppPublicationStatus.Failed
                    && p.Status != WhatsAppPublicationStatus.Expired
                    && p.Status != WhatsAppPublicationStatus.Cancelled
                    && p.Category != "highlights")
                .Select(p => p.DiscussionId!.Value)
                .ToListAsync(ct)).ToHashSet();

        var rows = await db.Discussions
            .AsNoTracking()
            .Where(d => !d.PublicFeedBlocked)
            .Where(d => d.Visibility == CommunityVisibilities.Public)
            .Where(d => d.TopicSlug != "politique")
            .OrderByDescending(d => d.ExternalPublishedAt ?? d.CreatedAt)
            .Take(200)
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.Body,
                d.TopicSlug,
                d.SourceProvider,
                d.SourceOriginalAuthor,
                d.PublicFeedRuleVersion,
                d.ExternalPublishedAt,
                d.CreatedAt,
                d.SourceEngagementScore,
            })
            .ToListAsync(ct);

        return rows
            .Where(d => !used.Contains(d.Id))
            .Select(d =>
            {
                var external = !string.IsNullOrWhiteSpace(d.SourceProvider)
                    && !d.SourceProvider.Equals(DiscussionSourceProvider.Kinshout, StringComparison.OrdinalIgnoreCase);
                DateTime? published = external ? d.ExternalPublishedAt : d.CreatedAt;
                if (external && d.PublicFeedRuleVersion <= 0)
                    return null;
                if (published is null || published < since || published > utcNow.AddMinutes(5))
                    return null;
                return new EditorialStory(d.Id, d.Title, d.Body, d.SourceOriginalAuthor, d.TopicSlug, published.Value, d.SourceEngagementScore);
            })
            .Where(s => s is not null)
            .Select(s => s!)
            .OrderByDescending(s => s.PublishedAtUtc)
            .ThenByDescending(s => s.SourceEngagement ?? 0)
            .ToList();
    }

    private static string[] Topics(string category) => category switch
    {
        "essentiel" => EssentielTopics,
        "buzz" => BuzzTopics,
        "insolite" => InsoliteTopics,
        "musique" => MusicTopics,
        "fierte" => PrideTopics,
        _ => [],
    };

    private static TimeSpan Window(string category) => category switch
    {
        "essentiel" => TimeSpan.FromHours(24),
        "buzz" => TimeSpan.FromHours(48),
        "insolite" => TimeSpan.FromHours(72),
        _ => TimeSpan.FromDays(7),
    };
}
