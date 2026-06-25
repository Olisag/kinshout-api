using System.Security.Claims;

namespace Kinshout.Api.Controllers;

internal static class ControllerUserHelper
{
    internal static Guid? TryGetUserId(HttpContext http)
    {
        var raw = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? http.User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var userId) ? userId : null;
    }
}
