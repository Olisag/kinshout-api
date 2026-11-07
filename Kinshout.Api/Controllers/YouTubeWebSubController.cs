using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

/// <summary>
/// YouTube PubSubHubbub callback. A brand-new upload has no views yet, so it is not imported here.
/// The 6-hour reconcile admits it once it clears the hit floor and the safety filter.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/youtube/websub")]
public class YouTubeWebSubController : ControllerBase
{
    [HttpGet]
    public ContentResult Verify(
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        return Content(challenge ?? "", "text/plain");
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        string body;
        using (var reader = new StreamReader(Request.Body))
            body = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(body))
            return Ok();

        var secret = Environment.GetEnvironmentVariable("YOUTUBE_WEBSUB_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var signature = Request.Headers["X-Hub-Signature"].ToString();
        if (!SignatureMatches(body, secret, signature))
            return Ok();

        return Ok();
    }

    private static bool SignatureMatches(string body, string secret, string signature)
    {
        const string prefix = "sha1=";
        if (!signature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || signature.Length != prefix.Length + 40)
            return false;

        var provided = signature[prefix.Length..];
        var hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        var expected = Convert.ToHexString(hash);
        var providedBytes = Encoding.ASCII.GetBytes(provided.ToUpperInvariant());
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
