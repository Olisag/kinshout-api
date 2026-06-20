using System.Text.Json;
using System.Text.Json.Serialization;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public record FacebookUserInfo(string Id, string? Email, string Name, string? PictureUrl);

public interface IFacebookAuthValidator
{
    Task<FacebookUserInfo> ValidateAccessTokenAsync(string accessToken, CancellationToken ct = default);
}

public class FacebookGraphAuthValidator(
    IHttpClientFactory httpClientFactory,
    IOptions<OAuthSettings> oauthOptions,
    ILogger<FacebookGraphAuthValidator> logger) : IFacebookAuthValidator
{
    private readonly FacebookOAuthSettings _facebook = oauthOptions.Value.Facebook;

    public async Task<FacebookUserInfo> ValidateAccessTokenAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new UnauthorizedAccessException("Facebook access token is required.");

        if (!string.IsNullOrWhiteSpace(_facebook.AppId) && !string.IsNullOrWhiteSpace(_facebook.AppSecret))
            await EnsureTokenBelongsToAppAsync(accessToken, ct);
        else
            logger.LogWarning("Facebook AppId/AppSecret not configured — skipping app token validation (dev only).");

        return await FetchUserProfileAsync(accessToken, ct);
    }

    private async Task EnsureTokenBelongsToAppAsync(string accessToken, CancellationToken ct)
    {
        var appToken = $"{_facebook.AppId}|{_facebook.AppSecret}";
        var url =
            $"https://graph.facebook.com/debug_token?input_token={Uri.EscapeDataString(accessToken)}&access_token={Uri.EscapeDataString(appToken)}";

        using var client = httpClientFactory.CreateClient();
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<FacebookDebugTokenResponse>(stream, cancellationToken: ct)
            ?? throw new UnauthorizedAccessException("Invalid Facebook token response.");

        if (payload.Data is null
            || !payload.Data.IsValid
            || !string.Equals(payload.Data.AppId, _facebook.AppId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Invalid Facebook access token.");
        }
    }

    private async Task<FacebookUserInfo> FetchUserProfileAsync(string accessToken, CancellationToken ct)
    {
        var url =
            $"https://graph.facebook.com/me?fields=id,name,email,picture.type(large)&access_token={Uri.EscapeDataString(accessToken)}";

        using var client = httpClientFactory.CreateClient();
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var profile = await JsonSerializer.DeserializeAsync<FacebookProfileResponse>(stream, cancellationToken: ct)
            ?? throw new UnauthorizedAccessException("Unable to read Facebook profile.");

        if (string.IsNullOrWhiteSpace(profile.Id))
            throw new UnauthorizedAccessException("Facebook profile missing id.");

        var email = string.IsNullOrWhiteSpace(profile.Email)
            ? $"{profile.Id}@facebook.kinshout"
            : profile.Email;

        return new FacebookUserInfo(
            profile.Id,
            email,
            string.IsNullOrWhiteSpace(profile.Name) ? "Utilisateur Facebook" : profile.Name,
            profile.Picture?.Data?.Url);
    }

    private sealed class FacebookDebugTokenResponse
    {
        [JsonPropertyName("data")]
        public FacebookDebugTokenData? Data { get; set; }
    }

    private sealed class FacebookDebugTokenData
    {
        [JsonPropertyName("app_id")]
        public string? AppId { get; set; }

        [JsonPropertyName("is_valid")]
        public bool IsValid { get; set; }
    }

    private sealed class FacebookProfileResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("picture")]
        public FacebookPictureWrapper? Picture { get; set; }
    }

    private sealed class FacebookPictureWrapper
    {
        [JsonPropertyName("data")]
        public FacebookPictureData? Data { get; set; }
    }

    private sealed class FacebookPictureData
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
