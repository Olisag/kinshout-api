namespace Kinshout.Api.Auth;

public static class AuthConstants
{
    public const string ClientTokenHeader = "X-Kinshout-Client-Token";
    public const string TokenTypeClaim = "typ";
    public const string ClientIdClaim = "client_id";
    public const string AppTokenType = "app";
    public const string UserTokenType = "user";
    public const string ClientContextKey = "KinshoutClientId";
    public const string UserPolicy = "KinshoutUser";
}
