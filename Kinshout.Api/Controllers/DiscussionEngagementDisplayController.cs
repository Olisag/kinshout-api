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
