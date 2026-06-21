namespace Kinshout.Api.Auth;

public static class AuthConstants
{
    public const string ClientTokenHeader = "X-Kinshout-Client-Token";
    /// <summary>Multipart form fallback when IIS strips the client token header alongside Authorization.</summary>
    public const string ClientTokenFormField = "x_kinshout_client_token";
    public const string ClientTokenQueryParam = "clientToken";
    public const string TokenTypeClaim = "typ";
    public const string ClientIdClaim = "client_id";
    public const string AppTokenType = "app";
    public const string UserTokenType = "user";
    public const string ClientContextKey = "KinshoutClientId";
    public const string UserPolicy = "KinshoutUser";
}
