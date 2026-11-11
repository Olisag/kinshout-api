using System.Security.Cryptography;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public sealed partial class DiscussionSeedingPipeline
{
    private static readonly (string Slug, string Name, string[] Topics)[] CommunityThemes =
    [
        ("vie-a-kin", "La vie à Kinshasa", ["les marchés de quartier", "les sorties en famille", "les transports du quotidien", "les coins pour se promener", "les activités pour les enfants", "les bibliothèques et lieux de lecture"]),
        ("diaspora", "Congolais de la diaspora", ["les rencontres entre familles", "la transmission du lingala", "les associations culturelles", "les retrouvailles pendant les vacances", "les échanges entre générations", "les liens avec le quartier d’origine"]),
        ("cuisine", "Cuisine congolaise", ["le pondu", "les mikatés", "le liboké", "les repas de fête", "les épices du marché", "les recettes transmises en famille"]),
        ("culture", "Culture congolaise", ["la rumba", "les ateliers de danse", "les livres congolais", "les contes pour enfants", "les concerts de quartier", "les artistes de la diaspora"]),
        ("langues", "Langues du Congo", ["les expressions en lingala", "les histoires en famille", "les chansons pour apprendre", "les jeux de vocabulaire", "les échanges entre langues", "les groupes de conversation"]),
        ("sports", "Sports et loisirs à Kinshasa", ["le football de quartier", "les clubs pour enfants", "les sorties à vélo", "les activités du week-end", "les terrains de basket", "les rencontres sportives de la diaspora"]),
    ];

    private static readonly (string Title, string Question)[] DiscussionPrompts =
    [
        ("Vos bonnes adresses pour {0}", "Quels endroits ou groupes recommanderiez-vous à quelqu’un qui découvre le sujet ?"),
        ("Vos souvenirs autour de {0}", "Quel souvenir ou quelle tradition aimeriez-vous transmettre à la prochaine génération ?"),
        ("Idées en famille autour de {0}", "Quelles idées simples peut-on essayer avec des enfants ou des proches ce week-end ?"),
        ("Par où commencer avec {0} ?", "Quel conseil pratique donneriez-vous à une personne qui aimerait s’y mettre ?"),
        ("Kinshasa et la diaspora : {0}", "Quelles habitudes retrouvez-vous dans les deux contextes ? Qu’est-ce qui change selon la ville ?"),
        ("Vos découvertes autour de {0}", "Quelle ressource, activité ou initiative locale mériterait d’être mieux connue ?"),
    ];

    public async Task<int> PublishSimulationCommunityContentAsync(CancellationToken ct = default)
    {
        var config = settings.Value;
        if (!config.Enabled || !config.SimulationEnabled || !ModerationReady
            || config.WeeklyPublicCommunities <= 0 || config.DailyDiscussionPosts <= 0) return 0;

        await using var read = new KinshoutDbContext(options);
        var run = await read.DiscussionSeedRuns.Where(r => r.StoppedAt == null)
            .OrderByDescending(r => r.CreatedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        if (run is not Guid runId || !await CanPublishCommunityContentAsync(read, runId, ct)) return 0;

        var communities = await read.Communities.AsNoTracking()
            .Where(c => c.CreatedByUser.SeedRunId == runId)
            .OrderBy(c => c.Slug).ToListAsync(ct);
        var nextTheme = CommunityThemes.FirstOrDefault(t => communities.All(c => c.Slug != CommunitySlug(runId, t.Slug)));
        var now = Now;
        // Monday UTC anchors the community creation limit across worker restarts.
        var weekStart = now.Date.AddDays(-((int)now.DayOfWeek + 6) % 7);
        if (nextTheme.Slug is not null
            && communities.Count(c => c.CreatedAt >= weekStart) < Math.Clamp(config.WeeklyPublicCommunities, 1, 2))
        {
            var slug = CommunitySlug(runId, nextTheme.Slug);
            var name = "Simulation · " + nextTheme.Name;
            var description = "Communauté de démonstration Kinoiserie. Les publications des profils Simulation sont générées pour la simulation.";
            if (!await ModerateCommunityContentAsync(name + "\n" + description, ct)) return 0;
            return await LockedAsync(async db =>
            {
                if (!await CanPublishCommunityContentAsync(db, runId, ct)
                    || await db.Communities.AnyAsync(c => c.Slug == slug, ct)
                    || await db.Communities.CountAsync(c => c.CreatedByUser.SeedRunId == runId && c.CreatedAt >= weekStart, ct)
                        >= Math.Clamp(config.WeeklyPublicCommunities, 1, 2)) return 0;
                var creatorId = await PickActiveProfileAsync(db, runId, ct);
                if (creatorId is null) return 0;
                var community = new Community { Slug = slug, Name = name, Description = description, CreatedByUserId = creatorId.Value, CreatedAt = Now };
                db.Communities.Add(community);
                db.CommunityMembers.Add(new() { CommunityId = community.Id, UserId = creatorId.Value,
                    Role = CommunityMemberRoles.Creator, Status = CommunityMemberStatuses.Approved });
                await db.SaveChangesAsync(ct);
                return 1;
            }, ct);
        }

        var available = communities.Where(c => c.IsActive && c.Visibility == CommunityVisibilities.Public).ToList();
        if (available.Count == 0) return 0;
        var posts = read.Discussions.Where(d => d.User.SeedRunId == runId && d.Community != null
            && d.Community.CreatedByUser.SeedRunId == runId);
        var daily = Math.Clamp(config.DailyDiscussionPosts, 1, 20);
        var today = await posts.CountAsync(d => d.CreatedAt >= now.Date, ct);
        if (today >= DueInteractions(now, daily)) return 0;
        var ordinal = (int)(now.Date - DateTime.UnixEpoch).TotalDays * daily + today;
        var communityForPost = available[ordinal % available.Count];
        var theme = CommunityThemes.Single(t => communityForPost.Slug == CommunitySlug(runId, t.Slug));
        var topicIndex = ordinal / available.Count;
        var topic = theme.Topics[topicIndex % theme.Topics.Length];
        var prompt = DiscussionPrompts[topicIndex / theme.Topics.Length % DiscussionPrompts.Length];
        var title = "Simulation · " + string.Format(System.Globalization.CultureInfo.InvariantCulture, prompt.Title, topic);
        var body = $"Parlons de ce sujet : {topic}. {prompt.Question}";
        if (!await ModerateCommunityContentAsync(title + "\n" + body, ct)) return 0;

        return await LockedAsync(async db =>
        {
            if (!await CanPublishCommunityContentAsync(db, runId, ct) || Now.Date != now.Date
                || !await db.Communities.AnyAsync(c => c.Id == communityForPost.Id && c.IsActive && c.Visibility == CommunityVisibilities.Public, ct)
                || await db.Discussions.CountAsync(d => d.CreatedAt >= now.Date && d.User.SeedRunId == runId
                    && d.Community != null && d.Community.CreatedByUser.SeedRunId == runId, ct) != today) return 0;
            var authorId = await PickActiveProfileAsync(db, runId, ct);
            if (authorId is null) return 0;
            var post = new Discussion { UserId = authorId.Value, CommunityId = communityForPost.Id,
                Title = title, Body = body, CreatedAt = Now, UpdatedAt = Now };
            PoliticsFeedFilter.Mark(post);
            if (post.PublicFeedBlocked) return 0;
            db.Discussions.Add(post);
            db.DiscussionParticipants.Add(new() { DiscussionId = post.Id, UserId = authorId.Value,
                Status = CommunityMemberStatuses.Approved, ReviewedAt = Now, ReviewedByUserId = authorId.Value });
            await db.SaveChangesAsync(ct);
            return 1;
        }, ct);
    }

    private static string CommunitySlug(Guid runId, string theme) => $"simulation-{runId:N}-{theme}";

    private static async Task<Guid?> PickActiveProfileAsync(KinshoutDbContext db, Guid runId, CancellationToken ct)
    {
        var ids = await db.Users.Where(u => u.SeedRunId == runId && u.SeedHiddenAt == null).Select(u => u.Id).ToListAsync(ct);
        return ids.Count == 0 ? null : ids[RandomNumberGenerator.GetInt32(ids.Count)];
    }

    private static async Task<bool> CanPublishCommunityContentAsync(KinshoutDbContext db, Guid runId, CancellationToken ct) =>
        await CanGenerateAsync(db, runId, ct)
        && await db.DiscussionSeedingControls.AnyAsync(c => c.Id == 1 && c.PublishingEnabled, ct);

    private async Task<bool> ModerateCommunityContentAsync(string text, CancellationToken ct)
    {
        if (DiscussionSeedCommentGenerator.HasPoliticalContent(text)
            || PoliticsFeedFilter.IsBlocked(null, null, text, null, null, null)) return false;
        try
        {
            await moderation.EnsureTextAllowedAsync(text, ct);
            return true;
        }
        catch (AdvertModerationException) { return false; }
    }
}
