using Kinshout.Api.Services;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

[ApiController]
[Route("api/discussions/engagement-display")]
public sealed class DiscussionEngagementDisplayController(IDiscussionSeedingService seeding, IOptions<DiscussionSeedingSettings> settings) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(new { combinedLikesEnabled = settings.Value.Enabled && (await seeding.GetFlagsAsync(ct)).CombinedLikesEnabled });
}
