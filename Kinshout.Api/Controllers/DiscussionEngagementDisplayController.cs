using Kinshout.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

[ApiController]
[Route("api/discussions/engagement-display")]
public sealed class DiscussionEngagementDisplayController(IDiscussionSeedingService seeding) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(new { combinedLikesEnabled = (await seeding.GetFlagsAsync(ct)).CombinedLikesEnabled });
}
