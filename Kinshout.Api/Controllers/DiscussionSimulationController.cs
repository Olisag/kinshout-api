using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Controllers;

/// <summary>Explicit disclosure: simulated likes never masquerade as genuine likes.</summary>
[ApiController]
[Route("api/discussions/{id:guid}/simulation")]
public sealed class DiscussionSimulationController(KinshoutDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!await db.Discussions.AnyAsync(d => d.Id == id && d.Visibility == CommunityVisibilities.Public
            && !d.PublicFeedBlocked && (d.Community == null || (d.Community.IsActive && d.Community.Visibility == CommunityVisibilities.Public)), ct))
            return NotFound();
        return Ok(new
        {
            label = "Simulated engagement — excluded from genuine traction",
            simulatedLikes = await db.DiscussionSimulatedLikes.CountAsync(l => l.DiscussionId == id && l.HiddenAt == null, ct),
            simulatedComments = await db.DiscussionReplies.CountAsync(r => r.DiscussionId == id && r.SeedRunId != null, ct),
            genuineComments = await db.DiscussionReplies.CountAsync(r => r.DiscussionId == id && r.SeedRunId == null
                && r.User.SeedRunId == null && !r.User.Email.EndsWith("@kinshout.system"), ct),
            genuineLikes = await db.LikedDiscussions.CountAsync(l => l.DiscussionId == id && l.User.SeedRunId == null, ct),
        });
    }
}
