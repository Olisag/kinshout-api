using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore;
using Kinshout.Api.Services;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

[ApiController]
[Route("api/discussions/engagement-display")]
public sealed class DiscussionEngagementDisplayController(IDiscussionSeedingService seeding, IOptions<DiscussionSeedingSettings> settings, KinshoutDbContext db) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(new { combinedLikesEnabled = settings.Value.Enabled && (await seeding.GetFlagsAsync(ct)).CombinedLikesEnabled });
    [HttpGet("simulation")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Simulation(CancellationToken ct) => Ok(new
    {
        label = "Simulated engagement; excluded from genuine traction",
        enabled = settings.Value.Enabled && settings.Value.SimulationEnabled,
        flags = await seeding.GetFlagsAsync(ct),
        targets = new { settings.Value.ProfileCount, settings.Value.DailyComments, settings.Value.DailyLikes, settings.Value.DailyDiscussionPosts, settings.Value.WeeklyPublicCommunities },
        legacyProfileNames = await db.Users.CountAsync(u => u.SeedRunId != null && u.DisplayName.StartsWith("Simulation_"), ct),
        legacyCommunityNames = await db.Communities.CountAsync(c => c.CreatedByUser.SeedRunId != null && (c.Name.StartsWith("Simulation · ") || c.Slug.StartsWith("simulation-")), ct),
        legacyDiscussionTitles = await DiscussionSeedingPipeline.LegacySeededDiscussionTitles(db).CountAsync(ct),
        archiveSeededCommunityContent = settings.Value.ArchiveSeededCommunityContent,
        pendingSeededCommunityCleanup = settings.Value.ArchiveSeededCommunityContent
            ? await DiscussionSeedingService.PendingCommunityCleanupAsync(db, ct) : (int?)null,
        visibleSeededDiscussionPosts = await db.Discussions.CountAsync(d => d.User.SeedRunId != null
            && d.Community != null && d.Community.CreatedByUser.SeedRunId == d.User.SeedRunId, ct),
        archivedSeededCommunities = await db.Communities.CountAsync(c => !c.IsActive && c.CreatedByUser.SeedRunId != null, ct),
        archivedSeededDiscussionPosts = await db.Discussions.IgnoreQueryFilters().CountAsync(d =>
            d.Visibility == Kinshout.Api.Models.Discussion.ArchivedSimulationVisibility && d.User.SeedRunId != null, ct),
        simulatedPublicCommunities = await db.Communities.CountAsync(c => c.IsActive && c.Visibility == "public" && c.CreatedByUser.SeedRunId != null, ct),
        simulatedDiscussionPostsToday = await db.Discussions.CountAsync(d => d.User.SeedRunId != null && d.CreatedAt >= DateTime.UtcNow.Date, ct),
        activeRuns = await db.DiscussionSeedRuns.CountAsync(r => r.StoppedAt == null, ct),
        activeSimulatedProfiles = await db.Users.CountAsync(u => u.SeedRunId != null && u.SeedHiddenAt == null
            && db.DiscussionSeedRuns.Any(r => r.Id == u.SeedRunId && r.StoppedAt == null), ct),
        simulatedCommentsToday = await db.DiscussionReplies.CountAsync(r => r.SeedRunId != null && r.CreatedAt >= DateTime.UtcNow.Date, ct),
        simulatedLikesToday = await db.DiscussionSimulatedLikes.CountAsync(l => l.HiddenAt == null && l.CreatedAt >= DateTime.UtcNow.Date, ct),
        lastPublishedComment = await db.DiscussionSeedJobs.MaxAsync(j => j.PublishedAt, ct),
        lastPublishedLike = await db.DiscussionSimulatedLikes.MaxAsync(l => (DateTime?)l.CreatedAt, ct),
        heartbeatAt = await db.DiscussionSimulationDays.MaxAsync(d => d.HeartbeatAt, ct),
    });
}
